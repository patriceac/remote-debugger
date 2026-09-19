import { DurableObject } from "cloudflare:workers";

type RelayEnv = Env & { ACCESS_KEY?: string; PROTECTED_ACCESS_KEY?: string };
type SocketState = {
  role: "agent" | "controller" | "stream";
  channel: string;
  created: number;
  window: number;
  bytes: number;
  generation: string;
  name?: string;
  fingerprint?: string;
  scope?: string;
};
const unavailable = () => new Response("Session unavailable", { status: 404 });
const validId = (s: string) => /^[A-F0-9]{16}$/.test(s);
const validKey = (s: string) => /^[a-fA-F0-9]{64}$/.test(s);
const hex = (bytes: ArrayBuffer) => Array.from(new Uint8Array(bytes), b => b.toString(16).padStart(2, "0")).join("");
const hash = async (value: string) => hex(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value)));
const equalSecret = (left: string, right?: string) => !!right && left.length === right.length &&
  crypto.subtle.timingSafeEqual(new TextEncoder().encode(left), new TextEncoder().encode(right));

export default {
  async fetch(request: Request, env: RelayEnv): Promise<Response> {
    const url = new URL(request.url);
    if (request.method === "GET" && url.pathname === "/health")
      return Response.json({ ok: true, protocol: env.PROTOCOL, securityMigration: 1 });
    const authorization = request.headers.get("Authorization") ?? "";
    const credential = authorization.startsWith("Bearer ") ? authorization.slice(7) : "";
    if (!validKey(credential)) return new Response("Unauthorized", { status: 401 });
    const scope = equalSecret(credential, env.PROTECTED_ACCESS_KEY) ? await hash(credential)
      : equalSecret(credential, env.ACCESS_KEY) ? "legacy" : null;
    if (!scope)
      return new Response("Unauthorized", { status: 401 });
    if (request.method === "GET" && url.pathname === "/v1/clients")
      return Response.json(await env.DIRECTORY.getByName(scope === "legacy" ? "private-clients" : `private-clients-${scope}`).list(scope), {
        headers: { "Cache-Control": "no-store" }
      });
    if (request.method !== "GET" || request.headers.get("Upgrade")?.toLowerCase() !== "websocket")
      return new Response("WebSocket required", { status: 426 });
    const parts = url.pathname.split("/");
    if (parts.length < 5 || parts[1] !== "v1" || parts[2] !== "sessions" || !validId(parts[3]))
      return unavailable();
    const action = parts.slice(4).join("/");
    if (action !== "agent" && action !== "connect" && !/^channels\/[a-f0-9]{32}$/.test(action))
      return unavailable();
    // Overwrite any caller-supplied scope. Old installers cannot discover or
    // open channels to a protected registration, even if they know its ID.
    const forwarded = new Request(request);
    forwarded.headers.set("X-Access-Scope", scope);
    return env.SESSIONS.getByName(parts[3]).fetch(forwarded);
  }
} satisfies ExportedHandler<RelayEnv>;

// The private install's small directory stores routing IDs only. A computer is
// listed only while its session has a live, recently responsive agent socket.
export class ClientDirectory extends DurableObject<RelayEnv> {
  constructor(ctx: DurableObjectState, env: RelayEnv) {
    super(ctx, env);
    ctx.storage.sql.exec("CREATE TABLE IF NOT EXISTS clients (id TEXT PRIMARY KEY, registered INTEGER NOT NULL)");
  }

  register(id: string): void {
    this.ctx.storage.sql.exec("INSERT OR REPLACE INTO clients VALUES (?, ?)", id, Date.now());
    this.ctx.storage.sql.exec("DELETE FROM clients WHERE id NOT IN (SELECT id FROM clients ORDER BY registered DESC LIMIT 32)");
  }

  async list(scope: string): Promise<{ id: string; name: string; fingerprint?: string }[]> {
    const rows = this.ctx.storage.sql.exec<{ id: string }>("SELECT id FROM clients").toArray();
    const clients = await Promise.all(rows.map(async ({ id }) => {
      const peer = await this.env.SESSIONS.getByName(id).presence(scope);
      return peer ? { id, ...peer } : null;
    }));
    return clients.filter((client): client is { id: string; name: string; fingerprint?: string } => client !== null);
  }
}

// Each invitation is an independent coordination unit. WebSocket attachments
// and the owner hash survive hibernation; screen/file data is never stored.
export class SupportSession extends DurableObject<RelayEnv> {
  constructor(ctx: DurableObjectState, env: RelayEnv) {
    super(ctx, env);
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  async fetch(request: Request): Promise<Response> {
    const action = new URL(request.url).pathname.split("/").slice(4).join("/");
    const scope = request.headers.get("X-Access-Scope");
    if (!scope) return unavailable();
    if (action === "agent") {
      let name = "";
      try { name = decodeURIComponent(request.headers.get("X-Computer-Name") ?? "").trim(); }
      catch { return new Response("Invalid computer name", { status: 400 }); }
      if (name.length > 128 || /[\x00-\x1f\x7f]/.test(name)) return new Response("Invalid computer name", { status: 400 });
      const fingerprint = request.headers.get("X-Computer-Fingerprint") ?? "";
      if (fingerprint && !validKey(fingerprint)) return new Response("Invalid computer identity", { status: 400 });
      const key = request.headers.get("X-Session-Key") ?? "";
      if (!validKey(key)) return unavailable();
      const owner = await hash(key);
      const accepted = await this.ctx.blockConcurrencyWhile(async () => {
        const existing = await this.ctx.storage.get<string>("owner");
        const existingScope = await this.ctx.storage.get<string>("scope") ?? "legacy";
        if (existing && (existing !== owner || existingScope !== scope)) return false;
        if (!existing) await this.ctx.storage.put({ owner, scope });
        return true;
      });
      if (!accepted) return unavailable();
      for (const socket of this.ctx.getWebSockets()) this.close(socket, "Agent reconnected");
      const [client, server] = Object.values(new WebSocketPair());
      this.accept(server, "agent", "", crypto.randomUUID(), scope, name, fingerprint || undefined);
      if (name) await this.env.DIRECTORY.getByName(scope === "legacy" ? "private-clients" : `private-clients-${scope}`).register(new URL(request.url).pathname.split("/")[3]);
      server.send(JSON.stringify({ type: "registered", publicIp: request.headers.get("CF-Connecting-IP") ?? "" }));
      return new Response(null, { status: 101, webSocket: client });
    }
    if ((await this.ctx.storage.get<string>("scope") ?? "legacy") !== scope) return unavailable();
    const agents = this.ctx.getWebSockets("agent").filter(s => s.readyState === WebSocket.OPEN);
    if (agents.length !== 1) return unavailable();
    if (action === "connect") {
      if (this.ctx.getWebSockets("controller").length >= 12)
        return new Response("Session busy", { status: 429 });
      const channel = crypto.randomUUID().replaceAll("-", "");
      const [client, server] = Object.values(new WebSocketPair());
      this.accept(server, "controller", channel, agents[0].deserializeAttachment().generation, scope);
      agents[0].send(JSON.stringify({ type: "open", channel }));
      await this.scheduleCleanup();
      return new Response(null, { status: 101, webSocket: client });
    }
    if (action.startsWith("channels/")) {
      const key = request.headers.get("X-Session-Key") ?? "";
      if (!validKey(key) || await hash(key) !== await this.ctx.storage.get<string>("owner")) return unavailable();
      const channel = action.slice("channels/".length);
      const pending = this.ctx.getWebSockets(`channel:${channel}`);
      if (pending.length !== 1 || pending[0].deserializeAttachment()?.role !== "controller") return unavailable();
      const [client, server] = Object.values(new WebSocketPair());
      this.accept(server, "stream", channel, pending[0].deserializeAttachment().generation, scope);
      pending[0].send("ready");
      return new Response(null, { status: 101, webSocket: client });
    }
    return unavailable();
  }

  async presence(scope: string): Promise<{ name: string; fingerprint?: string } | null> {
    if ((await this.ctx.storage.get<string>("scope") ?? "legacy") !== scope) return null;
    const socket = this.ctx.getWebSockets("agent").find(s => s.readyState === WebSocket.OPEN);
    if (!socket) return null;
    const state = socket.deserializeAttachment() as SocketState;
    const lastSeen = this.ctx.getWebSocketAutoResponseTimestamp(socket)?.getTime() ?? state.created;
    return Date.now() - lastSeen < 60000 && state.name
      ? { name: state.name, ...(state.fingerprint ? { fingerprint: state.fingerprint } : {}) } : null;
  }

  private accept(socket: WebSocket, role: SocketState["role"], channel: string, generation: string, scope: string, name?: string, fingerprint?: string): void {
    this.ctx.acceptWebSocket(socket, [role, `channel:${channel}`]);
    socket.serializeAttachment({ role, channel, created: Date.now(), window: Date.now(), bytes: 0, generation, scope, name, fingerprint } satisfies SocketState);
  }

  webSocketMessage(socket: WebSocket, message: string | ArrayBuffer): void {
    const state = socket.deserializeAttachment() as SocketState;
    if ((state.scope ?? "legacy") === "legacy" && !this.env.ACCESS_KEY) { this.closePair(socket, "Access retired"); return; }
    if (state.role === "agent" || typeof message === "string" || message.byteLength > 65536) {
      this.closePair(socket, "Invalid relay frame"); return;
    }
    if (Date.now() - state.window >= 1000) { state.window = Date.now(); state.bytes = 0; }
    state.bytes += message.byteLength;
    if (state.bytes > 16 * 1024 * 1024) { this.closePair(socket, "Relay rate exceeded"); return; }
    socket.serializeAttachment(state);
    const peers = this.ctx.getWebSockets(`channel:${state.channel}`).filter(s => s !== socket && s.readyState === WebSocket.OPEN);
    if (peers.length !== 1) { this.closePair(socket, "Channel unavailable"); return; }
    try { peers[0].send(message); } catch { this.closePair(socket, "Channel closed"); }
  }

  webSocketClose(socket: WebSocket): void { this.closePair(socket, "Peer disconnected"); }
  webSocketError(socket: WebSocket): void { this.closePair(socket, "Peer disconnected"); }

  private close(socket: WebSocket, reason: string): void {
    try { socket.close(1000, reason); } catch { /* Already closed. */ }
  }

  private closePair(socket: WebSocket, reason: string): void {
    const state = socket.deserializeAttachment() as SocketState;
    const peers = state.role === "agent" ? this.ctx.getWebSockets() : this.ctx.getWebSockets(`channel:${state.channel}`);
    for (const peer of peers)
      if (peer.deserializeAttachment()?.generation === state.generation) this.close(peer, reason);
    this.ctx.waitUntil(this.scheduleCleanup());
  }

  private async scheduleCleanup(): Promise<void> {
    if (await this.ctx.storage.getAlarm() === null) await this.ctx.storage.setAlarm(Date.now() + 30000);
  }

  async alarm(): Promise<void> {
    const sockets = this.ctx.getWebSockets().filter(socket => socket.readyState === WebSocket.OPEN);
    if (!sockets.length) { await this.ctx.storage.deleteAll(); return; }
    let pendingController = false;
    for (const socket of sockets) {
      const state = socket.deserializeAttachment() as SocketState;
      if (state.role !== "controller" || this.ctx.getWebSockets(`channel:${state.channel}`).length === 2) continue;
      if (Date.now() - state.created > 30000) this.close(socket, "Connection timed out");
      else pendingController = true;
    }
    if (pendingController) await this.ctx.storage.setAlarm(Date.now() + 30000);
  }
}

import { DurableObject } from "cloudflare:workers";

type RelayEnv = Env & { ACCESS_KEY: string };
type SocketState = {
  role: "agent" | "controller" | "stream";
  channel: string;
  created: number;
  window: number;
  bytes: number;
  generation: string;
};
const unavailable = () => new Response("Session unavailable", { status: 404 });
const validId = (s: string) => /^[A-F0-9]{16}$/.test(s);
const validKey = (s: string) => /^[a-fA-F0-9]{64}$/.test(s);
const hex = (bytes: ArrayBuffer) => Array.from(new Uint8Array(bytes), b => b.toString(16).padStart(2, "0")).join("");
const hash = async (value: string) => hex(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value)));

export default {
  async fetch(request: Request, env: RelayEnv): Promise<Response> {
    const url = new URL(request.url);
    if (request.method === "GET" && url.pathname === "/health")
      return Response.json({ ok: true, protocol: env.PROTOCOL });
    // Hosting access is private. This credential never grants control of a PC:
    // endpoint J-PAKE, the displayed code, and pinned inner TLS still do that.
    if (!env.ACCESS_KEY || request.headers.get("Authorization") !== `Bearer ${env.ACCESS_KEY}`)
      return new Response("Unauthorized", { status: 401 });
    if (request.method !== "GET" || request.headers.get("Upgrade")?.toLowerCase() !== "websocket")
      return new Response("WebSocket required", { status: 426 });
    const parts = url.pathname.split("/");
    if (parts.length < 5 || parts[1] !== "v1" || parts[2] !== "sessions" || !validId(parts[3]))
      return unavailable();
    const action = parts.slice(4).join("/");
    if (action !== "agent" && action !== "connect" && !/^channels\/[a-f0-9]{32}$/.test(action))
      return unavailable();
    return env.SESSIONS.getByName(parts[3]).fetch(request);
  }
} satisfies ExportedHandler<RelayEnv>;

// Each invitation is an independent coordination unit. WebSocket attachments
// and the owner hash survive hibernation; screen/file data is never stored.
export class SupportSession extends DurableObject<RelayEnv> {
  constructor(ctx: DurableObjectState, env: RelayEnv) {
    super(ctx, env);
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
  }

  async fetch(request: Request): Promise<Response> {
    const action = new URL(request.url).pathname.split("/").slice(4).join("/");
    if (action === "agent") {
      const key = request.headers.get("X-Session-Key") ?? "";
      if (!validKey(key)) return unavailable();
      const owner = await hash(key);
      const accepted = await this.ctx.blockConcurrencyWhile(async () => {
        const existing = await this.ctx.storage.get<string>("owner");
        if (existing && existing !== owner) return false;
        if (!existing) await this.ctx.storage.put("owner", owner);
        return true;
      });
      if (!accepted) return unavailable();
      for (const socket of this.ctx.getWebSockets()) this.close(socket, "Agent reconnected");
      const [client, server] = Object.values(new WebSocketPair());
      this.accept(server, "agent", "", crypto.randomUUID());
      server.send(JSON.stringify({ type: "registered", publicIp: request.headers.get("CF-Connecting-IP") ?? "" }));
      await this.scheduleCleanup();
      return new Response(null, { status: 101, webSocket: client });
    }
    const agents = this.ctx.getWebSockets("agent").filter(s => s.readyState === WebSocket.OPEN);
    if (agents.length !== 1) return unavailable();
    if (action === "connect") {
      if (this.ctx.getWebSockets("controller").length >= 12)
        return new Response("Session busy", { status: 429 });
      const channel = crypto.randomUUID().replaceAll("-", "");
      const [client, server] = Object.values(new WebSocketPair());
      this.accept(server, "controller", channel, agents[0].deserializeAttachment().generation);
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
      this.accept(server, "stream", channel, pending[0].deserializeAttachment().generation);
      pending[0].send("ready");
      return new Response(null, { status: 101, webSocket: client });
    }
    return unavailable();
  }

  private accept(socket: WebSocket, role: SocketState["role"], channel: string, generation: string): void {
    this.ctx.acceptWebSocket(socket, [role, `channel:${channel}`]);
    socket.serializeAttachment({ role, channel, created: Date.now(), window: Date.now(), bytes: 0, generation } satisfies SocketState);
  }

  webSocketMessage(socket: WebSocket, message: string | ArrayBuffer): void {
    const state = socket.deserializeAttachment() as SocketState;
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
  }

  private async scheduleCleanup(): Promise<void> {
    if (await this.ctx.storage.getAlarm() === null) await this.ctx.storage.setAlarm(Date.now() + 30000);
  }

  async alarm(): Promise<void> {
    const sockets = this.ctx.getWebSockets();
    if (!sockets.length) { await this.ctx.storage.deleteAll(); return; }
    for (const socket of sockets) {
      const state = socket.deserializeAttachment() as SocketState;
      if (state.role === "controller" && Date.now() - state.created > 30000 &&
          this.ctx.getWebSockets(`channel:${state.channel}`).length !== 2)
        this.close(socket, "Connection timed out");
    }
    await this.ctx.storage.setAlarm(Date.now() + 30000);
  }
}

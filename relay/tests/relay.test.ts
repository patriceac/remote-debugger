import { SELF } from "cloudflare:test";
import { evictDurableObject, runDurableObjectAlarm, runInDurableObject } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { afterEach, describe, expect, it } from "vitest";

const key = "a".repeat(64), owner = "b".repeat(64);
const sockets: WebSocket[] = [];
const id = () => crypto.randomUUID().replaceAll("-", "").slice(0, 16).toUpperCase();
const receive = (ws: WebSocket) => new Promise<string | ArrayBuffer>((resolve, reject) => {
  ws.addEventListener("message", e => resolve(e.data as string | ArrayBuffer), { once: true });
  ws.addEventListener("close", () => reject(new Error("closed")), { once: true });
});
const alarmAt = (room: string) => runInDurableObject(env.SESSIONS.getByName(room), (_, state) => state.storage.getAlarm());
async function connect(room: string, action: string, ownerKey?: string, name?: string) {
  const headers: Record<string, string> = { Authorization: `Bearer ${key}`, Upgrade: "websocket" };
  if (ownerKey) headers["X-Session-Key"] = ownerKey;
  if (name) headers["X-Computer-Name"] = encodeURIComponent(name);
  const response = await SELF.fetch(`https://relay/v1/sessions/${room}/${action}`, { headers });
  expect(response.status).toBe(101);
  const ws = response.webSocket!; ws.binaryType = "arraybuffer"; ws.accept(); sockets.push(ws); return ws;
}
afterEach(() => { for (const ws of sockets.splice(0)) ws.close(); });

describe("private encrypted transport relay", () => {
  it("publishes a stable fingerprint and cleans ownership after the last disconnect", async () => {
    const room = id(), fingerprint = "e".repeat(64);
    const headers = { Authorization: `Bearer ${key}`, Upgrade: "websocket", "X-Session-Key": owner, "X-Computer-Name": "PC", "X-Computer-Fingerprint": fingerprint };
    expect((await SELF.fetch(`https://relay/v1/sessions/${room}/agent`, { headers: { ...headers, "X-Computer-Fingerprint": "invalid" } })).status).toBe(400);
    const response = await SELF.fetch(`https://relay/v1/sessions/${room}/agent`, { headers });
    const agent = response.webSocket!; agent.accept(); sockets.push(agent); await receive(agent);
    const list = await SELF.fetch("https://relay/v1/clients", { headers: { Authorization: `Bearer ${key}` } });
    expect(await list.json()).toContainEqual({ id: room, name: "PC", fingerprint });
    agent.close();
    await expect.poll(() => alarmAt(room)).not.toBeNull();
    await runDurableObjectAlarm(env.SESSIONS.getByName(room));
    expect(await runInDurableObject(env.SESSIONS.getByName(room), (_, state) => state.storage.get("owner"))).toBeUndefined();
  });
  it("isolates migrated computers from every legacy directory and channel request", async () => {
    const room = id();
    const response = await SELF.fetch(`https://relay/v1/sessions/${room}/agent`, {
      headers: { Authorization: `Bearer ${"d".repeat(64)}`, Upgrade: "websocket", "X-Session-Key": owner, "X-Computer-Name": "Protected PC" }
    });
    expect(response.status).toBe(101);
    const agent = response.webSocket!; agent.accept(); sockets.push(agent); await receive(agent);
    const oldList = await SELF.fetch("https://relay/v1/clients", { headers: { Authorization: `Bearer ${key}` } });
    expect(await oldList.json()).not.toContainEqual({ id: room, name: "Protected PC" });
    const newList = await SELF.fetch("https://relay/v1/clients", { headers: { Authorization: `Bearer ${"d".repeat(64)}` } });
    expect(await newList.json()).toContainEqual({ id: room, name: "Protected PC" });
    for (const action of ["connect", "agent", `channels/${"e".repeat(32)}`]) {
      expect((await SELF.fetch(`https://relay/v1/sessions/${room}/${action}`, {
        headers: { Authorization: `Bearer ${key}`, Upgrade: "websocket", "X-Session-Key": owner, "X-Access-Scope": "forged" }
      })).status).toBe(404);
    }
  });
  it("privately discovers live computers by name and removes disconnected clients", async () => {
    expect((await SELF.fetch("https://relay/v1/clients")).status).toBe(401);
    expect((await SELF.fetch("https://relay/v1/clients", { headers: { Authorization: `Bearer ${owner}` } })).status).toBe(401);
    const room = id(), agent = await connect(room, "agent", owner, "PC-Étude"); await receive(agent);
    const list = async () => {
      const response = await SELF.fetch("https://relay/v1/clients", { headers: { Authorization: `Bearer ${key}` } });
      expect(response.headers.get("Cache-Control")).toBe("no-store");
      return await response.json() as { id: string; name: string }[];
    };
    expect(await list()).toContainEqual({ id: room, name: "PC-Étude" });
    const impostor = await SELF.fetch(`https://relay/v1/sessions/${room}/agent`, {
      headers: { Upgrade: "websocket", Authorization: `Bearer ${key}`, "X-Session-Key": "c".repeat(64), "X-Computer-Name": "Fake" }
    });
    expect(impostor.status).toBe(404);
    expect(await list()).toContainEqual({ id: room, name: "PC-Étude" });
    const replacement = await connect(room, "agent", owner, "PC-Renamed"); await receive(replacement);
    expect((await list()).filter(client => client.id === room)).toEqual([{ id: room, name: "PC-Renamed" }]);
    replacement.close();
    await expect.poll(async () => (await list()).some(client => client.id === room)).toBe(false);
  });

  it("reuses the short-lived directory cache until registration invalidates it", async () => {
    const room = id(), agent = await connect(room, "agent", owner, "PC-Cached"); await receive(agent);
    const list = async () => {
      const response = await SELF.fetch("https://relay/v1/clients", { headers: { Authorization: `Bearer ${key}` } });
      expect(response.headers.get("Cache-Control")).toBe("no-store");
      return await response.json() as { id: string; name: string }[];
    };
    expect(await list()).toContainEqual({ id: room, name: "PC-Cached" });
    await runInDurableObject(env.DIRECTORY.getByName("private-clients"), (_, state) => {
      state.storage.sql.exec("DELETE FROM clients WHERE id = ?", room);
    });
    expect(await list()).toContainEqual({ id: room, name: "PC-Cached" });

    const replacement = await connect(room, "agent", owner, "PC-Invalidated"); await receive(replacement);
    await expect.poll(async () => (await list()).filter(client => client.id === room)).toEqual([{ id: room, name: "PC-Invalidated" }]);
    replacement.close();
    await expect.poll(async () => (await list()).some(client => client.id === room)).toBe(false);
  });

  it("migrates an old directory schema and prunes an ended session after a cold start", async () => {
    const directory = env.DIRECTORY.getByName(`migration-${id()}`), room = id();
    await runInDurableObject(directory, (_, state) => {
      state.storage.sql.exec("ALTER TABLE clients DROP COLUMN generation");
      state.storage.sql.exec("INSERT INTO clients (id, registered) VALUES (?, ?)", room, Date.now());
    });
    await evictDurableObject(directory);

    const columns = await runInDurableObject(directory, (_, state) =>
      state.storage.sql.exec<{ name: string }>("PRAGMA table_info(clients)").toArray());
    expect(columns.some(column => column.name === "generation")).toBe(true);
    expect(await runInDurableObject(directory, directoryObject => directoryObject.list("legacy"))).toEqual([]);
    expect(await runInDurableObject(directory, (_, state) =>
      state.storage.sql.exec<{ id: string }>("SELECT id FROM clients").toArray())).toEqual([]);
  });

  it("rejects missing hosting credentials and unknown sessions", async () => {
    expect((await SELF.fetch("https://relay/v1/sessions/1111111111111111/agent", { headers: { Upgrade: "websocket" } })).status).toBe(401);
    expect((await SELF.fetch(`https://relay/v1/sessions/${id()}/connect`, { headers: { Upgrade: "websocket", Authorization: `Bearer ${key}` } })).status).toBe(404);
  });

  it("relays binary records bidirectionally without interpreting payloads", async () => {
    const room = id(), agent = await connect(room, "agent", owner);
    expect(JSON.parse(await receive(agent) as string).type).toBe("registered");
    const open = receive(agent), controller = await connect(room, "connect");
    const channel = JSON.parse(await open as string).channel;
    const ready = receive(controller), stream = await connect(room, `channels/${channel}`, owner);
    expect(await ready).toBe("ready");
    const toAgent = receive(stream); controller.send(new Uint8Array([22, 3, 3, 0, 255]));
    expect([...new Uint8Array(await toAgent as ArrayBuffer)]).toEqual([22, 3, 3, 0, 255]);
    const toController = receive(controller); stream.send(new Uint8Array([9, 8, 7]));
    expect([...new Uint8Array(await toController as ArrayBuffer)]).toEqual([9, 8, 7]);
  });

  it("does not keep an alarm running for an idle agent", async () => {
    const room = id(), agent = await connect(room, "agent", owner); await receive(agent);
    expect(await alarmAt(room)).toBeNull();

    const open = receive(agent), controller = await connect(room, "connect");
    const channel = JSON.parse(await open as string).channel;
    expect(await alarmAt(room)).not.toBeNull();
    const ready = receive(controller), stream = await connect(room, `channels/${channel}`, owner);
    expect(await ready).toBe("ready");

    expect(await runDurableObjectAlarm(env.SESSIONS.getByName(room))).toBe(true);
    expect(await alarmAt(room)).toBeNull();
    stream.close(); controller.close();
  });

  it("prevents another agent from replacing the invitation owner", async () => {
    const room = id(), agent = await connect(room, "agent", owner); await receive(agent);
    const response = await SELF.fetch(`https://relay/v1/sessions/${room}/agent`, {
      headers: { Upgrade: "websocket", Authorization: `Bearer ${key}`, "X-Session-Key": "c".repeat(64) }
    });
    expect(response.status).toBe(404);
  });

  it("does not let a stale disconnect close a replacement registration", async () => {
    const room = id(), first = await connect(room, "agent", owner, "PC-Old"); await receive(first);
    const second = await connect(room, "agent", owner, "PC-New"); await receive(second);
    const open = receive(second), controller = await connect(room, "connect");
    expect(JSON.parse(await open as string).type).toBe("open");
    expect(controller.readyState).toBe(WebSocket.OPEN);
    const list = await SELF.fetch("https://relay/v1/clients", { headers: { Authorization: `Bearer ${key}` } });
    expect(await list.json()).toContainEqual({ id: room, name: "PC-New" });
  });
});

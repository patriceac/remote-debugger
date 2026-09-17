import { SELF } from "cloudflare:test";
import { afterEach, describe, expect, it } from "vitest";

const key = "a".repeat(64), owner = "b".repeat(64);
const sockets: WebSocket[] = [];
const id = () => crypto.randomUUID().replaceAll("-", "").slice(0, 16).toUpperCase();
const receive = (ws: WebSocket) => new Promise<string | ArrayBuffer>((resolve, reject) => {
  ws.addEventListener("message", e => resolve(e.data as string | ArrayBuffer), { once: true });
  ws.addEventListener("close", () => reject(new Error("closed")), { once: true });
});
async function connect(room: string, action: string, ownerKey?: string) {
  const headers: Record<string, string> = { Authorization: `Bearer ${key}`, Upgrade: "websocket" };
  if (ownerKey) headers["X-Session-Key"] = ownerKey;
  const response = await SELF.fetch(`https://relay/v1/sessions/${room}/${action}`, { headers });
  expect(response.status).toBe(101);
  const ws = response.webSocket!; ws.binaryType = "arraybuffer"; ws.accept(); sockets.push(ws); return ws;
}
afterEach(() => { for (const ws of sockets.splice(0)) ws.close(); });

describe("private encrypted transport relay", () => {
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

  it("prevents another agent from replacing the invitation owner", async () => {
    const room = id(), agent = await connect(room, "agent", owner); await receive(agent);
    const response = await SELF.fetch(`https://relay/v1/sessions/${room}/agent`, {
      headers: { Upgrade: "websocket", Authorization: `Bearer ${key}`, "X-Session-Key": "c".repeat(64) }
    });
    expect(response.status).toBe(404);
  });

  it("does not let a stale disconnect close a replacement registration", async () => {
    const room = id(), first = await connect(room, "agent", owner); await receive(first);
    const second = await connect(room, "agent", owner); await receive(second);
    const open = receive(second), controller = await connect(room, "connect");
    expect(JSON.parse(await open as string).type).toBe("open");
    expect(controller.readyState).toBe(WebSocket.OPEN);
  });
});

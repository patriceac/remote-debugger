import test from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import { createCliRunner } from '../cli.mjs';

class FakeWorker extends EventEmitter {
  constructor() {
    super();
    this.stdout = new PassThrough();
    this.stderr = new PassThrough();
    this.stdin = new PassThrough();
    this.requests = [];
    let pending = '';
    this.stdin.on('data', chunk => {
      pending += chunk.toString();
      let end;
      while ((end = pending.indexOf('\n')) >= 0) {
        const line = pending.slice(0, end);
        pending = pending.slice(end + 1);
        if (line) this.requests.push(JSON.parse(line));
      }
    });
  }

  reply(value) { this.stdout.write(JSON.stringify(value) + '\n'); }

  kill() {
    this.killed = true;
    this.emit('close', 1);
  }
}

function waitFor(predicate) {
  const deadline = Date.now() + 2000;
  return new Promise((resolve, reject) => {
    const poll = () => {
      if (predicate()) return resolve();
      if (Date.now() >= deadline) return reject(new Error('Timed out waiting for fake worker input.'));
      setImmediate(poll);
    };
    poll();
  });
}

const ok = id => ({ id, ok: true, targetId: null, machine: 'TEST-PC', data: {}, rpcOk: true, exitCode: null });

test('cached worker cancels one request and sends the next request once', async () => {
  const children = [];
  const runner = createCliRunner('RemoteDebugger.exe', undefined, {
    spawnProcess: () => { const child = new FakeWorker(); children.push(child); return child; }
  });
  const first = { id: 'first', operation: 'system', timeoutSeconds: 1 };
  const abort = new AbortController();
  const pending = runner(first, abort.signal);
  await waitFor(() => children[0]?.requests.some(request => request.id === first.id));
  abort.abort();
  await waitFor(() => children[0].requests.some(request => request.cancel === first.id));
  children[0].reply(ok(first.id));
  const cancelled = await pending;
  assert.equal(cancelled.ok, false);
  assert.equal(cancelled.error, 'cancelled');

  const second = { id: 'second', operation: 'status', timeoutSeconds: 1 };
  const next = runner(second);
  await waitFor(() => children[0].requests.some(request => request.id === second.id));
  children[0].reply(ok(second.id));
  assert.equal((await next).ok, true);
  assert.equal(children.length, 1);
  assert.deepEqual(children[0].requests.map(request => request.id ?? request.cancel), ['first', 'first', 'second']);
  runner.close();
});

test('worker reconnect starts fresh and never replays the failed request', async () => {
  const children = [];
  const runner = createCliRunner('RemoteDebugger.exe', undefined, {
    spawnProcess: () => { const child = new FakeWorker(); children.push(child); return child; }
  });
  const first = { id: 'lost', operation: 'system', timeoutSeconds: 1 };
  const failed = runner(first);
  await waitFor(() => children[0]?.requests.some(request => request.id === first.id));
  children[0].emit('close', 1);
  const failure = await failed;
  assert.equal(failure.ok, false);
  assert.equal(failure.error, 'cli_failed');

  const second = { id: 'future', operation: 'status', timeoutSeconds: 1 };
  const next = runner(second);
  await waitFor(() => children[1]?.requests.some(request => request.id === second.id));
  children[1].reply(ok(second.id));
  assert.equal((await next).ok, true);
  assert.equal(children.length, 2);
  assert.deepEqual(children[0].requests.map(request => request.id), ['lost']);
  assert.deepEqual(children[1].requests.map(request => request.id), ['future']);
  runner.close();
});

import { spawn } from 'node:child_process';

// Reuse one authenticated worker; serialize operations and never replay a failed call.
export function createCliRunner(executable, connection, { spawnProcess = spawn } = {}) {
  let child, active, tail = Promise.resolve(), closed = false;
  const failure = (request, error, message) => ({ id: request.id, ok: false,
    targetId: request.targetId ?? null, machine: null, data: null, rpcOk: null, exitCode: null, error, message });
  function start() {
    const args = ['cli', 'connected', '--worker'];
    if (connection) args.push('--connection', connection);
    const process = spawnProcess(executable, args, { shell: false, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    child = process;
    let output = '', bytes = 0, stderr = '';
    process.stdout.setEncoding('utf8');
    process.stderr.setEncoding('utf8');
    process.stdout.on('data', chunk => {
      bytes += Buffer.byteLength(chunk); output += chunk;
      if (bytes > 24 * 1024 * 1024) { output = ''; active?.stop('output_limit'); return; }
      let end;
      while ((end = output.indexOf('\n')) >= 0) {
        const line = output.slice(0, end); output = output.slice(end + 1); bytes = Buffer.byteLength(output);
        let reply;
        try { reply = JSON.parse(line); } catch { /* Fail closed below. */ }
        if (!active || reply?.id !== active.request.id || typeof reply?.ok !== 'boolean') {
          if (child === process) child = undefined;
          process.kill(); active?.finish(failure(active.request, 'invalid_reply', 'The worker returned an invalid reply. Remote outcome may be unknown.')); return;
        }
        active.finish(reply);
      }
    });
    process.stderr.on('data', chunk => { if (stderr.length < 4096) stderr += chunk.slice(0, 4096 - stderr.length); });
    process.stdin.on('error', () => {});
    process.once('error', error => { stderr = error.message; });
    process.once('close', () => {
      if (child !== process) return;
      child = undefined;
      active?.finish(failure(active.request, 'cli_failed', stderr || 'Worker closed. Remote outcome may be unknown; retain this request ID.'));
    });
  }
  const run = (request, signal) => {
    const task = tail.then(() => new Promise(resolve => {
      if (signal?.aborted || closed) return resolve(failure(request, 'cancelled', 'Cancelled before dispatch.'));
      if (!child) start();
      let stopReason, killTimer;
      const stop = reason => {
        if (stopReason) return;
        stopReason = reason;
        child?.stdin.write(JSON.stringify({ cancel: request.id }) + '\n');
        killTimer = setTimeout(() => child?.kill(), 7000);
      };
      const abort = () => stop('cancelled');
      const timeout = setTimeout(() => stop('timeout'), (request.timeoutSeconds + 10) * 1000);
      active = { request, stop, finish(reply) {
        clearTimeout(timeout); clearTimeout(killTimer); signal?.removeEventListener('abort', abort); active = undefined;
        if (stopReason) reply = { ...reply, ok: false, error: stopReason,
          message: 'The call was interrupted. Inspect its result before retrying with the same request ID.' };
        resolve(reply);
      }};
      signal?.addEventListener('abort', abort, { once: true });
      child.stdin.write(JSON.stringify(request) + '\n');
      if (signal?.aborted) abort();
    }));
    tail = task.catch(() => {});
    return task;
  };
  run.close = () => {
    closed = true;
    const process = child;
    process?.stdin.end();
    if (process) setTimeout(() => process.kill(), 7000).unref();
  };
  return run;
}

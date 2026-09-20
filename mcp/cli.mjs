import { spawn } from 'node:child_process';

// One explicit CLI executable, JSON on stdin, no shell or temporary request files.
export function createCliRunner(executable, connection) {
  return (request, signal) => new Promise((resolve) => {
    const failure = (error, message) => ({ id: request.id, ok: false, targetId: request.targetId ?? null,
      machine: null, data: null, rpcOk: null, exitCode: null, error, message });
    if (signal?.aborted) return resolve(failure('cancelled', 'Cancelled before dispatch.'));
    const args = ['cli', 'connected', '--request', '-', '--cancel-on-stdin-close'];
    if (connection) args.push('--connection', connection);
    const child = spawn(executable, args, { shell: false, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    let output = '', stderr = '', bytes = 0, stopReason, killTimer;
    const stop = (reason) => {
      if (stopReason) return;
      stopReason = reason;
      // EOF cancels the CLI token, which sends a bounded cancel RPC for commands.
      child.stdin.end();
      killTimer = setTimeout(() => child.kill(), 7000);
    };
    const abort = () => stop('cancelled');
    signal?.addEventListener('abort', abort, { once: true });
    if (signal?.aborted) abort();
    const timeout = setTimeout(() => stop('timeout'), (request.timeoutSeconds + 10) * 1000);
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', chunk => {
      bytes += Buffer.byteLength(chunk);
      if (bytes > 24 * 1024 * 1024) stop('output_limit');
      else output += chunk;
    });
    child.stderr.on('data', chunk => { if (stderr.length < 4096) stderr += chunk.slice(0, 4096 - stderr.length); });
    child.stdin.on('error', () => {}); // An exited CLI closes its input pipe.
    child.once('error', error => { stderr = error.message; });
    child.once('close', code => {
      clearTimeout(timeout); clearTimeout(killTimer);
      signal?.removeEventListener('abort', abort);
      let reply;
      try { reply = JSON.parse(output); } catch { /* Normalize startup / transport failures below. */ }
      if (reply?.id !== request.id || typeof reply?.ok !== 'boolean')
        return resolve(failure(stopReason ?? 'cli_failed', stderr || 'The CLI did not return a valid connected-session response. Remote outcome may be unknown; retain this request ID.'));
      if (stopReason && reply.ok) return resolve({ ...reply, ok: false, error: stopReason,
        message: 'The call was interrupted. Inspect the returned result before retrying with the same request ID.' });
      if (code !== 0 && reply.ok) return resolve({ ...reply, ok: false, error: 'cli_failed', message: `The CLI exited with code ${code}.` });
      resolve(reply);
    });
    if (!stopReason) child.stdin.write(JSON.stringify(request) + '\n');
  });
}

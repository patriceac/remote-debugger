// Integration driver: run only in the disposable Hyper-V guest.
import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash, randomUUID } from 'node:crypto';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';

const [executable, connection, output, mode] = process.argv.slice(2);
const checks = [];
const client = new Client({ name: 'remote-debugger-probe', version: '1' });
let passed = false, error;
try {
  await client.connect(new StdioClientTransport({ command: process.execPath,
    args: [fileURLToPath(new URL('../server.mjs', import.meta.url))],
    env: { ...process.env, REMOTE_DEBUGGER_EXE: resolve(executable), REMOTE_DEBUGGER_CONNECTION: resolve(connection) }, stderr: 'pipe' }));
  assert.equal((await client.listTools()).tools.length, 9);
  checks.push('stdio_initialize_and_nine_tools');
  const call = async (name, args = {}, expectError = false) => {
    const result = await client.callTool({ name, arguments: args }, undefined, { timeout: 45000 });
    assert.equal(result.isError, expectError, JSON.stringify(result.structuredContent));
    return result;
  };
  const status = await call('remote_status', {}, mode === 'disconnected');
  if (mode === 'disconnected') {
    assert.equal(status.structuredContent.error, 'not_connected');
    checks.push('missing_session_fails_without_setup');
  } else {
    const { targetId, machine, data } = status.structuredContent;
    assert.ok(machine); assert.equal(data.binaryMatched, true);
    const common = { targetId, timeoutSeconds: 30 };
    await call('remote_system', common);
    await call('remote_processes', common);
    await call('remote_process_info', { ...common, pid: data.status.processId });
    checks.push('confirmed_machine_diagnostics');
    const run = await call('remote_run', { ...common, file: 'whoami.exe', arguments: [] });
    assert.equal(run.structuredContent.exitCode, 0);
    assert.ok(run.structuredContent.data.stdout.trim());
    const failed = await call('remote_run', { ...common, file: 'cmd.exe', arguments: ['/d', '/c', 'exit 7'] }, true);
    assert.equal(failed.structuredContent.rpcOk, true); assert.equal(failed.structuredContent.exitCode, 7);
    checks.push('command_success_and_nonzero_exit');
    const changed = await call('remote_run', { ...common, targetId: '0'.repeat(64) + '.' + '0'.repeat(64), file: 'whoami.exe', arguments: [] }, true);
    assert.equal(changed.structuredContent.error, 'target_changed');
    checks.push('target_change_rejected');
    const source = join(output, 'source with spaces.txt'), destination = join(output, 'download with spaces.txt');
    await writeFile(source, 'MCP verified transfer — é\n');
    const remotePath = 'deployments/mcp-probe/roundtrip.txt';
    const uploaded = await call('remote_upload', { ...common, localPath: source, remotePath });
    const info = await call('remote_file_info', { ...common, path: remotePath });
    const downloaded = await call('remote_download', { ...common, localPath: destination, remotePath });
    const hash = createHash('sha256').update(await readFile(source)).digest('hex').toUpperCase();
    assert.equal(uploaded.structuredContent.data.sha256, hash);
    assert.equal(info.structuredContent.data.sha256, hash);
    assert.equal(downloaded.structuredContent.data.sha256, hash);
    assert.deepEqual(await readFile(source), await readFile(destination));
    checks.push('sha256_upload_download_with_spaces_and_unicode');
    const screenshot = await call('remote_screenshot', common);
    const image = screenshot.content.find(block => block.type === 'image');
    assert.equal(image.mimeType, 'image/jpeg');
    const bytes = Buffer.from(image.data, 'base64');
    assert.equal(bytes.readUInt16BE(0), 0xffd8);
    await writeFile(join(output, 'mcp-screenshot.jpg'), bytes);
    checks.push('fresh_image_content');
    // The same UUID must return the original result, not write the marker twice.
    const marker = join(data.status.workspace, 'mcp-once.txt');
    const once = { ...common, requestId: randomUUID(), file: 'cmd.exe', arguments: ['/d', '/c', `echo once>>"${marker}"`] };
    await call('remote_run', once); await call('remote_run', once);
    assert.equal((await readFile(marker, 'utf8')).trim(), 'once');
    checks.push('same_uuid_command_not_reexecuted');
    const timed = await call('remote_run', { ...common, timeoutSeconds: 3, file: 'powershell.exe', arguments: ['-NoProfile', '-Command', 'Start-Sleep -Seconds 30'] }, true);
    assert.equal(timed.structuredContent.error, 'cancelled_or_timeout');
    checks.push('bounded_command_timeout');
    const started = join(data.status.workspace, 'mcp-cancel-pid.txt');
    const abort = new AbortController();
    const pending = client.callTool({ name: 'remote_run', arguments: { ...common, timeoutSeconds: 90,
      file: 'powershell.exe', arguments: ['-NoProfile', '-Command', `Set-Content -LiteralPath '${started.replaceAll("'", "''")}' -Value $PID; Start-Sleep -Seconds 60`] } },
      undefined, { signal: abort.signal, timeout: 100000 }).then(() => false, () => true);
    let pid;
    for (let attempt = 0; attempt < 100; attempt++) {
      try { pid = Number((await readFile(started, 'utf8')).trim()); if (pid > 0) break; } catch { }
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    assert.ok(pid > 0, 'The cancellable command must actually start.');
    abort.abort(); assert.equal(await pending, true);
    let alive = true;
    for (let attempt = 0; attempt < 100; attempt++) {
      try { process.kill(pid, 0); } catch { alive = false; break; }
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    assert.equal(alive, false, 'MCP cancellation must stop the remote command process.');
    checks.push('mcp_abort_stdin_eof_cancels_remote_process');
  }
  passed = true;
} catch (failure) { error = failure.stack; }
finally {
  await client.close();
  await writeFile(join(output, `mcp-${mode}.json`), JSON.stringify({ passed, checks, error }, null, 2));
}
if (!passed) { console.error(error); process.exitCode = 1; }

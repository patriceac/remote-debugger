// Integration driver: run only in the disposable Hyper-V guest.
import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
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
  assert.equal((await client.listTools()).tools.length, 10);
  checks.push('stdio_initialize_and_ten_tools');
  const call = async (name, args = {}, expectError = false) => {
    const result = await client.callTool({ name, arguments: args }, undefined, { timeout: 45000 });
    assert.equal(result.isError, expectError, JSON.stringify(result.structuredContent));
    return result;
  };
  const reports = await call('remote_reports');
  assert.ok(Array.isArray(reports.structuredContent.data.reports));
  checks.push('local_reports_without_remote_target');
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
    if (mode === 'connected-admin') {
      const run = await call('remote_run', { ...common, file: 'whoami.exe', arguments: ['/user'] });
      assert.equal(run.structuredContent.exitCode, 0);
      assert.match(run.structuredContent.data.stdout, /S-1-5-18/);
      const failed = await call('remote_run', { ...common, file: 'cmd.exe', arguments: ['/d', '/c', 'exit 7'] }, true);
      assert.equal(failed.structuredContent.rpcOk, true);
      assert.equal(failed.structuredContent.exitCode, 7);
      checks.push('administrator_broker_command_and_nonzero_exit');
    } else {
      const run = await call('remote_run', { ...common, file: 'whoami.exe', arguments: [] }, true);
      assert.equal(run.structuredContent.error, 'operation_failed');
      assert.match(run.structuredContent.message, /Administrator maintenance is unavailable/);
      checks.push('unprovisioned_admin_command_fails_closed');
    }
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
  }
  passed = true;
} catch (failure) { error = failure.stack; }
finally {
  await client.close();
  await writeFile(join(output, `mcp-${mode}.json`), JSON.stringify({ passed, checks, error }, null, 2));
}
if (!passed) { console.error(error); process.exitCode = 1; }

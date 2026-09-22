import test from 'node:test';
import assert from 'node:assert/strict';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { InMemoryTransport } from '@modelcontextprotocol/sdk/inMemory.js';
import { createServer, definitions, invokeTool } from '../tools.mjs';

const targetId = 'A'.repeat(64) + '.' + 'B'.repeat(64);
const definition = name => definitions.find(tool => tool.name === name);
const success = request => ({ id: request.id, ok: true, targetId, machine: 'TEST-PC', rpcOk: true, data: {} });

test('real SDK handshake discovers typed tools and propagates no-session failure', async () => {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const server = createServer(async request => ({ id: request.id, ok: false, data: null, error: 'not_connected', message: 'No connected computer.' }));
  const client = new Client({ name: 'test', version: '1' });
  try {
    await server.connect(serverTransport); await client.connect(clientTransport);
    const tools = (await client.listTools()).tools;
    assert.equal(tools.length, 10);
    assert.equal(tools.find(t => t.name === 'remote_run').annotations.readOnlyHint, false);
    assert.equal(tools.find(t => t.name === 'remote_status').annotations.readOnlyHint, true);
    assert.ok(tools.find(t => t.name === 'remote_run').inputSchema.required.includes('targetId'));
    const result = await client.callTool({ name: 'remote_status', arguments: {} });
    assert.equal(result.isError, true);
    assert.equal(result.structuredContent.error, 'not_connected');
    const invalid = await client.callTool({ name: 'remote_run', arguments: { targetId, file: 'whoami.exe', arguments: [], timeoutSeconds: 999 } });
    assert.equal(invalid.isError, true);
  } finally { await client.close(); await server.close(); }
});

test('saved reports need no live target and keep local evidence marked read-only', async () => {
  const result = await invokeTool(definition('remote_reports'), {}, async request => {
    assert.equal(request.targetId, undefined);
    assert.equal(request.operation, 'reports');
    return { ...success(request), targetId: 'local-reports', data: { reports: [{ id: 'a'.repeat(32) }] } };
  });
  assert.equal(result.isError, false);
  assert.equal(definition('remote_reports').readOnly, true);
});

test('command IDs, target and arguments survive mapping; nonzero and absent exits fail', async () => {
  const requestId = 'd3bf7695-1339-48e4-bf89-a784a1652b4e';
  for (const exitCode of [0, 7, undefined]) {
    const result = await invokeTool(definition('remote_run'), { targetId, requestId, file: 'tool.exe', arguments: ['a b', '"q"', '$var', 'é'] }, async request => {
      assert.equal(request.id, requestId); assert.equal(request.targetId, targetId);
      assert.equal(request.operation, 'maintenance.session');
      assert.deepEqual(request.args.arguments, ['a b', '"q"', '$var', 'é']);
      return { ...success(request), exitCode };
    });
    assert.equal(result.isError, exitCode !== 0);
  }
});

test('unexpected targets and RPC failures cannot look successful', async () => {
  for (const override of [{ targetId: 'wrong' }, { rpcOk: false }]) {
    const result = await invokeTool(definition('remote_system'), { targetId }, async request => ({ ...success(request), ...override }));
    assert.equal(result.isError, true); assert.equal(result.structuredContent.error, 'invalid_reply');
  }
});

test('screenshot is an MCP image and metadata contains no duplicated base64', async () => {
  const result = await invokeTool(definition('remote_screenshot'), { targetId }, async request => ({ ...success(request), data: { data: '/9j/2Q==', capturedUtc: 'now', geometry: { width: 1920 } } }));
  assert.equal(result.isError, false);
  assert.equal(result.content[1].mimeType, 'image/jpeg');
  assert.equal(result.content[1].data, '/9j/2Q==');
  assert.equal(result.structuredContent.data.data, undefined);
  assert.ok(!result.content[0].text.includes('/9j/2Q=='));
});

test('transfers map exact local and remote paths and retain verified hashes', async () => {
  for (const name of ['remote_upload', 'remote_download']) {
    const result = await invokeTool(definition(name), { targetId, localPath: 'C:\\some dir\\é.exe', remotePath: 'deployments/v1/é.exe' }, async request => {
      assert.equal(request.operation, name.slice(7));
      assert.equal(request.args.localPath, 'C:\\some dir\\é.exe');
      assert.equal(request.args.remotePath, 'deployments/v1/é.exe');
      return { ...success(request), data: { sha256: 'C'.repeat(64), size: 123 } };
    });
    assert.equal(result.isError, false); assert.equal(result.structuredContent.data.sha256, 'C'.repeat(64));
  }
});

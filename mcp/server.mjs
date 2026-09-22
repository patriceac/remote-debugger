import { isAbsolute } from 'node:path';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { createCliRunner } from './cli.mjs';
import { createServer } from './tools.mjs';

const executable = process.env.REMOTE_DEBUGGER_EXE;
const connection = process.env.REMOTE_DEBUGGER_CONNECTION;
if (!executable || !isAbsolute(executable)) throw new Error('REMOTE_DEBUGGER_EXE must name the absolute signed Release RemoteDebugger.exe path.');
if (connection && !isAbsolute(connection)) throw new Error('REMOTE_DEBUGGER_CONNECTION must be an absolute path.');
const runner = createCliRunner(executable, connection);
const server = createServer(runner);
server.server.onclose = () => runner.close();
await server.connect(new StdioServerTransport());

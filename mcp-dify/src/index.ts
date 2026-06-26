import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { loadConfig } from './config.js';
import { DifyClient } from './client.js';
import { registerSearchTools } from './tools/search.js';
import { registerDatasetTools } from './tools/datasets.js';
import { registerDocumentTools } from './tools/documents.js';
import { registerSegmentTools } from './tools/segments.js';

async function main(): Promise<void> {
  const config = loadConfig();
  const client = new DifyClient(config);

  const server = new McpServer({
    name: 'dify-knowledge-base',
    version: '1.0.0',
  });

  registerSearchTools(server, client, config);
  registerDatasetTools(server, client, config);
  registerDocumentTools(server, client, config);
  registerSegmentTools(server, client, config);

  const transport = new StdioServerTransport();
  await server.connect(transport);

  // stdout занят MCP-протоколом — лог только в stderr
  process.stderr.write('mcp-dify: сервер запущен\n');
}

main().catch((err: unknown) => {
  process.stderr.write(`mcp-dify: фатальная ошибка — ${String(err)}\n`);
  process.exit(1);
});

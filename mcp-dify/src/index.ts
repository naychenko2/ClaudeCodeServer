// MCP-сервер баз знаний Dify: TypeScript со сборкой tsc, единственный наш сервер
// с внешней зависимостью (@modelcontextprotocol/sdk); ходит во внешний Dify API
// (DIFY_API_URL/DIFY_API_KEY), а не в наш бэкенд.
//
// ЗАМОРОЖЕН (ветка отката, ADR-012 фаза 2 волна 4): источник контракта —
// backend/ClaudeHomeServer/Services/Mcp/Http/DifyToolset.cs; правки схем/поведения
// обязаны ехать парой с http-веткой (сторож — DifyToolsetParityTests).
// Файл не удалять: Mcp:HttpTransport=false возвращает dify на stdio с этим env
// (узел собирает ClaudeSession из секции Dify appsettings; объявление больше
// не требует записи в внешнем базовом конфиге McpConfigPath — она перекрывается).
//
// Окружение (задаёт ClaudeSession при запуске claude):
//   DIFY_API_URL            — адрес внешнего Dify API
//   DIFY_API_KEY            — ключ Dify (секция Dify appsettings; на http-ветке
//                             ключ не покидает бэкенд вовсе)
//   DIFY_DEFAULT_DATASET_ID — датасет проекта чата (поиск по умолчанию)
//   DIFY_SEARCH_ONLY        — "true": у проекта есть база → только поиск (4 инструмента)

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

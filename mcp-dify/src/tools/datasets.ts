import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import type { DifyClient } from '../client.js';
import type { DifyConfig } from '../config.js';

export function registerDatasetTools(server: McpServer, client: DifyClient, config: DifyConfig): void {
  server.tool(
    'list_datasets',
    'Получить список баз знаний (Knowledge Bases) в Dify с пагинацией.',
    {
      page: z.number().int().min(1).default(1).describe('Номер страницы'),
      limit: z.number().int().min(1).max(100).default(20).describe('Записей на странице'),
    },
    async ({ page, limit }) => {
      const result = await client.listDatasets(page, limit);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }
  );

  if (!config.searchOnly) {
    server.tool(
      'create_dataset',
      'Создать новую базу знаний (Knowledge Base) в Dify.',
      {
        name: z.string().describe('Название базы знаний'),
        description: z.string().optional().describe('Описание базы знаний'),
        indexing_technique: z.enum(['high_quality', 'economy'])
          .optional()
          .describe('Метод индексации: high_quality (embedding) или economy (инвертированный индекс)'),
        permission: z.enum(['only_me', 'all_team_members'])
          .optional()
          .describe('Права доступа: only_me или all_team_members'),
      },
      async (params) => {
        const result = await client.createDataset(params);
        return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
      }
    );

    server.tool(
      'delete_dataset',
      'Удалить базу знаний по ID. Операция необратима.',
      {
        dataset_id: z.string().describe('ID базы знаний для удаления'),
      },
      async ({ dataset_id }) => {
        await client.deleteDataset(dataset_id);
        return { content: [{ type: 'text', text: `База знаний ${dataset_id} удалена` }] };
      }
    );
  }
}

import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import type { DifyClient } from '../client.js';
import type { DifyConfig } from '../config.js';

export function registerSegmentTools(server: McpServer, client: DifyClient, config: DifyConfig): void {
  function resolveDatasetId(id?: string): string {
    const resolved = id ?? config.defaultDatasetId;
    if (!resolved) throw new Error('dataset_id не указан и DIFY_DEFAULT_DATASET_ID не задан');
    return resolved;
  }

  const datasetIdParam = z.string().optional().describe('ID базы знаний (по умолчанию — DIFY_DEFAULT_DATASET_ID)');

  server.tool(
    'list_segments',
    'Получить список сегментов (чанков) документа.',
    {
      dataset_id: datasetIdParam,
      document_id: z.string().describe('ID документа'),
      keyword: z.string().optional().describe('Фильтр по ключевому слову в тексте сегмента'),
      status: z.enum(['indexed', 'waiting', 'indexing', 'error'])
        .optional()
        .describe('Фильтр по статусу сегмента'),
    },
    async ({ dataset_id, document_id, keyword, status }) => {
      const dsId = resolveDatasetId(dataset_id);
      const result = await client.listSegments(dsId, document_id, keyword, status);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }
  );

  if (!config.searchOnly) server.tool(
    'add_segments',
    'Добавить сегменты (чанки) в существующий документ базы знаний.',
    {
      dataset_id: datasetIdParam,
      document_id: z.string().describe('ID документа'),
      segments: z.array(
        z.object({
          content: z.string().describe('Текст сегмента'),
          answer: z.string().optional().describe('Ответ для QA-режима документа'),
          keywords: z.array(z.string()).optional().describe('Ключевые слова сегмента'),
        })
      ).min(1).describe('Список сегментов для добавления'),
    },
    async ({ dataset_id, document_id, segments }) => {
      const dsId = resolveDatasetId(dataset_id);
      const result = await client.addSegments(dsId, document_id, segments);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }
  );
}

import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import type { DifyClient } from '../client.js';
import type { DifyConfig } from '../config.js';

export function registerSearchTools(server: McpServer, client: DifyClient, config: DifyConfig): void {
  function resolveDatasetId(id?: string): string {
    const resolved = id ?? config.defaultDatasetId;
    if (!resolved) throw new Error('dataset_id не указан и DIFY_DEFAULT_DATASET_ID не задан');
    return resolved;
  }

  server.tool(
    'search_knowledge',
    'Семантический поиск по базе знаний Dify. Возвращает релевантные фрагменты с оценкой схожести.',
    {
      query: z.string().describe('Текст поискового запроса'),
      dataset_id: z.string().optional().describe('ID базы знаний (по умолчанию — DIFY_DEFAULT_DATASET_ID)'),
      top_k: z.number().int().min(1).max(20).optional().describe('Максимальное число результатов (по умолчанию 5)'),
      search_method: z.enum(['semantic_search', 'keyword_search', 'full_text_search'])
        .optional()
        .describe('Метод поиска: semantic_search, keyword_search или full_text_search'),
      score_threshold_enabled: z.boolean().optional().describe('Включить фильтрацию по минимальному score'),
      score_threshold: z.number().min(0).max(1).optional().describe('Минимальный score (0–1), работает при score_threshold_enabled=true'),
    },
    async ({ query, dataset_id, top_k, search_method, score_threshold_enabled, score_threshold }) => {
      const dsId = resolveDatasetId(dataset_id);
      const result = await client.retrieve(dsId, query, {
        top_k,
        search_method,
        score_threshold_enabled,
        score_threshold,
      });
      return {
        content: [{ type: 'text', text: JSON.stringify(result, null, 2) }],
      };
    }
  );
}

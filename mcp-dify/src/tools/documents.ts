import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import type { DifyClient } from '../client.js';
import type { DifyConfig } from '../config.js';

export function registerDocumentTools(server: McpServer, client: DifyClient, config: DifyConfig): void {
  function resolveDatasetId(id?: string): string {
    const resolved = id ?? config.defaultDatasetId;
    if (!resolved) throw new Error('dataset_id не указан и DIFY_DEFAULT_DATASET_ID не задан');
    return resolved;
  }

  const datasetIdParam = z.string().optional().describe('ID базы знаний (по умолчанию — DIFY_DEFAULT_DATASET_ID)');
  const indexingParam = z.enum(['high_quality', 'economy']).optional().describe('Метод индексации: high_quality или economy');
  const processRuleParam = z.enum(['automatic', 'custom']).optional().describe('Режим обработки: automatic или custom');

  server.tool(
    'list_documents',
    'Получить список документов в базе знаний.',
    {
      dataset_id: datasetIdParam,
      page: z.number().int().min(1).default(1).describe('Номер страницы'),
      limit: z.number().int().min(1).max(100).default(20).describe('Записей на странице'),
      keyword: z.string().optional().describe('Фильтр по ключевому слову в названии документа'),
    },
    async ({ dataset_id, page, limit, keyword }) => {
      const dsId = resolveDatasetId(dataset_id);
      const result = await client.listDocuments(dsId, page, limit, keyword);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }
  );

  if (!config.searchOnly) {
  server.tool(
    'create_document_by_text',
    'Создать документ в базе знаний из текстового содержимого.',
    {
      dataset_id: datasetIdParam,
      name: z.string().describe('Название документа'),
      text: z.string().describe('Текстовое содержимое документа'),
      indexing_technique: indexingParam,
      process_rule_mode: processRuleParam,
    },
    async ({ dataset_id, name, text, indexing_technique, process_rule_mode }) => {
      const dsId = resolveDatasetId(dataset_id);
      const result = await client.createDocumentByText(dsId, {
        name,
        text,
        indexing_technique,
        process_rule: process_rule_mode ? { mode: process_rule_mode } : undefined,
      });
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'create_document_by_file',
    'Создать документ в базе знаний из файла. Файл передаётся в кодировке Base64.',
    {
      dataset_id: datasetIdParam,
      file_base64: z.string().describe('Содержимое файла в кодировке Base64'),
      file_name: z.string().describe('Имя файла с расширением (например, report.pdf)'),
      indexing_technique: indexingParam,
      process_rule_mode: processRuleParam,
    },
    async ({ dataset_id, file_base64, file_name, indexing_technique, process_rule_mode }) => {
      const dsId = resolveDatasetId(dataset_id);
      const buf = Buffer.from(file_base64, 'base64');
      const result = await client.createDocumentByFile(dsId, buf, file_name, {
        indexing_technique,
        process_rule: process_rule_mode ? { mode: process_rule_mode } : undefined,
      });
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'update_document_by_text',
    'Обновить существующий документ новым текстовым содержимым.',
    {
      dataset_id: datasetIdParam,
      document_id: z.string().describe('ID документа для обновления'),
      name: z.string().optional().describe('Новое название документа'),
      text: z.string().optional().describe('Новое текстовое содержимое'),
      process_rule_mode: processRuleParam,
    },
    async ({ dataset_id, document_id, name, text, process_rule_mode }) => {
      const dsId = resolveDatasetId(dataset_id);
      const result = await client.updateDocumentByText(dsId, document_id, {
        name,
        text,
        process_rule: process_rule_mode ? { mode: process_rule_mode } : undefined,
      });
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'update_document_by_file',
    'Заменить файл существующего документа. Новый файл передаётся в кодировке Base64.',
    {
      dataset_id: datasetIdParam,
      document_id: z.string().describe('ID документа для обновления'),
      file_base64: z.string().describe('Новое содержимое файла в кодировке Base64'),
      file_name: z.string().describe('Имя файла с расширением'),
      process_rule_mode: processRuleParam,
    },
    async ({ dataset_id, document_id, file_base64, file_name, process_rule_mode }) => {
      const dsId = resolveDatasetId(dataset_id);
      const buf = Buffer.from(file_base64, 'base64');
      const result = await client.updateDocumentByFile(dsId, document_id, buf, file_name, {
        process_rule: process_rule_mode ? { mode: process_rule_mode } : undefined,
      });
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'delete_document',
    'Удалить документ из базы знаний. Операция необратима.',
    {
      dataset_id: datasetIdParam,
      document_id: z.string().describe('ID документа для удаления'),
    },
    async ({ dataset_id, document_id }) => {
      const dsId = resolveDatasetId(dataset_id);
      await client.deleteDocument(dsId, document_id);
      return { content: [{ type: 'text', text: `Документ ${document_id} удалён` }] };
    }
  );
  } // if (!config.searchOnly)
}

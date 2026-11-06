// Типы домена «Знания» (переехали из lib/api.ts, docs/research/frontend-api-split-2026-11.md).
// lib/api.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// Документ Dify-датасета проекта: статус индексации и теги.
export interface DifyDocument {
  id: string;
  name: string;
  indexingStatus: string;
  error?: string | null;   // текст ошибки индексации (только у indexingStatus === 'error')
  tags?: string[];
}

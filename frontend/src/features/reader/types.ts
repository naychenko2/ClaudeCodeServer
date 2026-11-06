// Типы домена «Ридер ссылок» (переехали из lib/api.ts, docs/research/frontend-api-split-2026-11.md).
// lib/api.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.
import type { ReaderErrorCode, ReaderPage } from '../../types';

// Итог чтения ссылки: успех — страница, отказ — код ошибки; обе ветки — данные (ADR-005).
export type ReaderReadResult =
  | { ok: true; page: ReaderPage }
  | { ok: false; error: { code: ReaderErrorCode; httpStatus?: number | null } };

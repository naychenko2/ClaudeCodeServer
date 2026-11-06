// Типы домена «Ридер ссылок» (переехали из lib/api.ts, docs/research/frontend-api-split-2026-11.md).
// lib/api.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.
// ReaderErrorCode и ReaderPage переехали в этот файл из types/index.ts (S5-P3) — импорт не нужен.

// Итог чтения ссылки: успех — страница, отказ — код ошибки; обе ветки — данные (ADR-005).
export type ReaderReadResult =
  | { ok: true; page: ReaderPage }
  | { ok: false; error: { code: ReaderErrorCode; httpStatus?: number | null } };

// Типы раздела «Ридер ссылок» (переехали из types/index.ts, S5-P3).

// --- Ридер ссылок (docs/adr/ADR-005-link-reader-server.md) ---

// Причины отказа — таблица §6 ADR-005; тексты для человека рисует фронт (readerErrors.ts)
export type ReaderErrorCode =
  | 'invalid-url' | 'local-address' | 'dns-failed' | 'unreachable' | 'tls-invalid'
  | 'timeout' | 'auth-required' | 'blocked-by-site' | 'not-found' | 'server-error'
  | 'too-many-redirects' | 'not-a-page' | 'pdf' | 'too-large' | 'not-readable';

export interface ReaderPage {
  title: string;
  siteName?: string | null;
  byline?: string | null;
  markdown: string;
}

export interface ReaderError {
  code: ReaderErrorCode;
  // Для диагностики — человеку не показывается (ADR §6)
  httpStatus?: number | null;
}

// Домен «Ридер ссылок» (вынесено из lib/api.ts, docs/research/frontend-api-split-2026-11.md):
// чтение страницы по ссылке и проба встраиваемости (ADR-005, ADR-006 §1) вместе со своими
// хелперами. ReaderReadResult, ReaderPage и ReaderErrorCode — в ./types (S5-P3) — импорт
// через types/index.ts не нужен.
import { request } from '../../lib/offline';
import type { ReaderErrorCode, ReaderPage, ReaderReadResult } from './types';

// Методы домена; lib/api.ts подмешивает их в объект api spread'ом
// (адреса api.reader.* остаются прежними для всех потребителей).
export const readerApi = {
  reader: {
    // Контракт ADR-005 (docs/adr/ADR-005-link-reader-server.md): успех — { title, siteName?,
    // byline?, markdown }, отказ — { error: { code, httpStatus? } }. Отказ — ОЖИДАЕМЫЙ исход
    // (часть сайтов не читается, это норма, а не поломка), поэтому здесь гасим исключение
    // request() при non-2xx и возвращаем обе ветки как данные — вызывающему нечего ловить.
    read: (url: string) => readReaderPage(url),
    // Серверная проба встраиваемости (ADR-006 §1): POST /api/reader/embed-check, вердикт по
    // заголовкам финального ответа (X-Frame-Options / CSP frame-ancestors). Поле reason —
    // телеметрия/отладка; фронт на него НЕ ветвится — любой исход, кроме явного
    // embeddable:true (включая 401/403/429 и сетевые сбои), означает «не встраивается»
    // и тихо уводит панель в MD-режим.
    embedCheck: (url: string) => checkReaderEmbeddable(url),
  },
};

async function checkReaderEmbeddable(url: string): Promise<{ embeddable: boolean }> {
  try {
    const data = await request<{ embeddable?: unknown }>('/reader/embed-check', {
      method: 'POST',
      body: JSON.stringify({ url }),
    });
    return { embeddable: !!data && data.embeddable === true };
  } catch {
    // Сбой пробы — не исключение для показа, а сигнал идти MD-путём (ADR-006 §1)
    return { embeddable: false };
  }
}

function normalizeReaderError(raw: unknown, httpStatus?: number | null): { code: ReaderErrorCode; httpStatus?: number | null } {
  const code = raw && typeof raw === 'object' && 'code' in raw && typeof (raw as { code?: unknown }).code === 'string'
    ? (raw as { code: string }).code
    : null;
  const known = code != null && READER_ERROR_CODES.has(code as ReaderErrorCode);
  const rawStatus = raw && typeof raw === 'object' && 'httpStatus' in raw
    ? (raw as { httpStatus?: number | null }).httpStatus
    : null;
  return { code: known ? (code as ReaderErrorCode) : 'server-error', httpStatus: rawStatus ?? httpStatus ?? null };
}

const READER_ERROR_CODES = new Set<ReaderErrorCode>([
  'invalid-url', 'local-address', 'dns-failed', 'unreachable', 'tls-invalid',
  'timeout', 'auth-required', 'blocked-by-site', 'not-found', 'server-error',
  'too-many-redirects', 'not-a-page', 'pdf', 'too-large', 'not-readable',
]);

// Backend может сигналить отказ ЛИБО non-2xx статусом (request() бросает исключение,
// тело ошибки летит в err.body), ЛИБО 200 с discriminated union в теле — конвенция ещё
// не устоялась (эндпоинт делает Денис параллельно), поэтому распознаём оба варианта.
async function readReaderPage(url: string): Promise<ReaderReadResult> {
  try {
    const data = await request<ReaderPage | { error: unknown }>('/reader/read', {
      method: 'POST', body: JSON.stringify({ url }),
    });
    if (data && typeof data === 'object' && 'error' in data) {
      return { ok: false, error: normalizeReaderError((data as { error: unknown }).error) };
    }
    return { ok: true, page: data as ReaderPage };
  } catch (e) {
    const err = e as Error & { status?: number; body?: { error?: unknown } };
    return { ok: false, error: normalizeReaderError(err.body?.error, err.status) };
  }
}

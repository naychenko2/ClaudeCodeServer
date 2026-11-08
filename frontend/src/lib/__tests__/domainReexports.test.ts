import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { FROZEN_TYPES_INDEX_REEXPORTS, FROZEN_LIB_API_REEXPORTS } from './typesIndexExports';

// Защёлка реэкспортов домена (S5-B): доменные типы живут в features/<домен>/types.ts,
// их реэкспорт в общие файлы (types/index.ts, lib/api.ts) заморожен — фактический
// список строк `export type * from '…'` обязан быть подмножеством перечня в
// typesIndexExports.ts. НОВЫЙ реэкспорт домена в общий файл — красный: потребители
// нового домена читают типы сразу с адреса features/<домен>/types.ts (разрез
// Александра d358bb59, раздел «Цель линии типов» карты
// docs/research/frontend-api-split-2026-11.md). Удаление реэкспорта — зелёное:
// список может только убывать; при удалении убери строку и из typesIndexExports.ts.
// Форма захвата не каноническая: `;` опционален (в проекте нет semi в eslint/prettier,
// реэкспорт без точки валиден через ASI), ловится и `export type * as ns from '…'`
// (минор ревью R-S5-B). Инвариант тот же: фактический ⊆ замороженный.
const REEXPORT_RE = /^export type \*(?: as \w+)? from ['"]([^'"]+)['"]\s*;?/gm;

const reexports = (rel: string) =>
  [...readFileSync(resolve(__dirname, rel), 'utf-8').matchAll(REEXPORT_RE)].map((m) => m[1]);

const assertFrozen = (file: string, actual: string[], frozen: readonly string[]) => {
  const extra = actual.filter((s) => !frozen.includes(s));
  expect(extra, `новые реэкспорты домена в ${file}: ${extra.join(', ')}`).toEqual([]);
};

describe('реэкспорты домена в общих файлах заморожены (S5-B)', () => {
  it('types/index.ts: ни одного реэкспорта сверх замороженного перечня', () => {
    assertFrozen('types/index.ts', reexports('../../types/index.ts'), FROZEN_TYPES_INDEX_REEXPORTS);
  });
  it('lib/api.ts: ни одного реэкспорта сверх замороженного перечня', () => {
    assertFrozen('lib/api.ts', reexports('../api.ts'), FROZEN_LIB_API_REEXPORTS);
  });
});

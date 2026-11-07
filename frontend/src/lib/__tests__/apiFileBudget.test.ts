import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { FROZEN_TYPES_INDEX_DECLARATIONS } from './typesIndexExports';

// Защёлка роста: lib/api.ts и types/index.ts — горячие точки мержей, их разносят по
// features/<домен>/ (docs/research/frontend-api-split-2026-11.md).
// lib/api.ts — счёт строк: потолок РАВЕН фактической длине (сравнение `toBe`,
// не «≤») — ловит обе стороны: рост файла и задирание потолка вручную. После выноса
// домена пересчитай число под фактическое — вниз. Поднимать потолок нельзя: новый
// код пиши сразу в features/<домен>/api.ts.
// types/index.ts — по ИМЕНАМ: замороженный набор верхнеуровневых объявлений
// (export interface|type|const|function|enum X; реэкспорты доменов `export type *`
// в счёт не идут). Поля в существующих типах растут свободно — строки файла больше
// не держатся; НОВОЕ объявление — красное. При переносе домена (шаги P10…P14)
// убери имена из typesIndexExports.ts — список обязан соответствовать фактическим
// объявлениям файла.
const MAX_API_LINES = 175;

const lines = (rel: string) => readFileSync(resolve(__dirname, rel), 'utf-8').split('\n').length - 1;

// Имена верхнеуровневых объявлений; `export type { X }` и `export type * from`
// не матчатся — имена доменов живут в features/<домен>/types.ts.
const DECLARATION_RE = /^export (?:interface|type|const|function|enum) ([A-Za-z_$][A-Za-z0-9_$]*)/gm;

const declarations = (rel: string) =>
  [...readFileSync(resolve(__dirname, rel), 'utf-8').matchAll(DECLARATION_RE)]
    .map((m) => m[1])
    .sort();

describe('потолки api.ts и types/index.ts', () => {
  it('потолок lib/api.ts равен фактической длине (ловит рост и задирание)', () => {
    expect(lines('../api.ts')).toBe(MAX_API_LINES);
  });
  it('набор объявлений types/index.ts заморожен (новое объявление — красное)', () => {
    expect(declarations('../../types/index.ts')).toEqual(
      [...FROZEN_TYPES_INDEX_DECLARATIONS].sort(),
    );
  });
});

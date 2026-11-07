import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

// Защёлка роста: lib/api.ts и types/index.ts — горячие точки мержей, их разносят по
// features/<домен>/ (docs/research/frontend-api-split-2026-11.md). Потолок РАВЕН фактической
// длине файла: сравнение через `toBe`, не «≤» — защёлка ловит обе стороны: рост файла
// и задирание потолка вручную. После выноса домена пересчитай число под фактическое —
// вниз. Поднимать потолок нельзя: новый код пиши сразу в features/<домен>/api.ts и types.ts.
const MAX_API_LINES = 189;
const MAX_TYPES_LINES = 2036;

const lines = (rel: string) => readFileSync(resolve(__dirname, rel), 'utf-8').split('\n').length - 1;

describe('потолок размера api.ts и types/index.ts', () => {
  it('потолок lib/api.ts равен фактической длине (ловит рост и задирание)', () => {
    expect(lines('../api.ts')).toBe(MAX_API_LINES);
  });
  it('потолок types/index.ts равен фактической длине (ловит рост и задирание)', () => {
    expect(lines('../../types/index.ts')).toBe(MAX_TYPES_LINES);
  });
});

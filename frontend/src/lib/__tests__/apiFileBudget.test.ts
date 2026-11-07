import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

// Защёлка роста: lib/api.ts и types/index.ts — горячие точки мержей, их разносят по
// features/<домен>/ (docs/research/frontend-api-split-2026-11.md). Потолок только СНИЖАЕТСЯ:
// после выноса домена пересчитай число под фактическое. Поднимать потолок нельзя —
// новый код пиши сразу в features/<домен>/api.ts и types.ts.
const MAX_API_LINES = 1558;
const MAX_TYPES_LINES = 2090;

const lines = (rel: string) => readFileSync(resolve(__dirname, rel), 'utf-8').split('\n').length - 1;

describe('потолок размера api.ts и types/index.ts', () => {
  it('lib/api.ts не растёт', () => {
    expect(lines('../api.ts')).toBeLessThanOrEqual(MAX_API_LINES);
  });
  it('types/index.ts не растёт', () => {
    expect(lines('../../types/index.ts')).toBeLessThanOrEqual(MAX_TYPES_LINES);
  });
});

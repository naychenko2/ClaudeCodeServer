import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { FLAGS } from '../featureFlags';

// Контракт фронт/бэк: const FLAGS должен содержать ровно те же ключи,
// что C#-каталог FeatureFlagKeys. Ключи дублируются в двух местах —
// этот тест ловит рассинхрон при добавлении/переименовании флага.

const here = dirname(fileURLToPath(import.meta.url));
const csFile = resolve(here, '../../../../backend/ClaudeHomeServer/Models/FeatureFlag.cs');

function readBackendKeys(): string[] {
  const src = readFileSync(csFile, 'utf-8');
  // Берём только тело класса FeatureFlagKeys — чтобы не зацепить константы из других классов
  const classStart = src.indexOf('class FeatureFlagKeys');
  expect(classStart, 'класс FeatureFlagKeys не найден в FeatureFlag.cs').toBeGreaterThan(-1);
  const bodyStart = src.indexOf('{', classStart);
  const bodyEnd = src.indexOf('}', bodyStart);
  const body = src.slice(bodyStart, bodyEnd);

  const keys: string[] = [];
  for (const m of body.matchAll(/public const string \w+ = "([a-z0-9-]+)"/g)) {
    keys.push(m[1]);
  }
  return keys;
}

describe('контракт фич-флагов фронт/бэк', () => {
  it('C#-каталог содержит хотя бы один ключ (регэксп не сломан)', () => {
    expect(readBackendKeys().length).toBeGreaterThan(0);
  });

  it('множества ключей FLAGS (TS) и FeatureFlagKeys (C#) совпадают', () => {
    const backend = new Set(readBackendKeys());
    const frontend = new Set<string>(Object.values(FLAGS));

    const missingOnFrontend = [...backend].filter(k => !frontend.has(k));
    const missingOnBackend = [...frontend].filter(k => !backend.has(k));

    expect(missingOnFrontend, 'ключи есть в C#-каталоге, но нет в TS FLAGS').toEqual([]);
    expect(missingOnBackend, 'ключи есть в TS FLAGS, но нет в C#-каталоге').toEqual([]);
  });

  it('в FLAGS нет дублирующихся значений', () => {
    const values = Object.values(FLAGS);
    expect(new Set(values).size).toBe(values.length);
  });
});

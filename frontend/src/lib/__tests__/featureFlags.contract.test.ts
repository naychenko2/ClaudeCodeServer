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
  // Берём тело класса FeatureFlagKeys по БАЛАНСУ скобок (а не по первой '}'):
  // иначе любой вложенный блок внутри класса (метод, свойство с телом) обрезал бы
  // тело и молча терял ключи после него → ложнозелёный контракт.
  const classStart = src.indexOf('class FeatureFlagKeys');
  expect(classStart, 'класс FeatureFlagKeys не найден в FeatureFlag.cs').toBeGreaterThan(-1);
  const bodyStart = src.indexOf('{', classStart);
  let depth = 0;
  let bodyEnd = -1;
  for (let i = bodyStart; i < src.length; i++) {
    if (src[i] === '{') depth++;
    else if (src[i] === '}' && --depth === 0) { bodyEnd = i; break; }
  }
  expect(bodyEnd, 'не найдена закрывающая скобка класса FeatureFlagKeys').toBeGreaterThan(-1);
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

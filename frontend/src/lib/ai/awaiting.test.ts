// Проводка в App.tsx (Х-6, хвост ревью Р-Х-4): сам сброс проверен в awaiting.ts, но дефект
// был именно в App — cc-unauthorized не звал resetAiAwaiting(), а logout звал (асимметрия,
// как исправленный в Х-4 дубль/пропуск сброса персонажей). Тест обязан краснеть на снятие
// любого из двух вызовов, иначе проводка снова уедет в прод сломанной.

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, it, expect } from 'vitest';

describe('App.tsx: resetAiAwaiting на обоих путях выхода (Х-6)', () => {
  const src = readFileSync(fileURLToPath(new URL('../../App.tsx', import.meta.url)), 'utf8');

  // Тело функции берём по балансу скобок (паттерн useCharacters.test.ts /
  // featureFlags.contract.test.ts): первая '}' внутри вложенного блока обрезала бы
  // тело и молча теряла вызовы после него.
  function bodyOf(header: string): string {
    const start = src.indexOf(header);
    expect(start, `в App.tsx нет «${header}»`).toBeGreaterThan(-1);
    let depth = 0;
    for (let i = src.indexOf('{', start); i < src.length; i++) {
      if (src[i] === '{') depth++;
      else if (src[i] === '}' && --depth === 0) return src.slice(start, i + 1);
    }
    throw new Error(`App.tsx: не найден конец «${header}»`);
  }

  const callsOf = (body: string) => body.match(/resetAiAwaiting\(\)/g)?.length ?? 0;

  it('cc-unauthorized: ровно один вызов (снятие его из cc-unauthorized краснит этот тест)', () => {
    expect(callsOf(bodyOf('const onUnauthorized = () => {'))).toBe(1);
  });

  it('logout: ровно один вызов (поведение обычного выхода не меняется)', () => {
    expect(callsOf(bodyOf('const logout = () => {'))).toBe(1);
  });
});

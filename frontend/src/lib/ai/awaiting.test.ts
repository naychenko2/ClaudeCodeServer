// Два блока об одном сторе — «ждущие чаты».
// Проводка в App.tsx (Х-6, хвост ревью Р-Х-4): дефект был именно в App — cc-unauthorized
// не звал resetAiAwaiting(), logout звал (асимметрия, как исправленный в Х-4 дубль/пропуск
// сброса персонажей); те тесты краснеют на снятие любого из двух вызовов.
// Поведение самого сброса — очистка карты ожиданий и меты, пустой снимок — проверено
// блоком ниже (находка Р-Х-6: раньше его не проверял ни один тест).

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, it, expect, vi, beforeEach } from 'vitest';

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

// --- Сам сброс (Х-12) ---

// SignalR/api/offline/visiblePoll мокаем целиком: стор подписывается на onMessage/
// onReconnected и читает подписи из api.home.summary; соединения и опроса в тестах нет.
vi.mock('../signalr', () => ({
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));
vi.mock('../api', () => ({
  api: { home: { summary: vi.fn(() => Promise.resolve({ active: [] })) } },
}));
vi.mock('../offline', () => ({ readStoredUserId: vi.fn(() => null) }));
vi.mock('../visiblePoll', () => ({ startVisiblePoll: vi.fn(() => () => {}) }));
// Хук стора вне React: отдаёт текущий снимок (паттерн architectureStore.test.ts)
vi.mock('react', () => ({ useSyncExternalStore: (_s: unknown, get: () => unknown) => get() }));

import type { HomeSessionInfo, ServerMessage } from '../../types';
import { api } from '../api';
import { onMessage } from '../signalr';
import { ensureAiAwaitingLoaded, resetAiAwaiting, useAiAwaiting } from './awaiting';

// Обработчик, который стор передал в onMessage при старте (паттерн agentsPresence.test.ts)
function handler(): (msg: ServerMessage) => void {
  const calls = vi.mocked(onMessage).mock.calls;
  return calls[calls.length - 1][0] as (msg: ServerMessage) => void;
}

const chat = (id: string, name: string, projectId: string | null = null) =>
  ({ id, name, projectId, status: 'waiting', origin: 'local', messageCount: 0, updatedAt: '2026-01-01T00:00:00Z' }) as unknown as HomeSessionInfo;

const statusChanged = (sessionId: string, status: string) =>
  ({ type: 'status_changed', sessionId, status }) as unknown as ServerMessage;

// Начальное состояние через сводку — единственный источник меты, как при загрузке страницы
const seed = (active: HomeSessionInfo[]) => {
  vi.mocked(api.home.summary).mockResolvedValue({ active });
  return ensureAiAwaitingLoaded();
};

describe('resetAiAwaiting: сам сброс (Х-12)', () => {
  beforeEach(async () => {
    // Детерминированная точка отсчёта: сброс гасит мету, пустая сводка — состав
    resetAiAwaiting();
    await seed([]);
  });

  it('после сброса снимок пуст: ждущие чаты не остаются', async () => {
    await seed([chat('c1', 'Чат Один', 'p1')]);
    expect(useAiAwaiting()).toHaveLength(1); // начало теста: что сбрасывать есть

    resetAiAwaiting();
    expect(useAiAwaiting()).toEqual([]);
  });

  it('карта ожиданий пуста: ждавший до сброса чат не возвращается с новым событием', async () => {
    await seed([chat('c1', 'Чат Один', 'p1')]);
    resetAiAwaiting();

    // Если reset не очистил карту, старый чат вернулся бы в снимке рядом с новым
    handler()(statusChanged('c2', 'waiting'));
    expect(useAiAwaiting()).toEqual([{ chatId: 'c2', name: '', projectId: null }]);
  });

  it('мета прежнего пользователя не остаётся: чат после сброса получает пустую подпись', async () => {
    await seed([chat('c1', 'Старое Имя', 'p1')]);
    resetAiAwaiting();

    // Тот же чат входит снова по realtime: подпись берётся из меты — осталась бы мета
    // прежнего пользователя, в балуне светилось бы «Старое Имя» и его проект
    handler()(statusChanged('c1', 'waiting'));
    expect(useAiAwaiting()).toEqual([{ chatId: 'c1', name: '', projectId: null }]);
  });
});

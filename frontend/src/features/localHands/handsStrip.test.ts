import { beforeEach, describe, expect, it } from 'vitest';

// Окружение node — localStorage нет; мокаем минимальную реализацию на Map
const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;

import { __resetComposerStrips, getActiveStrip, getPendingFocus, selectStrip } from '../../lib/composerStrips';
import { setAllFlags } from '../../lib/featureFlags';
import { getSlotContributions, SLOT_COMPOSER_STRIP, type ComposerStripApi } from '../../lib/subsystems/registryCore';
import type { ServerMessage } from '../../types';
import {
  __resetHandsStrip, HANDS_STRIP, handsStatusLoaded, handsStripOnMessage, setHandsProject,
} from './handsStrip';
import './handsStripManifest';

const P = 'p1';
const S = 's1';

const hands = (state: string, reason?: string) =>
  ({ sessionId: S, type: 'hands_status', state, reason }) as ServerMessage;

// Состав полос так, как его собирает хост: Git (встроенная) плюс доступные вклады реестра
function available(): string[] {
  const strips = getSlotContributions<never, ComposerStripApi>(SLOT_COMPOSER_STRIP)
    .filter(c => c.action?.isAvailable?.({ projectId: P, sessionId: S }) ?? true)
    .map(c => c.name!);
  return ['git', ...strips];
}
const active = () => getActiveStrip(S, available());

beforeEach(() => {
  store.clear();
  __resetComposerStrips();
  __resetHandsStrip();
  setAllFlags({ 'local-hands': true });
  setHandsProject(P, { available: true, deviceName: 'Ноутбук' });
});

describe('полоса «Руки» — фокус', () => {
  it('active → полоса запрошена', () => {
    handsStripOnMessage(S, hands('active'));
    expect(active()).toBe(HANDS_STRIP);
  });

  it('первая отрисовка с active тоже запрашивает полосу', () => {
    handsStatusLoaded(S, { state: 'active', reason: null, deviceName: 'Ноутбук' });
    expect(active()).toBe(HANDS_STRIP);
  });

  it('stopped → полоса отпущена, возврат к прежней', () => {
    handsStripOnMessage(S, hands('active'));
    handsStripOnMessage(S, hands('stopped', 'tray-stop'));
    expect(active()).toBe('git');
  });

  it('allowed, unavailable и конец хода отпускают полосу', () => {
    for (const end of [hands('allowed'), hands('unavailable', 'busy'),
      { sessionId: S, type: 'result', subtype: 'success', durationMs: 1, numTurns: 1 } as ServerMessage]) {
      handsStripOnMessage(S, hands('active'));
      expect(active()).toBe(HANDS_STRIP);
      handsStripOnMessage(S, end);
      expect(active()).toBe('git');
    }
  });

  it('после ручного ухода повторный active полосу не навязывает, на «▾» точка', () => {
    handsStripOnMessage(S, hands('active'));
    selectStrip(S, 'git');
    handsStripOnMessage(S, hands('active'));
    expect(active()).toBe('git');
    // Условие точки на «▾» в ComposerStripHost: pendingFocus && pendingFocus !== active
    expect(getPendingFocus(S)).toBe(HANDS_STRIP);
  });

  it('событие чужого чата фокус не трогает', () => {
    handsStripOnMessage(S, { ...hands('active'), sessionId: 'other' } as ServerMessage);
    expect(active()).toBe('git');
  });
});

describe('полоса «Руки» — доступность', () => {
  it('без флага local-hands полосы нет', () => {
    setAllFlags({});
    handsStripOnMessage(S, hands('active'));
    expect(available()).not.toContain(HANDS_STRIP);
    expect(active()).toBe('git');
  });

  it('при handsRefusal проекта полосы нет', () => {
    setHandsProject(P, { available: false, deviceName: 'Ноутбук' });
    expect(available()).not.toContain(HANDS_STRIP);
  });

  it('сервер ответил «у чата рук нет» — полосы нет', () => {
    handsStatusLoaded(S, { state: null });
    expect(available()).not.toContain(HANDS_STRIP);
  });

  it('с флагом и доступными руками полоса есть — вклад каркаса не зависит от тумблера подсистем', () => {
    expect(available()).toContain(HANDS_STRIP);
  });
});

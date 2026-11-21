// X-W4-37: детская поверхность не дёргает ни одной из двух ручек сторожа
// (/local-model/stats и /stats/media не детские — инвентаризация X-W4-36, ребёнку
// был 403 каждые 60 с). Сторож смонтирован на странице всегда и без пропов, поэтому
// тест рендерит пробы-компонент: детский me → ни один fetch не позван (и админ-путь
// тоже: /engines закрыт ребёнку так же); взрослый — спрашивает своё, как прежде.
import { act, createElement, type ReactElement } from 'react';
import type { Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Частичный мок: lib/api spread'ом подмешивает localModelApi из этого модуля в свой api
// (его импортирует defaultPersona) — снимать этот экспорт целиком нельзя, только fetch-функции
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  fetchLocalModelStats: vi.fn(),
  fetchLocalModelMedia: vi.fn(),
  fetchLocalModelEngines: vi.fn(),
}));
// defaultPersona при публикации me вешает realtime-подписку на signalr — в node соединения нет
vi.mock('../../lib/signalr', () => ({ onMessage: () => () => {} }));

import { fetchLocalModelEngines, fetchLocalModelMedia, fetchLocalModelStats } from './api';
import { useLocalModelWatcher } from './useLocalModelWatcher';
import { setLocalModelConfigured } from './useLocalModelStats';
import { installFakeDom, mount, uninstallFakeDom } from '../../components/chat/__tests__/fakeDom';
import { clearMe, setMeFromServer } from '../../lib/defaultPersona';
import type { Me } from '../../types';

function probe(isAdmin: boolean): ReactElement {
  function Probe(): null { useLocalModelWatcher(isAdmin); return null; }
  return createElement(Probe);
}

// root — в модульной переменной и снимается в afterEach: красный кейс (ассерт до конца
// тела) оставлял поллер смонтированным, и его интервал бил по следующему кейсу
// («взрослый админ /engines»: 2 вызова вместо 1) — доработка X-W4-37
let root: Root | null = null;

async function mountWatcher(isChild: boolean, isAdmin: boolean) {
  setMeFromServer({ userId: isChild ? 'u-child' : 'u-adult', isChild } as Me);
  root = mount(probe(isAdmin)).root;
  await act(async () => { await Promise.resolve(); }); // промисы первого тика доехали
}

beforeEach(() => {
  vi.useFakeTimers();
  installFakeDom();
  // startConfiguredPolling/startEngineStatePolling ставят таймеры через window.setInterval;
  // под vi.useFakeTimers() глобальные таймеры уже фейковые — прокидываем их в window, как в браузере
  const win = globalThis as any;
  win.window.setInterval = (cb: any, ms?: number) => setInterval(cb, ms);
  win.window.clearInterval = (id: any) => clearInterval(id as number);
  // ответы не значатся для теста (речь про сам вызов) — но должны быть промисами
  vi.mocked(fetchLocalModelStats).mockResolvedValue({ configured: false } as never);
  vi.mocked(fetchLocalModelMedia).mockResolvedValue({ configured: false } as never);
  vi.mocked(fetchLocalModelEngines).mockResolvedValue([]);
  setLocalModelConfigured(null);
  clearMe();
});

afterEach(async () => {
  // размонтирование здесь, а не в конце кейса: падение кейса не должно красить соседа
  const r = root; root = null;
  if (r) await act(async () => { r.unmount(); });
  clearMe();
  setLocalModelConfigured(null);
  vi.useRealTimers();
  uninstallFakeDom();
  vi.clearAllMocks();
});

describe('useLocalModelWatcher: ребёнок не опрашивает взрослые ручки (X-W4-37)', () => {
  it('ребёнок-админ: ни /stats, ни /stats/media, ни /engines', async () => {
    await mountWatcher(true, true);
    expect(fetchLocalModelStats, '/stats позван ребёнку').not.toHaveBeenCalled();
    expect(fetchLocalModelMedia, '/stats/media позван ребёнку').not.toHaveBeenCalled();
    expect(fetchLocalModelEngines, '/engines позван ребёнку').not.toHaveBeenCalled();
  });

  it('ребёнок-не-админ: тот же результат', async () => {
    await mountWatcher(true, false);
    expect(fetchLocalModelStats).not.toHaveBeenCalled();
    expect(fetchLocalModelMedia).not.toHaveBeenCalled();
    expect(fetchLocalModelEngines).not.toHaveBeenCalled();
  });

  it('взрослый админ: /stats + /engines как прежде, /stats/media не трогает', async () => {
    await mountWatcher(false, true);
    expect(fetchLocalModelStats, 'админ не спросил /stats').toHaveBeenCalledTimes(1);
    expect(fetchLocalModelEngines, 'админ не спросил /engines').toHaveBeenCalledTimes(1);
    expect(fetchLocalModelMedia).not.toHaveBeenCalled();
  });

  it('взрослый не-админ: /stats/media как прежде, /stats и /engines не трогает', async () => {
    await mountWatcher(false, false);
    expect(fetchLocalModelMedia, 'не-админ не спросил /stats/media').toHaveBeenCalledTimes(1);
    expect(fetchLocalModelStats).not.toHaveBeenCalled();
    expect(fetchLocalModelEngines).not.toHaveBeenCalled();
  });
});

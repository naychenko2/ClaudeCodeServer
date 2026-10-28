// Манифест «Локальные модели»: ленивая панель в слоте workspace-panel-def.
// Рендер слота прямым вызовом (без Suspense-границы в ядре) — поэтому граница
// живёт в самом манифесте. Тест: чанк ещё не загрузился (компонент suspend-ит) →
// рендер не падает и не оставляет пустой корень (показывается заглушка-каркас).
// Мутация: убрать <Suspense> из манифеста → тест красный (suspended, no fallback).

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { FLAGS, setFlagLocal } from '../../lib/featureFlags';
import { setLocalModelAdmin, setLocalModelConfigured } from './useLocalModelStats';
import { getLocalModelBonsai, setLocalModelBonsai, startBonsaiStatePolling } from './useLocalModelWatcher';
import { mockBonsaiStats } from './localModel.fixtures';
import type { WorkspacePanelDefApi, WorkspacePanelDefCtx } from '../../lib/subsystems/registryCore';

// Дефолтный fetch лёгкого опроса (engine/window) проверяется через подмену api:
// тест видит, К КАКОЙ дорожке идёт запрос, не подставляя свой fetch.
const apiSpy = vi.hoisted(() => ({ stats: vi.fn() }));
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  fetchLocalModelStats: (...args: unknown[]) => { apiSpy.stats(...args); return Promise.resolve(mockBonsaiStats('ready')); },
}));

// Компонент, который «suspend»ит: бросает never-resolving проми. vi.hoisted — фабрика
// vi.mock (hoisted) не видит обычные top-level константы.
const suspender = vi.hoisted(() => {
  const pending = new Promise<never>(() => {});
  return function Suspender(): null { throw pending; };
});

vi.mock('./LocalModelPanel', () => ({ LocalModelPanel: suspender }));

const { manifest } = await import('./manifest');

const ctx: WorkspacePanelDefCtx = {
  projectId: 'p1', sessionId: 's1', isMobile: false, onClose: () => {},
};

describe('localModel/manifest: Suspense вокруг ленивной панели', () => {
  it('рендер слота с незагруженным чанком не падает и не оставляет пустой корень', () => {
    const entry = manifest.slots?.['workspace-panel-def']?.[0];
    expect(entry?.render, 'вклад workspace-panel-def не найден').toBeTruthy();
    // Suspense-граница: suspend-компонент → заглушка-каркас (не пустой корень), без падения
    const html = renderToStaticMarkup(entry!.render!(ctx) as Parameters<typeof renderToStaticMarkup>[0]);
    expect(html.length, 'корень пуст — Suspense-граница не держит макет').toBeGreaterThan(0);
  });
});

// === Ф6: значок и точка состояния ===
// Геттеры isAvailable/indicator читают модульные сторы (их кормит сторож страницы);
// тест ставит сторы сам — каждый свой, и обнуляет после (сторы живут на файл).
const panelAction = () => manifest.slots?.['workspace-panel-def']?.[0]?.action as unknown as WorkspacePanelDefApi;

describe('localModel/manifest: значок при «Bonsai без vLLM» (макет §8.6)', () => {
  beforeEach(() => { setFlagLocal(FLAGS.localModelStats, true); setLocalModelAdmin(true); setLocalModelConfigured(null); setLocalModelBonsai(null); });
  afterEach(() => { setFlagLocal(FLAGS.localModelStats, false); setLocalModelAdmin(false); setLocalModelConfigured(null); setLocalModelBonsai(null); });

  it('только Bonsai (main не настроен) — значок есть: configured читает и вторую дорожку', () => {
    setLocalModelConfigured(false);
    setLocalModelBonsai(mockBonsaiStats('ready'));
    expect(panelAction().isAvailable?.('p1')).toBe(true);
  });

  it('ничего не настроено — значка нет', () => {
    expect(panelAction().isAvailable?.('p1')).toBe(false);
  });

  it('админ без флага — значка нет, даже когда Bonsai настроена', () => {
    setFlagLocal(FLAGS.localModelStats, false);
    setLocalModelBonsai(mockBonsaiStats('ready'));
    expect(panelAction().isAvailable?.('p1')).toBe(false);
  });
});

describe('localModel/manifest: точка индикатора на значке (Ф6)', () => {
  beforeEach(() => { setFlagLocal(FLAGS.localModelStats, true); setLocalModelAdmin(true); setLocalModelConfigured(true); setLocalModelBonsai(null); });
  afterEach(() => { setFlagLocal(FLAGS.localModelStats, false); setLocalModelAdmin(false); setLocalModelConfigured(null); setLocalModelBonsai(null); });

  it('без флага — точки нет (состояние не опрашивается)', () => {
    setFlagLocal(FLAGS.localModelStats, false);
    setLocalModelBonsai(mockBonsaiStats('yielded'));
    expect(panelAction().indicator?.()).toBeNull();
  });

  it('не-админ — точки нет, даже когда панель видима (состояние известно только админу)', () => {
    setLocalModelAdmin(false);
    setLocalModelBonsai(mockBonsaiStats('yielded'));
    expect(panelAction().isAvailable?.(null)).toBe(true);
    expect(panelAction().indicator?.()).toBeNull();
  });

  it('опрос ещё не ответил — точки нет (не выдумываем состояние)', () => {
    expect(panelAction().indicator?.()).toBeNull();
  });

  it('Bonsai работает, арбитр отвечает — точки нет', () => {
    setLocalModelBonsai(mockBonsaiStats('ready'));
    expect(panelAction().indicator?.()).toBeNull();
  });

  it('уступка по плану — muted-точка и подсказка (макет §4)', () => {
    setLocalModelBonsai(mockBonsaiStats('yielded'));
    expect(panelAction().indicator?.()).toEqual({ dot: 'muted', hint: 'Bonsai уступила карту' });
  });

  it('ждёт память карты — warning и «ждёт память карты»', () => {
    setLocalModelBonsai(mockBonsaiStats('waiting-vram'));
    expect(panelAction().indicator?.()).toEqual({ dot: 'warning', hint: 'Bonsai ждёт память карты' });
  });

  it('авария — warning и «Bonsai недоступна»', () => {
    setLocalModelBonsai(mockBonsaiStats('unknown'));
    expect(panelAction().indicator?.()).toEqual({ dot: 'warning', hint: 'Bonsai недоступна' });
  });

  it('арбитр недоступен (метрики отвечают, /state молчит) — warning и «арбитр не отвечает» (хвост ревью Ф2 4)', () => {
    const ready = mockBonsaiStats('ready');
    setLocalModelBonsai({ ...ready, duty: { ...ready.duty!, arbiter: { ...ready.duty!.arbiter!, reachable: false } } });
    expect(panelAction().indicator?.()).toEqual({ dot: 'warning', hint: 'Bonsai: арбитр не отвечает' });
  });
});

describe('startBonsaiStatePolling: лёгкий 15-с опрос второй дорожки (Ф6)', () => {
  beforeEach(() => { vi.useFakeTimers(); setLocalModelBonsai(null); apiSpy.stats.mockClear(); });
  afterEach(() => { vi.useRealTimers(); setLocalModelBonsai(null); });

  it('дефолтный запрос — вторая дорожка: window лёгкий, engine=bonsai (не основной стенд)', async () => {
    const stop = startBonsaiStatePolling(15_000, undefined, (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id as ReturnType<typeof setInterval>));
    await vi.advanceTimersByTimeAsync(0);
    expect(apiSpy.stats).toHaveBeenCalledWith('1m', undefined, 'bonsai');
    expect(getLocalModelBonsai()?.status).toBeTruthy();
    stop();
  });

  it('первый ответ кладёт снимок; сбой сохраняет прежний; остановка гасит таймер', async () => {
    const s1 = mockBonsaiStats('ready');
    let n = 0;
    const stop = startBonsaiStatePolling(
      15_000,
      async () => { if (++n === 1) return s1; throw new Error('сбой'); },
      (fn, ms) => setInterval(fn, ms),
      (id) => clearInterval(id as ReturnType<typeof setInterval>),
    );
    expect(n).toBe(1);
    await vi.advanceTimersByTimeAsync(0);
    expect(getLocalModelBonsai()).toBe(s1);
    await vi.advanceTimersByTimeAsync(15_000); // второй тик — сбой: прежнее значение
    expect(getLocalModelBonsai()).toBe(s1);
    stop();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(n).toBe(2); // после остановки новых запросов нет
  });
});

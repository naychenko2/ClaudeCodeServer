// Манифест «Локальные модели»: ленивая панель в слоте workspace-panel-def.
// Рендер слота прямым вызовом (без Suspense-границы в ядре) — поэтому граница
// живёт в самом манифесте. Тест: чанк ещё не загрузился (компонент suspend-ит) →
// рендер не падает и не оставляет пустой корень (показывается заглушка-каркас).
// Мутация: убрать <Suspense> из манифеста → тест красный (suspended, no fallback).
// Плюс Ф6: имя панели «Ферма» зафиксировано тестом — по Ф5 его не проверял ни один
// тест, и возврат старого слова прошёл бы незамеченным.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { PANEL_META } from '../../pages/workspace/panelCatalog';
import { setLocalModelAdmin, setLocalModelConfigured } from './useLocalModelStats';
import { getLocalModelEngineStats, setLocalModelEngineStats, startEngineStatePolling } from './useLocalModelWatcher';
import { mockEngineStats } from './localModel.fixtures';
import type { WorkspacePanelDefApi, WorkspacePanelDefCtx } from '../../lib/subsystems/registryCore';

// Дефолтный fetch лёгкого опроса (engine/window) проверяется через подмену api:
// тест видит, К КАКОЙ дорожке идёт запрос, не подставляя свой fetch.
const apiSpy = vi.hoisted(() => ({ stats: vi.fn(), engines: vi.fn() }));
vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('./api')>()),
  fetchLocalModelStats: (...args: unknown[]) => { apiSpy.stats(...args); return Promise.resolve(mockEngineStats('ready')); },
  fetchLocalModelEngines: () => { apiSpy.engines(); return Promise.resolve([
    { name: 'main', title: 'Flash-Next', configured: true }, { name: 'gemma', title: 'Gemma 4', configured: true }]); },
}));

// Компонент, который «suspend»ит: бросает never-resolving проми. vi.hoisted — фабрика
// vi.mock (hoisted) не видит обычные top-level константы.
const suspender = vi.hoisted(() => {
  const pending = new Promise<never>(() => {});
  return function Suspender(): null { throw pending; };
});

vi.mock('./LocalModelPanel', () => ({ LocalModelPanel: suspender }));

const { LOCAL_MODEL_PANEL, manifest } = await import('./manifest');

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

describe('localModel/manifest: значок при «второй движок без vLLM» (макет §8.6)', () => {
  beforeEach(() => { setLocalModelAdmin(true); setLocalModelConfigured(null); setLocalModelEngineStats(null); });
  afterEach(() => { setLocalModelAdmin(false); setLocalModelConfigured(null); setLocalModelEngineStats(null); });

  it('только второй движок (main не настроен) — значок есть: configured читает и вторую дорожку', () => {
    setLocalModelConfigured(false);
    setLocalModelEngineStats(mockEngineStats('ready'));
    expect(panelAction().isAvailable?.('p1')).toBe(true);
  });

  it('ничего не настроено — значка нет', () => {
    expect(panelAction().isAvailable?.('p1')).toBe(false);
  });

});

// === Ф6: имя панели «Ферма» (решение Андрея 18.11) ===
// Имя живёт в двух реестрах и расходиться не должно: манифест (заголовок вклада
// панели → кнопка рельсы) и каталог панелей (мету которой читает PanelZone —
// заголовок шапки панели, PanelZone.tsx:892). Ни один тест Ф5 имя не проверял.
describe('localModel/manifest: имя панели «Ферма»', () => {
  it('манифест и его вклад панели называются «Ферма»', () => {
    expect(manifest.title).toBe('Ферма');
    expect(panelAction().title).toBe('Ферма');
  });
  it('имя манифеста совпадает с метой каталога панелей (шапка рисует её)', () => {
    expect(PANEL_META[LOCAL_MODEL_PANEL].title).toBe('Ферма');
    expect(panelAction().title).toBe(PANEL_META[LOCAL_MODEL_PANEL].title);
  });
});

describe('localModel/manifest: точка индикатора на значке (Ф6)', () => {
  beforeEach(() => { setLocalModelAdmin(true); setLocalModelConfigured(true); setLocalModelEngineStats(null); });
  afterEach(() => { setLocalModelAdmin(false); setLocalModelConfigured(null); setLocalModelEngineStats(null); });

  it('не-админ — точки нет, даже когда панель видима (состояние известно только админу)', () => {
    setLocalModelAdmin(false);
    setLocalModelEngineStats(mockEngineStats('yielded'));
    expect(panelAction().isAvailable?.(null)).toBe(true);
    expect(panelAction().indicator?.()).toBeNull();
  });

  it('опрос ещё не ответил — точки нет (не выдумываем состояние)', () => {
    expect(panelAction().indicator?.()).toBeNull();
  });

  it('Модель работает, арбитр отвечает — точки нет', () => {
    setLocalModelEngineStats(mockEngineStats('ready'));
    expect(panelAction().indicator?.()).toBeNull();
  });

  it('уступка по плану — muted-точка и подсказка с короткой причиной (макет §4)', () => {
    // фикстура 'yielded' имеет reasons=[comfy video] — короткая форма «видео» (KINDS макета)
    setLocalModelEngineStats(mockEngineStats('yielded'));
    expect(panelAction().indicator?.()).toEqual({ dot: 'muted', hint: 'Модель уступила карту: видео' });
  });

  it('ожидание возврата — muted-точка и «Модель вернётся через 2:40» (макет §4)', () => {
    setLocalModelEngineStats(mockEngineStats('returning'));
    expect(panelAction().indicator?.()).toEqual({ dot: 'muted', hint: 'Модель вернётся через 2:40' });
  });

  it('ждёт память карты — warning и «ждёт память карты»', () => {
    setLocalModelEngineStats(mockEngineStats('waiting-vram'));
    expect(panelAction().indicator?.()).toEqual({ dot: 'warning', hint: 'Модель ждёт память карты' });
  });

  it('авария — warning и «Модель недоступна»', () => {
    setLocalModelEngineStats(mockEngineStats('unknown'));
    expect(panelAction().indicator?.()).toEqual({ dot: 'warning', hint: 'Модель недоступна' });
  });

  it('арбитр недоступен (метрики отвечают, /state молчит) — warning и «арбитр не отвечает» (хвост ревью Ф2 4)', () => {
    const ready = mockEngineStats('ready');
    setLocalModelEngineStats({ ...ready, duty: { ...ready.duty!, arbiter: { ...ready.duty!.arbiter!, reachable: false } } });
    expect(panelAction().indicator?.()).toEqual({ dot: 'warning', hint: 'Модель: арбитр не отвечает' });
  });
});

describe('startEngineStatePolling: лёгкий 15-с опрос второй дорожки (Ф6)', () => {
  beforeEach(() => { vi.useFakeTimers(); setLocalModelEngineStats(null); apiSpy.stats.mockClear(); apiSpy.engines.mockClear(); });
  afterEach(() => { vi.useRealTimers(); setLocalModelEngineStats(null); });
  const timers = [(fn: () => void, ms: number) => setInterval(fn, ms), (id: unknown) => clearInterval(id as ReturnType<typeof setInterval>)] as const;

  it('дефолтный запрос — вторая дорожка: имя движка из /engines (не main), window лёгкий', async () => {
    const stop = startEngineStatePolling(15_000, undefined, undefined, ...timers);
    await vi.advanceTimersByTimeAsync(0);
    expect(apiSpy.engines).toHaveBeenCalled();
    expect(apiSpy.stats).toHaveBeenCalledWith('1m', undefined, 'gemma');
    expect(getLocalModelEngineStats()?.status).toBeTruthy();
    stop();
  });

  it('второго движка нет (только main) — снимок null, /stats не спрашивается', async () => {
    setLocalModelEngineStats(mockEngineStats('ready'));
    const stats = vi.fn();
    const stop = startEngineStatePolling(15_000, async () => [{ name: 'main', title: 'Flash-Next', configured: true }], stats, ...timers);
    await vi.advanceTimersByTimeAsync(0);
    expect(stats).not.toHaveBeenCalled();
    expect(getLocalModelEngineStats()).toBeNull();
    stop();
  });

  it('первый ответ кладёт снимок; сбой сохраняет прежний; остановка гасит таймер', async () => {
    const s1 = mockEngineStats('ready');
    let n = 0;
    const stop = startEngineStatePolling(
      15_000,
      async () => [{ name: 'main', title: 'Flash-Next', configured: true }, { name: 'gemma', title: 'Gemma 4', configured: true }],
      async () => { if (++n === 1) return s1; throw new Error('сбой'); },
      ...timers,
    );
    await vi.advanceTimersByTimeAsync(0);
    expect(n).toBe(1);
    expect(getLocalModelEngineStats()).toBe(s1);
    await vi.advanceTimersByTimeAsync(15_000); // второй тик — сбой: прежнее значение
    expect(getLocalModelEngineStats()).toBe(s1);
    stop();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(n).toBe(2); // после остановки новых запросов нет
  });
});

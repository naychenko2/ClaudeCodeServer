// Стор страницы фичи (волна 2 «Обзор фич», шаг Ф2.2).
//
// Окружение node (без jsdom): effects не идут, поэтому тестируем ЛОГИКУ стора
// через тестовые швы `__test`, а не React-хук (тот же приём, что в featureRuns.test.ts).
// Блоки:
//   1. Состояния: 200 → ok, 404 → notFound, 503 → disabled, прочий сбой → error.
//   2. Живое обновление: чужое событие не перезапрашивает; три своих подряд — один запрос.
//   3. Мутация: сломанный фильтр/дебаунс красит тест.

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  __test,
  FEATURE_MAIN_TAB_LABEL,
  getFeatureMainTab,
  setFeatureMainTab,
  setFeaturePageTab,
  stepsTabCount,
  toMainTab,
} from './featurePage';
import { api } from './api';
import type { Task } from '../types';
import type { FeaturePage } from '../types/featureIndex';
import type { FeatureRun } from '../types/featureRun';
import { BRIEF } from './featurePage.contract';

// showToast диспатчит window-событие, в node-окружении его нет — no-op-мок на случай
// транзитного импорта (сам стор фичи тост больше не шлёт).
vi.mock('./toast', () => ({ showToast: () => {} }));


function task(over: Partial<Task>): Task {
  return {
    id: 't', title: 'Задача', description: '', status: 'todo', priority: 'medium',
    linkedFiles: [], subtasks: [], labels: [], order: 1, createdAt: '', updatedAt: '',
    ...over,
  } as Task;
}

// Минимальная валидная страница (run + events + eventsTotal).
function page(fid: string): FeaturePage {
  return {
    run: {
      featureId: fid,
      epic: { id: fid, title: `Эпик ${fid}` },
      steps: [], reviews: [], qa: [], final: null, merge: null,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
    },
    events: [],
    eventsTotal: 0,
  };
}

// ── 1. Состояния: 200 → ok, 404 → notFound, 503 → disabled, прочий → error ─────

describe('состояния страницы фичи', () => {
  let getFeatureSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.useFakeTimers();
    __test.reset();
  });
  afterEach(() => {
    vi.restoreAllMocks();
    __test.reset();
    vi.useRealTimers();
  });

  it('200 → ok (run + events + eventsTotal)', async () => {
    getFeatureSpy = vi.spyOn(api.tasks, 'getFeature').mockResolvedValue(page('epic-1'));
    __test.setFid('epic-1');
    __test.fetchPage('epic-1', true);
    await vi.runAllTimersAsync();
    expect(__test.state()).toEqual({ status: 'ok', page: page('epic-1') });
  });

  it('404 → notFound', async () => {
    const err = new Error('Не найдено') as Error & { status?: number };
    err.status = 404;
    getFeatureSpy = vi.spyOn(api.tasks, 'getFeature').mockRejectedValue(err);
    __test.setFid('epic-1');
    __test.fetchPage('epic-1', true);
    await vi.runAllTimersAsync();
    expect(__test.state()).toEqual({ status: 'notFound' });
  });

  it('503 → disabled (подсистема Tasks выключена)', async () => {
    const err = new Error('Подсистема выключена') as Error & { status?: number };
    err.status = 503;
    getFeatureSpy = vi.spyOn(api.tasks, 'getFeature').mockRejectedValue(err);
    __test.setFid('epic-1');
    __test.fetchPage('epic-1', true);
    await vi.runAllTimersAsync();
    expect(__test.state()).toEqual({ status: 'disabled' });
  });

  it('прочий сбой (500) → error', async () => {
    const err = new Error('Внутренняя ошибка') as Error & { status?: number };
    err.status = 500;
    getFeatureSpy = vi.spyOn(api.tasks, 'getFeature').mockRejectedValue(err);
    __test.setFid('epic-1');
    __test.fetchPage('epic-1', true);
    await vi.runAllTimersAsync();
    expect(__test.state()).toEqual({ status: 'error' });
  });
});

// ── 2. Живое обновление: чужое событие не перезапрашивает, три своих подряд — один запрос ──

describe('живое обновление по task_changed', () => {
  let getFeatureSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.useFakeTimers();
    __test.reset();
    getFeatureSpy = vi.spyOn(api.tasks, 'getFeature').mockResolvedValue(page('epic-1'));
  });
  afterEach(() => {
    getFeatureSpy.mockRestore();
    __test.reset();
    vi.useRealTimers();
  });

  it('чужое событие (featureId ≠ fid) — не перезапрашивает', () => {
    __test.setFid('epic-1');
    // событие чужой фичи (epic-2) — наша страница (epic-1) его не видит
    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'step-1', featureId: 'epic-2' }) });
    // дебаунс не запланирован
    expect(__test.pending()).toBe(false);
    // даже после окна дебаунса запрос не уходит
    vi.advanceTimersByTime(1500);
    expect(getFeatureSpy).not.toHaveBeenCalled();
  });

  it('обычная задача (без featureId/id) — не перезапрашивает', () => {
    __test.setFid('epic-1');
    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'plain' }) });
    expect(__test.pending()).toBe(false);
    vi.advanceTimersByTime(1500);
    expect(getFeatureSpy).not.toHaveBeenCalled();
  });

  it('свой шаг (featureId === fid) — перезапрос после дебаунса', () => {
    __test.setFid('epic-1');
    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'step-1', featureId: 'epic-1' }) });
    // дебаунс запланирован
    expect(__test.pending()).toBe(true);
    // до окна — запрос ещё не ушёл
    vi.advanceTimersByTime(1499);
    expect(getFeatureSpy).not.toHaveBeenCalled();
    // после окна — один запрос (signal не передаётся: live-перезапрос не отменяется)
    vi.advanceTimersByTime(1);
    expect(getFeatureSpy).toHaveBeenCalledTimes(1);
    expect(getFeatureSpy).toHaveBeenCalledWith('epic-1', undefined, undefined);
  });

  it('сам эпик (id === fid) — перезапрос', () => {
    __test.setFid('epic-1');
    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'epic-1', role: 'feature' }) });
    expect(__test.pending()).toBe(true);
    vi.advanceTimersByTime(1500);
    expect(getFeatureSpy).toHaveBeenCalledTimes(1);
  });

  it('три своих подряд в окне 1,5 с — ОДИН запрос (дебаунс)', () => {
    __test.setFid('epic-1');
    const msg = { type: 'task_changed' as const, action: 'updated' as const, task: task({ id: 'step-1', featureId: 'epic-1' }) };
    __test.handleTaskChanged(msg);
    __test.handleTaskChanged(msg);
    __test.handleTaskChanged(msg);
    // один таймер (дебаунс), не три
    expect(__test.pending()).toBe(true);
    vi.advanceTimersByTime(1500);
    expect(getFeatureSpy).toHaveBeenCalledTimes(1);
  });

  it('страница закрыта (fid = null) — событие не перезапрашивает', () => {
    __test.setFid(null);
    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'step-1', featureId: 'epic-1' }) });
    expect(__test.pending()).toBe(false);
    vi.advanceTimersByTime(1500);
    expect(getFeatureSpy).not.toHaveBeenCalled();
  });
});

// ── 3. Мутация: сломанный фильтр красит «чужое не перезапрашивает» ──────────────
//
// Мутация: isOwnEvent всегда true (фильтр принимает любое событие). Тогда «чужое
// событие (featureId ≠ fid) — не перезапрашивает» обязан покраснеть: pending станет
// true, а после окна дебаунса запрос уйдёт. Проверяем, что исходный (правильный)
// фильтр даёт pending=false, и что функция-фильтр — реальный шов (не заглушка).

describe('мутация: сломанный фильтр красит «чужое не перезапрашивает»', () => {
  let getFeatureSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.useFakeTimers();
    __test.reset();
    getFeatureSpy = vi.spyOn(api.tasks, 'getFeature').mockResolvedValue(page('epic-1'));
  });
  afterEach(() => {
    getFeatureSpy.mockRestore();
    __test.reset();
    vi.useRealTimers();
  });

  it('правильный фильтр: чужое (featureId ≠ fid) → pending=false, запрос не ушёл', () => {
    __test.setFid('epic-1');
    // шов-фильтр — реальная функция (не заглушка): на чужом событии даёт false
    expect(__test.isOwnEvent({ type: 'task_changed', action: 'updated', task: task({ id: 'step-1', featureId: 'epic-2' }) }, 'epic-1')).toBe(false);
    // через обработчик: pending не запланирован
    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'step-1', featureId: 'epic-2' }) });
    expect(__test.pending()).toBe(false);
    vi.advanceTimersByTime(1500);
    expect(getFeatureSpy).not.toHaveBeenCalled();
  });

  it('свой (featureId === fid) → isOwnEvent=true, pending=true', () => {
    __test.setFid('epic-1');
    expect(__test.isOwnEvent({ type: 'task_changed', action: 'updated', task: task({ id: 'step-1', featureId: 'epic-1' }) }, 'epic-1')).toBe(true);
    expect(__test.isOwnEvent({ type: 'task_changed', action: 'updated', task: task({ id: 'epic-1', role: 'feature' }) }, 'epic-1')).toBe(true);
  });
});

// ── 4. Смена fid: устаревший первичный запрос отменяется (AbortSignal), не плодим GET ──
//
// Быстрая смена fid: cleanup эффекта abort'ит в-полёте запрос. AbortError отменённого
// запроса не должен ронять состояние в error (fid сменился → _fid ≠ fid), а signal —
// реально уходить в api.tasks.getFeature (иначе запрос не отменяется).

describe('смена fid: устаревший запрос отменяется (AbortSignal)', () => {
  let getFeatureSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.useFakeTimers();
    __test.reset();
  });
  afterEach(() => {
    vi.restoreAllMocks();
    __test.reset();
    vi.useRealTimers();
  });

  it('signal уходит в getFeature; AbortError отменённого запроса не роняет состояние', async () => {
    const seen: Array<{ fid: string; signal?: AbortSignal }> = [];
    getFeatureSpy = vi.spyOn(api.tasks, 'getFeature').mockImplementation((fid: string, _lim?: string, signal?: AbortSignal) => {
      seen.push({ fid, signal });
      // Pending-Promise: отклоняется только по signal (как живой fetch при abort).
      return new Promise((_resolve, reject) => {
        if (signal) signal.addEventListener('abort', () => {
          const e = new Error('aborted') as Error; e.name = 'AbortError'; reject(e);
        });
      });
    });
    const c1 = new AbortController();
    __test.setFid('epic-1');
    __test.fetchPage('epic-1', true, c1.signal);
    // Смена fid: cleanup эффекта abort'ит первый запрос и ставит _fid = 'epic-2'.
    __test.setFid('epic-2');
    c1.abort();
    await vi.runAllTimersAsync();
    // signal реально передан в api (не плодим запросов без отмены).
    expect(seen[0].signal, 'signal не ушёл в getFeature').toBe(c1.signal);
    // AbortError отменённого запроса не роняет состояние в error (fid сменился → _fid ≠ fid).
    expect(__test.state(), 'состояние упало в error после отмены').toEqual({ status: 'loading' });
  });
});

// ── 5. brief (волна 6, F1): поле опционально — ответ с brief и без него ───────────
//
// Контрактный литерал BRIEF — в featurePage.contract.ts: тот файл НЕ исключён из
// сборки (tsconfig.app.json исключает только *.test.ts), поэтому переименование поля
// в FeatureBrief красит `tsc -b` (TS2561), а сделать brief обязательным — проверка
// опциональности в том же файле (TS2322). Здесь остаётся рантайм: стор пропускает
// ответ как есть — с brief и без него.

describe('brief: ответ страницы с brief и без него', () => {
  let getFeatureSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.useFakeTimers();
    __test.reset();
  });
  afterEach(() => {
    vi.restoreAllMocks();
    __test.reset();
    vi.useRealTimers();
  });

  it('ответ без brief разбирается без ошибки (поле опционально)', async () => {
    // Тайп-утверждение: старая страница (без brief) — валидный FeaturePage.
    const withoutBrief: FeaturePage = page('epic-1');
    getFeatureSpy = vi.spyOn(api.tasks, 'getFeature').mockResolvedValue(withoutBrief);
    __test.setFid('epic-1');
    __test.fetchPage('epic-1', true);
    await vi.runAllTimersAsync();
    expect(__test.state()).toEqual({ status: 'ok', page: withoutBrief });
  });

  it('ответ с полным brief разбирается, brief проходит как есть', async () => {
    // Тайп-утверждение: литерал с brief по контракту п. 6 — валидный FeaturePage.
    const withBrief: FeaturePage = { ...page('epic-1'), brief: BRIEF };
    getFeatureSpy = vi.spyOn(api.tasks, 'getFeature').mockResolvedValue(withBrief);
    __test.setFid('epic-1');
    __test.fetchPage('epic-1', true);
    await vi.runAllTimersAsync();
    const state = __test.state();
    expect(state.status).toBe('ok');
    // Стор не пересчитывает и не выбрасывает brief: он доходит до UI без изменений.
    if (state.status === 'ok') expect(state.page.brief).toEqual(BRIEF);
  });
});

// p.15/p.16 («Показать все») — замороженный блок: функция refetchFeaturePageAll убрана,
// её тесты (персист режима + тост при сбое) — с ней.

// ── 6. Названия вкладок и счётчик ────────────────────────────────────────────────
describe('названия вкладок и счётчик шагов', () => {
  it('stepsTabCount: stepProgress.total, а не число задач; без прогресса — число цепочек', () => {
    const chains = [{ stepRef: 'A' }, { stepRef: 'B' }] as FeatureRun['stepChains'];
    const withProgress = { stepProgress: { total: 98 }, stepChains: chains, steps: [{}, {}, {}] } as unknown as FeatureRun;
    expect(stepsTabCount(withProgress)).toBe(98);
    expect(stepsTabCount({ stepChains: chains } as unknown as FeatureRun)).toBe(2);
    expect(stepsTabCount({} as unknown as FeatureRun)).toBe(0);
  });
});

// ── 7. Мейн-вкладка «Ход · Материалы · Разбор» (FS-FTABS, макет v4) ──────────────────
//
// По макету v4 у «Разбора» сегментов нет: стор одной вкладки, без якоря. Живёт рядом
// со старым `_tab` (до FS-FP): setFeaturePageTab пишет оба значения, перевод — строго
// по таблице. Уведомления меряются сырой подпиской `__test.subscribe` (тот же emit,
// что читают useSyncExternalStore-хуки). Мутации:
//   «events → progress» в toMainTab → красный ровно тест «events → review»;
//   «убрать ранний выход при равном значении» в setFeatureMainTab → красный
//   «chats, затем stats» (2 вместо 1) и «no-op при равном» (3 вместо 2).

describe('мейн-вкладка (FS-FTABS)', () => {
  beforeEach(() => {
    __test.reset();
  });
  afterEach(() => {
    __test.reset();
  });

  // По строке на каждое значение таблицы: мутация одной строки красит ровно её тест.
  it('toMainTab: timeline → progress', () => {
    expect(toMainTab('timeline')).toBe('progress');
  });
  it('toMainTab: tasks → progress', () => {
    expect(toMainTab('tasks')).toBe('progress');
  });
  it('toMainTab: plans → materials', () => {
    expect(toMainTab('plans')).toBe('materials');
  });
  it('toMainTab: chats → review', () => {
    expect(toMainTab('chats')).toBe('review');
  });
  it('toMainTab: events → review', () => {
    expect(toMainTab('events')).toBe('review');
  });
  it('toMainTab: stats → review', () => {
    expect(toMainTab('stats')).toBe('review');
  });

  it('FEATURE_MAIN_TAB_LABEL: «Ход · Материалы · Разбор»', () => {
    expect(FEATURE_MAIN_TAB_LABEL.progress).toBe('Ход');
    expect(FEATURE_MAIN_TAB_LABEL.materials).toBe('Материалы');
    expect(FEATURE_MAIN_TAB_LABEL.review).toBe('Разбор');
  });

  it('chats, затем stats: main=review, второй вызов мейн-значение не меняет и подписчиков не будит', () => {
    setFeaturePageTab('chats'); // main → review (до подписки — не считаем)
    let n = 0;
    const off = __test.subscribe(() => {
      n += 1;
    });
    setFeaturePageTab('stats'); // main тот же (review): setFeatureMainTab ранне выходит
    off();
    expect(getFeatureMainTab()).toBe('review');
    // Лишнего уведомления нет: мутация «emit в setFeaturePageTab» даёт 1 вместо 0.
    expect(n).toBe(0);
  });

  it('повтор того же старого значения после ручной смены вкладки снова открывает нужную', () => {
    setFeaturePageTab('events');
    setFeatureMainTab('progress');
    setFeaturePageTab('events');
    expect(getFeatureMainTab()).toBe('review');
  });

  it('setFeatureMainTab: no-op при равном значении, уведомление только при смене', () => {
    let n = 0;
    const off = __test.subscribe(() => {
      n += 1;
    });
    setFeatureMainTab('review'); // progress → review: 1
    setFeatureMainTab('review'); // то же: 0 (ранний выход)
    setFeatureMainTab('materials'); // review → materials: 1
    off();
    expect(getFeatureMainTab()).toBe('materials');
    expect(n).toBe(2);
  });

  it('умолчание progress; смена фичи сбрасывает к нему, лишнего уведомления нет', () => {
    expect(getFeatureMainTab()).toBe('progress');
    // Счётчик «новых значений», как это делает useSyncExternalStore:
    // уведомление засчитывается, когда срез реально изменился.
    let last = getFeatureMainTab();
    let n = 0;
    const off = __test.subscribe(() => {
      const v = getFeatureMainTab();
      if (v !== last) {
        n += 1;
        last = v;
      }
    });
    __test.setPage('epic-1');
    setFeaturePageTab('stats'); // → review: одно новое значение
    __test.setPage('epic-2'); // смена фичи: сброс к дефолту вместе с _tab
    off();
    expect(__test.tab()).toBe('timeline');
    expect(__test.mainTab()).toBe('progress');
    expect(n).toBe(1); // ровно одно; сброс сам по себе emit не даёт
  });
});

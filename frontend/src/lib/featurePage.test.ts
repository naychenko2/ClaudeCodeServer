// Стор страницы фичи (волна 2 «Обзор фич», шаг Ф2.2).
//
// Окружение node (без jsdom): effects не идут, поэтому тестируем ЛОГИКУ стора
// через тестовые швы `__test`, а не React-хук (тот же приём, что в featureRuns.test.ts).
// Блоки:
//   1. Состояния: 200 → ok, 404 → notFound, 503 → disabled, прочий сбой → error.
//   2. Живое обновление: чужое событие не перезапрашивает; три своих подряд — один запрос.
//   3. Мутация: сломанный фильтр/дебаунс красит тест.

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { __test, refetchFeaturePageAll } from './featurePage';
import { api } from './api';
import { setAllFlags } from './featureFlags';
import type { Task } from '../types';
import type { FeaturePage } from '../types/featureIndex';

// p.16: тост по канону (showToast) — мок-шпион, чтобы проверить, что сбой «Показать все»
// не проглочен (реальный showToast диспатчит window-событие, в node-окружении его нет).
const { showToastMock } = vi.hoisted(() => ({ showToastMock: vi.fn() }));
vi.mock('./toast', () => ({ showToast: showToastMock }));

const FLAG = 'features-overview';

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
    setAllFlags({ [FLAG]: true });
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
    setAllFlags({ [FLAG]: true });
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

  it('флаг выключен — событие проигнорировано (нет перезапроса)', () => {
    setAllFlags({ [FLAG]: false });
    __test.setFid('epic-1');
    __test.handleTaskChanged({ type: 'task_changed', action: 'updated', task: task({ id: 'step-1', featureId: 'epic-1' }) });
    expect(__test.pending()).toBe(false);
    vi.advanceTimersByTime(1500);
    expect(getFeatureSpy).not.toHaveBeenCalled();
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
    setAllFlags({ [FLAG]: true });
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
    setAllFlags({ [FLAG]: true });
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

// ── p.15 / p.16: «Показать все» переживает живой перезапрос; сбой — тостом ──────
describe('«Показать все»: персист режима и тост при сбое (p.15, p.16)', () => {
  let getFeatureSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    vi.useFakeTimers();
    __test.reset();
    setAllFlags({ [FLAG]: true });
    showToastMock.mockClear();
  });
  afterEach(() => {
    vi.restoreAllMocks();
    __test.reset();
    vi.useRealTimers();
  });

  it('p.15: живой перезапрос (task_changed) держит eventsLimit=all после «Показать все»', async () => {
    getFeatureSpy = vi.spyOn(api.tasks, 'getFeature').mockResolvedValue(page('epic-1'));
    __test.setFid('epic-1');
    // Первичный запрос — без 'all' (дефолтный лимит).
    __test.fetchPage('epic-1', true);
    await vi.runAllTimersAsync();
    // «Показать все» — режим включается, запрос с 'all'.
    refetchFeaturePageAll('epic-1');
    await vi.runAllTimersAsync();
    expect(__test.showAll(), 'режим «все» не включился').toBe(true);
    // Живой перезапрос (task_changed → scheduleRefresh → fetchPage) — держит 'all'.
    __test.scheduleRefresh('epic-1');
    await vi.runAllTimersAsync();
    // Последний вызов getFeature — с eventsLimit='all' (не undefined/дефолт).
    const calls = getFeatureSpy.mock.calls as unknown as [string, string | undefined, AbortSignal?][];
    const last = calls[calls.length - 1];
    expect(last[1], 'живой перезапрос потерял eventsLimit=all (p.15)').toBe('all');
  });

  it('p.15: режим «все» сбрасывается при закрытии страницы (новая лента — дефолт)', async () => {
    getFeatureSpy = vi.spyOn(api.tasks, 'getFeature').mockResolvedValue(page('epic-1'));
    __test.setFid('epic-1');
    __test.fetchPage('epic-1', true);
    await vi.runAllTimersAsync();
    refetchFeaturePageAll('epic-1');
    await vi.runAllTimersAsync();
    expect(__test.showAll()).toBe(true);
    // Закрытие страницы (fid → null) сбрасывает режим: setPage — тот же шов, что эффект.
    __test.setPage(null);
    expect(__test.showAll(), 'режим «все» не сброшен при закрытии (p.15)').toBe(false);
  });

  it('p.16: сбой «Показать все» → тост (не проглочен .catch(() => {}))', async () => {
    const err = new Error('Сбой сети') as Error;
    getFeatureSpy = vi.spyOn(api.tasks, 'getFeature').mockRejectedValue(err);
    __test.setFid('epic-1');
    refetchFeaturePageAll('epic-1');
    await vi.runAllTimersAsync();
    // Тост по канону вызван (showToast) — клик не прошёл без признака.
    expect(showToastMock, 'сбой «Показать все» проглочен (p.16)').toHaveBeenCalled();
    const [title, body, kind] = showToastMock.mock.calls[0] as [string, string, string];
    expect(kind, 'тост не с тоном error').toBe('error');
    expect(body, 'сообщение тоста').toContain('Сбой сети');
    void title;
  });
});

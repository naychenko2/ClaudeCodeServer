// Шторка фичи (FeatureBannerSheet, Ф-pl-b; макет feature-banner-1, «А · Строка» + раскрытие).
//
// Данные — ТОЛЬКО из списка `features?sessionId=` (runs: FeatureRun[]): 7 этапов со
// статусом/временем, исполнители ТЕКУЩЕГО (active) этапа (roleLabel + чип модели +
// NavTargetButton), переключатель фич в порядке Б-ord (от бэка, фронт не пересортировывает).
//
// Два уровня:
//   1 — ЧИСТЫЙ helper executorLines(stage): подписи исполнителей (roleLabel + модель) — без DOM.
//   2 — РЕНДЕР (mount, fakeDom): десктопная раскрывающаяся панель (role=dialog, НЕ портал):
//      7 этапов, исполнители активного этапа, название фичи; закрытие по Esc/крестику;
//      возврат фокуса на строку; порядок переключателя фич (Б-ord).
//
// Окружение node (без jsdom): fakeDom даёт document/window; signalr/personas — mock.

import { act, createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FeatureBannerSheet, executorLines } from './FeatureBannerSheet';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } from '../__tests__/fakeDom';
import type { FeatureRun, FeatureStage, FeatureStageExecutor, FeatureTask, StageKey, StageState, StageTone } from '../../../types/featureRun';

// signalr: onMessage/onPreviewMessage — no-op (иначе getConnection() строит соединение '/hubs/session').
vi.mock('../../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
// personas: getPersonaById → undefined (имён персон в подписи роли нет), ensurePersonasLoaded no-op.
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, ensurePersonasLoaded: async () => {}, getPersonaById: () => undefined };
});

// === Фикстуры ===

function mkExecutor(over: Partial<FeatureStageExecutor> = {}): FeatureStageExecutor {
  return {
    personaId: null, taskId: 't1', taskRole: 'step', stepRef: 'Ш2', lane: 'бэк',
    status: 'running', chatId: 'chat-exec', chatArchived: false,
    modelLabel: 'Qwen 27B', modelFull: 'Qwen3.8-27B',
    ...over,
  };
}

function mkStage(key: StageKey, state: StageState, tone: StageTone | null = null, executors: FeatureStageExecutor[] = []): FeatureStage {
  return {
    key, state, tone,
    passes: 1,
    taskIds: executors.map(e => e.taskId),
    rounds: [],
    firstStartedAt: state === 'done' || state === 'active' ? '2026-10-20T10:00:00Z' : null,
    lastCompletedAt: state === 'done' ? '2026-10-20T11:30:00Z' : null,
    approxMinutes: state === 'done' ? 90 : state === 'active' ? 40 : 0,
    overlapsActive: false,
    executors,
  };
}

// 7 стадий: cut/plan пройдены, prod — активная (run, с исполнителем), остальное — впереди.
function stages(): FeatureStage[] {
  return [
    mkStage('cut', 'done'),
    mkStage('plan', 'done'),
    mkStage('prod', 'active', 'run', [mkExecutor()]),
    mkStage('review', 'pending'),
    mkStage('qa', 'pending'),
    mkStage('final', 'pending'),
    mkStage('merge', 'pending'),
  ];
}

function mkRun(featureId: string, title: string): FeatureRun {
  return {
    featureId,
    state: 'running',
    epic: { id: featureId, title } as unknown as FeatureTask,
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false,
    stages: stages(),
  };
}

// === 1: чистый helper executorLines ===

describe('executorLines: подписи исполнителей (roleLabel + модель)', () => {
  it('step с stepRef+lane → «Ш2 · бэк», модель — из modelLabel', () => {
    const stage = mkStage('prod', 'active', 'run', [mkExecutor()]);
    const lines = executorLines(stage);
    expect(lines).toHaveLength(1);
    expect(lines[0].role).toBe('Ш2 · бэк');
    expect(lines[0].model).toBe('Qwen 27B');
  });

  it('review → «ревью»; без модели → model null', () => {
    const stage = mkStage('review', 'active', 'run', [mkExecutor({ taskRole: 'review', stepRef: null, lane: null, modelLabel: null, modelFull: null })]);
    const lines = executorLines(stage);
    expect(lines[0].role).toBe('ревью');
    expect(lines[0].model).toBeNull();
  });

  it('пустая стадия → пустой список', () => {
    expect(executorLines(mkStage('qa', 'pending'))).toEqual([]);
  });
});

// === 2: рендер (mount, fakeDom) ===

describe('FeatureBannerSheet: рендер (fakeDom, десктоп)', () => {
  beforeEach(() => installFakeDom());
  afterEach(() => uninstallFakeDom());

  it('7 этапов (data-testid feature-sheet-stage-*), исполнители активного этапа, название фичи', async () => {
    const { root, container } = mount(createElement(FeatureBannerSheet, {
      runs: [mkRun('f1', 'Моя фича')], isMobile: false, onClose: () => {},
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    // Все 7 этапов на месте.
    for (const k of ['cut', 'plan', 'prod', 'review', 'qa', 'final', 'merge']) {
      expect(findNodeByAttr(container, 'data-testid', `feature-sheet-stage-${k}`), `нет этапа ${k}`).not.toBeNull();
    }
    // Исполнитель активного (prod) этапа: roleLabel + чип модели.
    const role = findNodeByAttr(container, 'data-testid', 'feature-sheet-executor-role');
    expect(role, 'нет роли исполнителя').not.toBeNull();
    const model = findNodeByAttr(container, 'data-testid', 'feature-sheet-executor-model');
    expect(model, 'нет чипа модели').not.toBeNull();
    // Название фичи (epic.title) — в заголовке панели.
    expect(textNodes(container).join(' ')).toContain('Моя фича');
    root.unmount();
  });

  it('Esc закрывает панель (onClose) — desktop', async () => {
    // FDoc не хранит document-level слушателей — перехватываем keydown-обработчик
    // через патч addEventListener (sheet вешает его в useEffect на desktop).
    const doc = (globalThis as { document: { addEventListener: (t: string, f: unknown) => void } }).document;
    const orig = doc.addEventListener;
    let h: ((e: { key: string; preventDefault: () => void }) => void) | null = null;
    doc.addEventListener = (t: string, f: unknown) => { if (t === 'keydown') h = f as typeof h; return orig.call(doc, t, f); };
    let closed = 0;
    const { root } = mount(createElement(FeatureBannerSheet, {
      runs: [mkRun('f1', 'Моя фича')], isMobile: false, onClose: () => { closed++; },
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    doc.addEventListener = orig;
    expect(h, 'keydown-обработчик не зарегистрирован').not.toBeNull();
    act(() => { h!({ key: 'Escape', preventDefault: () => {} }); });
    expect(closed, 'Esc не закрыл панель').toBe(1);
    root.unmount();
  });

  it('крестик (feature-sheet-close) закрывает панель (onClose)', async () => {
    let closed = 0;
    const { root, container } = mount(createElement(FeatureBannerSheet, {
      runs: [mkRun('f1', 'Моя фича')], isMobile: false, onClose: () => { closed++; },
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    const closeBtn = findNodeByAttr(container, 'data-testid', 'feature-sheet-close');
    expect(closeBtn, 'нет крестика').not.toBeNull();
    act(() => { (reactProps(closeBtn!)!.onClick as () => void)(); });
    expect(closed, 'крестик не закрыл панель').toBe(1);
    root.unmount();
  });

  it('возврат фокуса: закрытие вызывает focusTarget.focus()', async () => {
    let focused = 0;
    const focusTarget = { current: { focus: () => { focused++; } } } as never;
    let closed = 0;
    const { root, container } = mount(createElement(FeatureBannerSheet, {
      runs: [mkRun('f1', 'Моя фича')], isMobile: false,
      onClose: () => { closed++; }, focusTarget,
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    const closeBtn = findNodeByAttr(container, 'data-testid', 'feature-sheet-close');
    act(() => { (reactProps(closeBtn!)!.onClick as () => void)(); });
    expect(closed).toBe(1);
    expect(focused, 'фокус не вернулся на строку').toBe(1);
    root.unmount();
  });

  it('переключатель фич: порядок Б-ord (от бэка) — фичи в порядке runs, не пересортированы', async () => {
    const runs = [mkRun('f1', 'Первая фича'), mkRun('f2', 'Вторая фича')];
    const { root, container } = mount(createElement(FeatureBannerSheet, {
      runs, isMobile: false, onClose: () => {},
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    // Переключатель есть (мультифича) и показывает обе фичи В ПОРЯДКЕ runs.
    const switcher = findNodeByAttr(container, 'data-testid', 'feature-sheet-switcher');
    expect(switcher, 'нет переключателя фич').not.toBeNull();
    const first = findNodeByAttr(container, 'data-testid', 'feature-sheet-feature-0');
    const second = findNodeByAttr(container, 'data-testid', 'feature-sheet-feature-1');
    expect(first, 'нет первой фичи').not.toBeNull();
    expect(second, 'нет второй фичи').not.toBeNull();
    // Порядок: «Первая фича» раньше «Вторая фича» в разметке.
    const all = textNodes(container).join(' ');
    const iFirst = all.indexOf('Первая фича');
    const iSecond = all.indexOf('Вторая фича');
    expect(iFirst).toBeGreaterThanOrEqual(0);
    expect(iSecond).toBeGreaterThanOrEqual(0);
    expect(iFirst, 'фичи пересортированы (потерян порядок Б-ord)').toBeLessThan(iSecond);
    root.unmount();
  });
});

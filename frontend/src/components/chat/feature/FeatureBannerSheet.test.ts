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
import { FeatureBannerSheet, executorLines, focusablesIn } from './FeatureBannerSheet';
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

// === focusablesIn: tabindex=-2 — явно НЕ фокусируемый (даже BUTTON) ===
describe('focusablesIn: tabindex', () => {
  it('BUTTON (tabindex=0) — фокусируем; BUTTON tabindex=-2 — нет (мутация «-2 игнорируется» красит)', () => {
    installFakeDom();
    const doc = (globalThis as { document: { createElement: (t: string) => any } }).document;
    const root = doc.createElement('div');
    const normal = doc.createElement('button');
    normal.setAttribute('tabindex', '0');
    const hidden = doc.createElement('button');
    hidden.setAttribute('tabindex', '-2');
    root.appendChild(normal);
    root.appendChild(hidden);
    const focusables = focusablesIn(root as never);
    // Нормальный button — в списке; tabindex=-2 — нет (явно скрыт от табуляции).
    expect(focusables).toContain(normal);
    expect(focusables).not.toContain(hidden);
    uninstallFakeDom();
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

  // Минор 7: проп order (порядок строки/индекса) пересортировывает runs — переключатель
  // идёт в том же порядке, что строка (а не в бэковом порядке runs).
  it('order: переключатель пересортирован под порядок строки (order ≠ порядок runs)', async () => {
    const runs = [mkRun('f1', 'Первая фича'), mkRun('f2', 'Вторая фича')];
    const { root, container } = mount(createElement(FeatureBannerSheet, {
      runs, isMobile: false, onClose: () => {}, order: ['f2', 'f1'],
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    const all = textNodes(container).join(' ');
    const iFirst = all.indexOf('Первая фича');
    const iSecond = all.indexOf('Вторая фича');
    expect(iFirst, '«Первая фича» в переключателе').toBeGreaterThanOrEqual(0);
    expect(iSecond, '«Вторая фича» в переключателе').toBeGreaterThanOrEqual(0);
    // order=['f2','f1'] → «Вторая фича» ПЕРВОЙ (бэковый порядок runs не сохранён).
    expect(iSecond, 'order не применён: «Вторая фича» должна быть первой').toBeLessThan(iFirst);
    root.unmount();
  });

  // Минор 6: runs пуст — шторка не молчит: loading → «Загрузка…», error → причина.
  it('loading: runs пуст + loading → «Загрузка…» (не тишина)', async () => {
    const { root, container } = mount(createElement(FeatureBannerSheet, {
      runs: [], isMobile: false, onClose: () => {}, loading: true,
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    const state = findNodeByAttr(container, 'data-testid', 'feature-sheet-state');
    expect(state, 'нет состояния шторки').not.toBeNull();
    expect(textNodes(state!).join('')).toContain('Загрузка…');
    root.unmount();
  });

  it('error: runs пуст + error → причина в шторке (не тишина)', async () => {
    const { root, container } = mount(createElement(FeatureBannerSheet, {
      runs: [], isMobile: false, onClose: () => {}, error: 'Сервер не ответил',
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    const state = findNodeByAttr(container, 'data-testid', 'feature-sheet-state');
    expect(state, 'нет состояния шторки').not.toBeNull();
    expect(textNodes(state!).join('')).toContain('Сервер не ответил');
    root.unmount();
  });

  it('runs пуст, loading/error нет → шторка не рисуется (null)', async () => {
    const { root, container } = mount(createElement(FeatureBannerSheet, {
      runs: [], isMobile: false, onClose: () => {},
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-sheet-panel'), 'панель не должна быть').toBeNull();
    root.unmount();
  });

  // M5: focus trap в самой шторке (не в ките): при открытии фокус уходит ВНУТРЬ (на
  // первый фокусируемый — крестик), Tab/Shift+Tab — по кругу. fakeDom: focus() — no-op,
  // document.addEventListener — no-op: патчим оба (focus → activeElement; keydown-
  // обработчики — в массив, вызываем напрямую).
  it('M5: focus trap — фокус внутрь при открытии, Tab/Shift+Tab по кругу (desktop)', async () => {
    const doc = (globalThis as { document: any }).document;
    // Захват keydown-обработчиков (Esc + trap) — патч addEventListener на время mount.
    const keydownHandlers: Function[] = [];
    const origAdd = doc.addEventListener;
    doc.addEventListener = (t: string, f: unknown) => { if (t === 'keydown') keydownHandlers.push(f); return origAdd.call(doc, t, f); };
    // focus() → activeElement (иначе no-op: не видно, куда ушёл фокус).
    const proto = Object.getPrototypeOf(doc.createElement('div'));
    const origFocus = proto.focus;
    proto.focus = function (this: any) { doc.activeElement = this; };

    const { root, container } = mount(createElement(FeatureBannerSheet, {
      runs: [mkRun('f1', 'Моя фича')], isMobile: false, onClose: () => {},
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    doc.addEventListener = origAdd;

    const closeBtn = findNodeByAttr(container, 'data-testid', 'feature-sheet-close');
    const taskBtn = findNodeByAttr(container, 'data-testid', 'nav-target-task');
    expect(closeBtn, 'нет крестика').not.toBeNull();
    expect(taskBtn, 'нет кнопки задачи (последний фокусируемый)').not.toBeNull();

    // При открытии: фокус на ПЕРВОМ фокусируемом (крестик).
    expect(doc.activeElement, 'фокус не ушёл внутрь панели при открытии').toBe(closeBtn);

    // Tab с ПОСЛЕДНЕГО (кнопка задачи) → на ПЕРВЫЙ (крестик).
    doc.activeElement = taskBtn;
    act(() => { for (const h of keydownHandlers) h({ key: 'Tab', shiftKey: false, preventDefault: () => {} }); });
    expect(doc.activeElement, 'Tab с последнего не привёл на первый').toBe(closeBtn);

    // Shift+Tab с ПЕРВОГО (крестик) → на ПОСЛЕДНИЙ (кнопка задачи).
    doc.activeElement = closeBtn;
    act(() => { for (const h of keydownHandlers) h({ key: 'Tab', shiftKey: true, preventDefault: () => {} }); });
    expect(doc.activeElement, 'Shift+Tab с первого не привёл на последний').toBe(taskBtn);

    proto.focus = origFocus;
    root.unmount();
  });

  // Минор 4: initialSel — шторка открывает фичу, выбранную в строке («· ещё N»),
  // а не первую по order. Мутация initialSel=0 (вместо 1) → red.
  it('Минор 4: initialSel=1 → шторка открывает ВТОРУЮ фичу (не первую)', async () => {
    const runs = [mkRun('f1', 'Первая фича'), mkRun('f2', 'Вторая фича')];
    const { root, container } = mount(createElement(FeatureBannerSheet, {
      runs, isMobile: false, onClose: () => {}, initialSel: 1,
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    // Заголовок панели — «Вторая фича» (initialSel=1).
    const panel = findNodeByAttr(container, 'data-testid', 'feature-sheet-panel');
    expect(panel, 'нет панели').not.toBeNull();
    expect(textNodes(panel!).join(' ')).toContain('Вторая фича');
    root.unmount();
  });

  // Зонд: проводка initialSel={idx} баннер→шторка, где порядок entries (order)
  // ОТЛИЧАЕТСЯ от порядка runs (API). Мутация: убрать order → runs в API-порядке →
  // initialSel=0 покажет f1 (не f2) → red.
  it('зонд: order ≠ порядок runs → initialSel=0 открывает фичу из order (не первую из runs)', async () => {
    // runs из API: [f1, f2]; order из индекса: ['f2', 'f1'] (f2 первая в строке).
    const runs = [mkRun('f1', 'F1 из API'), mkRun('f2', 'F2 из API')];
    const { root, container } = mount(createElement(FeatureBannerSheet, {
      runs, isMobile: false, onClose: () => {},
      order: ['f2', 'f1'], // индекс: f2 на первом месте
      initialSel: 0,       // баннер показывает f2 (idx=0 в entries)
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });

    // Панель должна показать f2 (первая в order), а не f1 (первая в runs).
    const panel = findNodeByAttr(container, 'data-testid', 'feature-sheet-panel');
    expect(panel, 'нет панели').not.toBeNull();
    const text = textNodes(panel!).join(' ');
    expect(text, 'ожидается «F2 из API» (первая в order), не «F1 из API»').toContain('F2 из API');
    root.unmount();
  });

  // Минор 5: focus trap на МОБИЛЕ (Modal/bottom-sheet): ловушка на оверлее (cc-overlay),
  // фокус внутрь при открытии, Tab/Shift+Tab по кругу. fakeDom: keydown → handlers.
  // На мобиле Modal рисует портал (createPortal → document.body); в fakeDom портал
  // рендерится в document.body, а не в container — поэтому ищем по document.
  // Мутация «снять useFocusTrap»: фокус не уйдёт внутрь → activeElement ≠ closeBtn → red.
  it('Минор 5: focus trap на мобиле — фокус внутрь, Tab/Shift+Tab по кругу', async () => {
    const doc = (globalThis as { document: any }).document;
    // Захват keydown-обработчиков (Esc Modal + trap) — патч addEventListener на время mount.
    const keydownHandlers: Function[] = [];
    const origAdd = doc.addEventListener;
    doc.addEventListener = (t: string, f: unknown) => { if (t === 'keydown') keydownHandlers.push(f); return origAdd.call(doc, t, f); };
    // focus() → activeElement (иначе no-op: не видно, куда ушёл фокус).
    const proto = Object.getPrototypeOf(doc.createElement('div'));
    const origFocus = proto.focus;
    proto.focus = function (this: any) { doc.activeElement = this; };

    const { root } = mount(createElement(FeatureBannerSheet, {
      runs: [mkRun('f1', 'Моя фича')], isMobile: true, onClose: () => {},
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    doc.addEventListener = origAdd;

    // Крестик Modal (первый фокусируемый в оверлее) — в document.body (портал).
    const closeBtn = findNodeByAttr(doc.body, 'data-testid', 'feature-sheet-close')
      ?? findNodeByAttr(doc.body, 'aria-label', 'Закрыть');
    expect(closeBtn, 'крестик Modal (портал) не найден').not.toBeNull();

    // При открытии: фокус на ПЕРВОМ фокусируемом (крестик Modal).
    expect(doc.activeElement, 'фокус не ушёл внутрь Modal при открытии (mobile)').toBe(closeBtn);

    // Tab с ПОСЛЕДНЕГО → на ПЕРВЫЙ (крестик). Ставим activeElement на «последний»
    // (любой фокусируемый после крестика) и проверяем цикл.
    // В fakeDom у Modal есть только крестик + контент; если фокусируемых > 1 —
    // Tab с последнего → первый. Если только один (крестик) — Tab → на него же.
    const allFocusable = focusablesIn(doc.body);
    if (allFocusable.length > 1) {
      const last = allFocusable[allFocusable.length - 1] as HTMLElement;
      doc.activeElement = last;
      act(() => { for (const h of keydownHandlers) h({ key: 'Tab', shiftKey: false, preventDefault: () => {} }); });
      expect(doc.activeElement, 'Tab с последнего не привёл на первый (mobile)').toBe(closeBtn);

      doc.activeElement = closeBtn;
      act(() => { for (const h of keydownHandlers) h({ key: 'Tab', shiftKey: true, preventDefault: () => {} }); });
      expect(doc.activeElement, 'Shift+Tab с первого не привёл на последний (mobile)').toBe(last);
    } else {
      // Один фокусируемый (крестик): Tab → на него же (цикл из одного).
      doc.activeElement = closeBtn;
      act(() => { for (const h of keydownHandlers) h({ key: 'Tab', shiftKey: false, preventDefault: () => {} }); });
      expect(doc.activeElement, 'Tab с единственного не остался на нём (mobile)').toBe(closeBtn);
    }

    proto.focus = origFocus;
    root.unmount();
  });

  // Минор 6 (зонды): фича без исполнителей — «Исполнителей нет» в шторке.
  it('Минор 6: фича без исполнителей → «Исполнителей нет» (не тишина)', async () => {
    // prod — active, но executors: [] (пусто).
    const run: FeatureRun = {
      featureId: 'f1', state: 'running',
      epic: { id: 'f1', title: 'Без исполнителей' } as unknown as import('../../../types/featureRun').FeatureTask,
      steps: [], reviews: [], qa: [], final: null, merge: null,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
      stages: [
        mkStage('cut', 'done'),
        mkStage('plan', 'done'),
        mkStage('prod', 'active', 'run', []), // нет исполнителей
        mkStage('review', 'pending'),
        mkStage('qa', 'pending'),
        mkStage('final', 'pending'),
        mkStage('merge', 'pending'),
      ],
    };
    const { root, container } = mount(createElement(FeatureBannerSheet, {
      runs: [run], isMobile: false, onClose: () => {},
    }));
    await act(async () => { await new Promise(r => setTimeout(r, 0)); });
    const all = textNodes(container).join(' ');
    expect(all, '«Исполнителей нет» не показано').toContain('Исполнителей нет');
    root.unmount();
  });
});

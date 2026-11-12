// FP-C2: рендер FeatureChatPopover — поповер 1280 / шторка 360 строки фичи чата
// (записка chat-feature-fit-v1-proposal.md, §3 «А», §5 «Поповер (1280)», «360: шторка»,
// «Состояния»). Окружение — настоящий компонент на fakeDom, как в FeatureChatCard.test
// и FeatureLanePopover.test: поповер и шторка живут в портале (document.body), поэтому
// поиск узлов — от body.
//
// Компонент чисто презентационный: модель и полоса приходят фикстурами пропсами, все
// действия — spy-колбэки. Сторов и маршрутов в компоненте нет (критерий c4) — тест их и
// не мокает: их отсутствие виден по тому, что моков здесь нет вовсе.
//
// Что защищается: 1280 — Menu по якорю (полная фраза тоном с <b> имени, трекер 4 px,
// счёт, ряд кнопок: действие · «Ключевой чат» · «К фиче» · «ещё N»); Esc → onClose;
// устаревшая модель — перерисовка с action=null не бросает и не рисует кнопку; пустой
// bar — трекера нет; isKeyChat — Badge «это ключевой чат» вместо кнопки; 360 — шторка,
// все кнопки на всю ширину с minHeight 40.
//
// Мутации (в отчёте): (1) снять minHeight 40 у кнопок шторки → краснеет ровно тест
// «360»; (2) рисовать кнопку действия при onAction=null → краснеет тест action=null.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureChatPopover } from './FeatureChatPopover';
import { C } from '../../../lib/design';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../__tests__/fakeDom';
import type { FeatureChatCardModel } from './featureChatCardModel';
import type { TruthBar } from './featureTruthModel';

type FNodeLike = {
  nodeType: number; nodeName: string; childNodes: FNodeLike[];
  attributes?: Record<string, string>; style?: Record<string, string>;
  getAttribute(n: string): string | null;
};

interface FakeDoc {
  body: FNodeLike;
  activeElement: FNodeLike | null;
  listeners: Record<string, Set<(e: unknown) => void>>;
  createElement(tag: string): FNodeLike & { getBoundingClientRect(): DOMRect };
}

interface FakeWindow {
  innerWidth: number; innerHeight: number;
  matchMedia: (q: string) => { matches: boolean; addEventListener: () => void; removeEventListener: () => void };
}

function doc(): FakeDoc { return (globalThis as unknown as { document: FakeDoc }).document; }
function win(): FakeWindow { return (globalThis as unknown as { window: FakeWindow }).window; }

// Узел по data-testid в document.body: Menu и Modal рисуются порталом в body —
// в контейнере mount их нет (тот же приём, что FeatureLanePopover.test).
function inBody(testId: string): FNodeLike | null {
  return findNodeByAttr(doc().body as never, 'data-testid', testId) as FNodeLike | null;
}

function walk(n: FNodeLike, fn: (x: FNodeLike) => void): void {
  fn(n);
  for (const c of n.childNodes) walk(c, fn);
}
function buttonNodes(root: FNodeLike): FNodeLike[] {
  const out: FNodeLike[] = [];
  walk(root, (x) => { if (x.nodeType === 1 && x.nodeName === 'BUTTON') out.push(x); });
  return out;
}
const textOf = (n: FNodeLike): string => textNodes(n as never).join('');
const styleOf = (n: FNodeLike): Record<string, string> => n.style ?? {};

// Esc на document: обработчики закрытия висят на document (ui/Menu, ui/Modal).
function pressEscape(): void {
  const handlers = [...(doc().listeners?.keydown ?? new Set<(e: unknown) => void>())];
  act(() => { for (const h of handlers) h({ key: 'Escape', preventDefault: () => {}, defaultPrevented: false }); });
}

// ── Фикстуры: модель и полоса — ровно те же формы, что отдают featureChatCardModel
// и featureTruthBar; тест не строит их сам заново. ──
function model(over: Partial<FeatureChatCardModel> = {}): FeatureChatCardModel {
  return {
    title: 'Правда о прогрессе', glyph: 'warning', tag: null,
    count: '6 из 44', eta: '≈ 2 д', etaPaused: false,
    phrase: { label: 'Нужно от тебя', text: 'нужно решение по отступам сейчас', tone: 'warn', name: null },
    action: { label: 'Ответить здесь', sessionId: 's-me', toMerge: false },
    isKeyChat: false, keySessionId: 's-key', showTracker: true,
    ...over,
  };
}

const bar = (): TruthBar => ({
  percent: 14, segments: [{ key: 'accepted', count: 6, color: C.success, label: 'принято' }],
  inMaster: 0, masterDraw: 0,
});
const emptyBar = (): TruthBar => ({ percent: 0, segments: [], inMaster: 0, masterDraw: 0 });

type Props = Parameters<typeof FeatureChatPopover>[0];

let closeCount = 0;
const calls: Record<string, number> = {};
const spy = (name: string) => () => { calls[name] = (calls[name] ?? 0) + 1; };
const roots: import('react-dom/client').Root[] = [];

function renderPopover(over: Partial<Props> = {}): { root: import('react-dom/client').Root; anchor: FNodeLike } {
  // Якорь-триггер: Menu позиционируется по его rect (FNode отдаёт нулевой — не поведение).
  const anchor = doc().createElement('button');
  const { root } = mount(createElement(FeatureChatPopover, {
    model: model(), bar: bar(), isMobile: false, anchor: anchor as unknown as HTMLElement,
    onClose: () => { closeCount++; },
    onAction: spy('action'), onKeyChat: spy('key'), onToFeature: spy('feature'),
    toFeatureLabel: 'К фиче ›', more: null,
    ...over,
  }));
  roots.push(root);
  return { root, anchor };
}

beforeEach(() => {
  closeCount = 0;
  for (const k of Object.keys(calls)) delete calls[k];
  installFakeDom();
  // Меню в якорном режиме читает окно (позиция/направление); порога matchMedia нет →
  // не-мобильная раскладка (шторка включается только там, где просим).
  win().innerWidth = 1280; win().innerHeight = 800;
  win().matchMedia = () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} });
  // Возврат фокуса у Menu проверяет `el instanceof HTMLElement` — в node этого глобала
  // нет, и без заглушки handler падал бы ReferenceError раньше, чем тест увидел бы onClose.
  vi.stubGlobal('HTMLElement', class {});
});
afterEach(() => {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
  uninstallFakeDom();
});

describe('FeatureChatPopover: поповер 1280 (Menu по якорю)', () => {
  it('полное содержимое: название, фраза тоном с <b> имени, трекер 4 px, счёт, ряд кнопок в порядке', () => {
    renderPopover({
      model: model({
        phrase: { label: 'Нужно от тебя', text: 'нужно решение по отступам — Кира ждёт', tone: 'warn', name: 'Кира' },
      }),
      more: { count: 2, onNext: spy('more') },
    });
    const pop = inBody('feature-popover');
    expect(pop, 'поповера нет').not.toBeNull();
    expect(textOf(inBody('feature-popover-title')!)).toBe('Правда о прогрессе');
    // Фраза — полная (без многоточия строки), тоном warn; подпись — отдельный элемент.
    const phrase = inBody('feature-popover-phrase')!;
    expect(textOf(phrase)).toBe('Нужно от тебя: нужно решение по отступам — Кира ждёт');
    expect(styleOf(phrase).color, 'тон фразы не warn').toBe(C.warningText);
    let nameBold: FNodeLike | null = null;
    walk(phrase, (x) => { if (x.nodeType === 1 && x.nodeName === 'B' && textOf(x) === 'Кира') nameBold = x; });
    expect(nameBold, 'имя из phrase.name не выделено <b>').not.toBeNull();
    // Трекер 4 px — как есть (FeatureTrackerBar), на всю ширину.
    const barEl = inBody('feature-popover-bar');
    expect(barEl, 'трекера нет').not.toBeNull();
    expect(styleOf(barEl!).height, 'трекер не 4 px').toBe('4px');
    expect(textOf(inBody('feature-popover-count')!)).toContain('6 из 44');
    // Ряд кнопок: действие · «Ключевой чат» · «К фиче» · «ещё N» последней.
    const row = inBody('feature-popover-actions')!;
    const ids = row.childNodes
      .filter((c) => c.nodeType === 1)
      .map((c) => c.getAttribute('data-testid'));
    expect(ids).toEqual([
      'feature-popover-action', 'feature-popover-to-key', 'feature-popover-to-feature', 'feature-popover-cycle',
    ]);
    expect(textOf(inBody('feature-popover-action')!)).toBe('Ответить здесь');
    expect(textOf(inBody('feature-popover-cycle')!)).toBe('ещё 2');
    // Кнопки ряда живут своими колбэками пропсов (маршрутов здесь нет — их решает вызывающий).
    const actionProps = reactProps(inBody('feature-popover-action')! as never) as Record<string, (() => void) | undefined> | null;
    act(() => { actionProps?.onClick?.(); });
    expect(calls.action, 'клик действия не зовёт onAction').toBe(1);
  });

  it('Escape вызывает onClose', () => {
    renderPopover();
    expect(inBody('feature-popover'), 'поповера нет').not.toBeNull();
    expect(closeCount).toBe(0);
    pressEscape();
    expect(closeCount, 'Escape не закрыл поповер').toBe(1);
  });

  it('isKeyChat: кнопки «Ключевой чат» нет, есть бейдж «это ключевой чат» (span, не кнопка)', () => {
    renderPopover({ model: model({ isKeyChat: true }), onKeyChat: null });
    expect(inBody('feature-popover-to-key'), 'кнопка ключевого чата в ключевом чате').toBeNull();
    const badge = inBody('feature-popover-key-badge');
    expect(badge, 'бейджа ключевого чата нет').not.toBeNull();
    expect(textOf(badge!)).toBe('это ключевой чат');
    expect(badge!.nodeName).not.toBe('BUTTON');
  });
});

describe('FeatureChatPopover: устаревшая модель (конкурентность)', () => {
  it('перерисовка с action=null не бросает и не рисует кнопку', () => {
    const { root, anchor } = renderPopover();
    expect(inBody('feature-popover-action'), 'кнопки действия нет в исходном рендере').not.toBeNull();
    // Run пришёл позже индекса: модель без действия — ТОТ ЖЕ корень и тот же якорь,
    // без перемонтирования.
    act(() => { root.render(createElement(FeatureChatPopover, {
      model: model({ action: null }), bar: bar(), isMobile: false, anchor: anchor as unknown as HTMLElement,
      onClose: () => { closeCount++; }, onAction: null, onKeyChat: spy('key'),
      onToFeature: spy('feature'), toFeatureLabel: 'К фиче ›', more: null,
    })); });
    expect(inBody('feature-popover'), 'поповер рассыпался').not.toBeNull();
    expect(inBody('feature-popover-action'), 'кнопка действия осталась при action=null').toBeNull();
  });

  it('onAction=null: кнопки действия нет, даже когда модель её ещё не сняла', () => {
    renderPopover({ onAction: null });
    expect(inBody('feature-popover-action'), 'кнопка действия при onAction=null').toBeNull();
    expect(inBody('feature-popover'), 'остальное содержимое пропало').not.toBeNull();
  });

  it('пустой bar.segments: трекера нет (showTracker=true не рисует пустую полосу)', () => {
    renderPopover({ bar: emptyBar() });
    expect(inBody('feature-popover'), 'поповера нет').not.toBeNull();
    expect(inBody('feature-popover-bar'), 'пустая полоса рисуется').toBeNull();
  });
});

describe('FeatureChatPopover: шторка 360', () => {
  it('шторка (не карточка): все кнопки ряда на всю ширину с minHeight 40', () => {
    // Мобильный viewport: Modal сам становится шторкой (useIsMobileModal по matchMedia).
    win().matchMedia = () => ({ matches: true, addEventListener: () => {}, removeEventListener: () => {} });
    renderPopover({
      isMobile: true,
      model: model({ keySessionId: 's-key' }),
      more: { count: 1, onNext: spy('more') },
    });
    const pop = inBody('feature-popover');
    expect(pop, 'содержимого шторки нет').not.toBeNull();
    // Форма — именно шторка: карточка Modal на мобиле помечена классом cc-sheet-card.
    let sheet = false;
    for (let n: FNodeLike | null = pop; n; n = (n as { parentNode?: FNodeLike | null }).parentNode ?? null) {
      // React пишет className в атрибут «class» (тот же приём, что FeatureStatsTab.test).
      if (n.nodeType === 1 && n.getAttribute('class') === 'cc-sheet-card') { sheet = true; break; }
    }
    expect(sheet, 'вместо шторки — карточка').toBe(true);
    const row = inBody('feature-popover-actions')!;
    const btns = buttonNodes(row);
    expect(btns.length, 'в ряду должны быть действие, «Ключевой чат», «К фиче» и «ещё 1»').toBe(4);
    for (const b of btns) {
      const st = styleOf(b);
      expect(st.minHeight, `кнопка «${textOf(b)}» ниже 40`).toBe('40px');
      expect(st.width, `кнопка «${textOf(b)}» не на всю ширину`).toBe('100%');
    }
  });
});

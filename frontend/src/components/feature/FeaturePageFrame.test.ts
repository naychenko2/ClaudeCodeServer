// Каркас страницы фичи (волна 3, Ф1):
//   • тег статуса по run.state — 9 состояний → 8 подписей макета (idle = «не начата»);
//   • вкладки: «Хронология» по умолчанию, выбор сохраняется в сторе (скрытие/возврат),
//     на 360 полоса вкладок — горизонтальная прокрутка;
//   • тревога по attention.kind: wait/stopped/merge → тон info/danger/info,
//     главное действие («Ответить {имя}» / «Чат {имя}» / «Мерж») и пара «чат · задача»;
//   • 360: тег — первым в мете, главное действие — в блоке тревоги.
//
// Окружение node (без jsdom): fakeDom (mount) + управляемые сторы (featurePage,
// featuresIndex, featureBackNav) и моки api/personas/signalr/toast/openChat/tasks.

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeaturePage, type FeaturePageBackTarget } from './FeaturePage';
import { featureRunStatusTag, featurePrimaryAction } from './FeaturePageHeader';
import { __test as pageTest, setFeaturePageTab } from '../../lib/featurePage';
import { __test as indexTest } from '../../lib/featuresIndex';
import { __test as backNavTest } from '../../lib/featureBackNav';
import { setAllFlags } from '../../lib/featureFlags';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import type { FeatureIndexFeature, FeaturesIndex, FeaturePage as FeaturePageData } from '../../types/featureIndex';
import type { FeatureRun, FeatureState, FeatureAttention } from '../../types/featureRun';
import type { Session, Task } from '../../types';

// ── Управляемые данные ──────────────────────────────────────────────────────────
const mockIndex = vi.hoisted(() => ({
  features: [] as FeatureIndexFeature[],
  chats: {} as Record<string, import('../../types/featureIndex').FeatureChatLink[]>,
}));
const mockChats = vi.hoisted(() => new Map<string, Session>());
const mockGetFeature = vi.hoisted(() => ({
  pending: false,
  result: null as FeaturePageData | null,
  error: null as (Error & { status?: number }) | null,
  calls: [] as { fid: string; eventsLimit?: string }[],
}));
// Персоны (имена для «Ответить {имя}»/«Чат {имя}»).
const { personaMap } = vi.hoisted(() => ({ personaMap: new Map<string, string>() }));
// Журналы действий: openChatById / openTaskInSection / showToast.
const mockOpenChat = vi.hoisted(() => ({ calls: [] as string[] }));
const mockOpenTask = vi.hoisted(() => ({ calls: [] as { id: string; projectId?: string }[] }));
const mockToast = vi.hoisted(() => ({ calls: [] as [string, string][] }));
// Мерж (Ф6): calls — журнал (двойной клик → 1), pending — запрос в полёте,
// error — 409/сбой (status + message), result — задача мержа.
const mockMerge = vi.hoisted(() => ({
  calls: [] as string[],
  pending: false,
  error: null as (Error & { status?: number }) | null,
  result: null as import('../../types').Task | null,
}));

vi.mock('../../lib/api', () => ({
  api: {
    chats: { get: async (id: string) => mockChats.get(id) ?? null },
    tasks: {
      featuresIndex: async () => mockIndex,
      getFeature: (fid: string, eventsLimit?: string) => {
        mockGetFeature.calls.push({ fid, eventsLimit });
        if (mockGetFeature.pending) return new Promise<FeaturePageData>(() => {});
        if (mockGetFeature.error) return Promise.reject(mockGetFeature.error);
        return Promise.resolve(mockGetFeature.result!);
      },
      mergeFeature: (fid: string) => {
        mockMerge.calls.push(fid);
        if (mockMerge.pending) return new Promise<import('../../types').Task>(() => {});
        if (mockMerge.error) return Promise.reject(mockMerge.error);
        return Promise.resolve(mockMerge.result!);
      },
    },
  },
}));
vi.mock('../../lib/personas', () => ({
  getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined),
  usePersonasVersion: () => 0,
  personaInitials: (name: string) => (name ? name.trim().charAt(0).toUpperCase() : ''),
}));
vi.mock('../../lib/signalr', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/signalr')>();
  return { ...orig, onMessage: () => () => {}, onPreviewMessage: () => () => {} };
});
vi.mock('../../lib/toast', () => ({
  showToast: (t: string, b: string) => { mockToast.calls.push([t, b]); },
}));
vi.mock('../../lib/openChat', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/openChat')>();
  return { ...orig, openChatById: async (id: string) => { mockOpenChat.calls.push(id); return true; } };
});
vi.mock('../../lib/tasks', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/tasks')>();
  return { ...orig, openTaskInSection: (t: { id: string; projectId?: string }) => { mockOpenTask.calls.push(t); } };
});

// ── Фиксированное «сейчас» (стабильные длительности тревоги) ───────────────────
const NOW = new Date('2026-10-19T12:00:00Z').getTime();

function feat(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Моя фича', projectId: 'p1',
    bucket: 'running', state: 'running', attention: null,
    rail: [
      { key: 'cut', state: 'done', tone: null },
      { key: 'plan', state: 'done', tone: null },
      { key: 'prod', state: 'active', tone: 'run' },
      { key: 'review', state: 'pending', tone: null },
      { key: 'qa', state: 'pending', tone: null },
      { key: 'final', state: 'pending', tone: null },
      { key: 'merge', state: 'pending', tone: null },
    ],
    chips: ['open'],
    stepsAccepted: 1, stepsTotal: 3, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: 'chat-key', keyArchived: false, keyProjectId: 'p1', chatCount: 2,
    branch: 'feat/my-feature', createdAt: '2026-10-18T06:00:00Z', taskCount: 5,
    ...over,
  } as FeatureIndexFeature;
}

function pageData(run: Partial<FeatureRun> = {}): FeaturePageData {
  return {
    run: {
      featureId: 'f1',
      state: 'running',
      epic: { id: 'f1', title: 'Моя фича' },
      steps: [], reviews: [], qa: [], final: null, merge: null,
      verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
      progress: 0, ready: false,
      ...run,
    },
    events: [], eventsTotal: 0,
  };
}

// Тревога: since 30 минут назад (NOW − 30 мин) — длительность «30 мин» стабильна.
function attention(kind: FeatureAttention['kind'], over: Partial<FeatureAttention> = {}): FeatureAttention {
  return {
    kind,
    taskId: 't1',
    since: new Date(NOW - 30 * 60000).toISOString(),
    reason: null,
    personaId: 'p-kira',
    linkedSessionId: 'chat-kira',
    ...over,
  };
}

type AnyNode = { nodeType: number; nodeName: string; childNodes: AnyNode[] };

// Вкладка по подписи (первый текстовый узел кнопки = подпись; счётчик — отдельно).
function tabByLabel(container: AnyNode, label: string): AnyNode | null {
  let hit: AnyNode | null = null;
  const walk = (x: AnyNode) => {
    if (hit) return;
    if (x.nodeType === 1 && x.nodeName === 'BUTTON') {
      const n = x as unknown as { getAttribute: (a: string) => string | null };
      if (n.getAttribute('role') === 'tab') {
        const first = textNodes(x)[0];
        if (first === label) { hit = x; return; }
      }
    }
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return hit;
}

// Корни всех mount'ов теста: unmount в afterEach (иначе два живых корня дёргают
// общий стор и первый рендер «зависает» в памяти).
const roots: import('react-dom/client').Root[] = [];

function render(props: { featureId?: string; projectId?: string | null; isMobile?: boolean; onBack?: (t: FeaturePageBackTarget) => void }) {
  installFakeDom();
  // fakeDom'ов window/document без addEventListener/querySelector — минимальные
  // (Esc-обработчик и меню). dispatchEvent → реальные слушатели.
  const win = globalThis as any;
  const doc = (globalThis as any).document;
  if (!win.window.addEventListener) {
    const listeners: Record<string, Set<Function>> = {};
    win.window.addEventListener = (t: string, f: Function) => { (listeners[t] ??= new Set()).add(f); };
    win.window.removeEventListener = (t: string, f: Function) => { listeners[t]?.delete(f); };
    win.window.dispatchEvent = (e: { type: string }) => { listeners[e.type]?.forEach(f => f(e)); return true; };
    win.__winListeners = listeners;
  }
  if (!doc.querySelector) doc.querySelector = () => null;
  const { root, container } = mount(createElement(FeaturePage, {
    featureId: props.featureId ?? 'f1',
    projectId: props.projectId ?? 'p1',
    isMobile: props.isMobile ?? false,
    onBack: props.onBack ?? (() => {}),
  }));
  roots.push(root);
  return container;
}

function unmountAll() {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
}

// Дождаться разрешения fetch (fake timers + microtasks) → финальное состояние.
async function settle() {
  await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
}

// ── Общий сброс ──────────────────────────────────────────────────────────────────
beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(NOW);
  pageTest.reset();
  indexTest.reset();
  backNavTest.reset();
  personaMap.clear();
  setAllFlags({ 'features-overview': true });
  mockIndex.features = [feat()];
  mockIndex.chats = {};
  mockChats.clear();
  mockGetFeature.pending = false;
  mockGetFeature.result = pageData();
  mockGetFeature.error = null;
  mockGetFeature.calls = [];
  mockOpenChat.calls = [];
  mockOpenTask.calls = [];
  mockToast.calls = [];
  mockMerge.calls = [];
  mockMerge.pending = false;
  mockMerge.error = null;
  mockMerge.result = null;
  indexTest.setIndex('p1', mockIndex as FeaturesIndex);
});
afterEach(() => {
  unmountAll();
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
});

// ═══ 1. Тег статуса: run.state (9 состояний) ═══════════════════════════════════
describe('Тег: run.state → подпись (макет §3)', () => {
  it('9 состояний → 8 подписей: idle показывается как «не начата»', () => {
    const cases: [FeatureState, string][] = [
      ['not_started', 'не начата'],
      ['idle', 'не начата'],
      ['running', 'идёт'],
      ['waiting', 'ждёт вас'],
      ['stopped', 'стоит'],
      ['merge_pending', 'ждёт согласия на мерж'],
      ['merging', 'мерж · идёт'],
      ['merged', 'смержена'],
      ['closed', 'закрыта без мержа'],
    ];
    for (const [state, label] of cases) {
      expect(featureRunStatusTag(state).label, `state=${state}`).toBe(label);
    }
  });

  it('running с активной стадией — «идёт · {этап}»', () => {
    expect(featureRunStatusTag('running', 'Производство').label).toBe('идёт · Производство');
    expect(featureRunStatusTag('running', null).label).toBe('идёт');
  });

  it('точка — у «ждёт вас» и «ждёт согласия на мерж»', () => {
    expect(featureRunStatusTag('waiting').dot).toBe(true);
    expect(featureRunStatusTag('merge_pending').dot).toBe(true);
    // У остальных состояний точки нет (undefined) — не false.
    expect(featureRunStatusTag('running').dot).toBeFalsy();
    expect(featureRunStatusTag('stopped').dot).toBeFalsy();
  });

  it('мутация: перепутать merge/wait → тона расходятся (red)', () => {
    // merge_pending — тон merge (не wait), waiting — тон wait (не merge).
    expect(featureRunStatusTag('merge_pending').tone).toBe('merge');
    expect(featureRunStatusTag('waiting').tone).toBe('wait');
  });

  it('страница: тег в шапке (1280) по run.state', async () => {
    mockGetFeature.result = pageData({ state: 'waiting' });
    const c = render({});
    await settle();
    const tag = findNodeByAttr(c, 'data-testid', 'feature-page-status');
    expect(tag).not.toBeNull();
    expect(textNodes(tag!).join('')).toContain('ждёт вас');
  });

  it('страница: 360 — тег в мете, не в шапке', async () => {
    mockGetFeature.result = pageData({ state: 'waiting' });
    const c = render({ isMobile: true });
    await settle();
    const meta = findNodeByAttr(c, 'data-testid', 'feature-page-meta');
    expect(meta).not.toBeNull();
    expect(textNodes(meta!).join('')).toContain('ждёт вас');
  });
});

// ═══ 2. Вкладки: дефолт, сохранение, 360 ════════════════════════════════════════
describe('Вкладки (макет §4)', () => {
  it('по умолчанию «Хронология»; мутация дефолта на «Чаты» → red', async () => {
    const c = render({});
    await settle();
    const tabs = findNodeByAttr(c, 'data-testid', 'feature-page-tabs');
    expect(tabs).not.toBeNull();
    const tl = tabByLabel(c, 'Хронология');
    expect(tl).not.toBeNull();
    expect((tl as unknown as { getAttribute: (a: string) => string | null }).getAttribute('aria-selected')).toBe('true');
    // Мутация (в отчёте): DEFAULT_TAB = 'chats' → «Хронология» не выбрана → red.
  });

  it('выбор сохраняется: «Чаты» переживает размонтирование (стор, не DOM)', async () => {
    const c = render({});
    await settle();
    // Переключаем на «Чаты» (стор страницы).
    await act(async () => { setFeaturePageTab('chats'); });
    // Новый mount (страницу «скрыли и вернули»): выбор из стора.
    const c2 = render({});
    await settle();
    const chats = tabByLabel(c2, 'Чаты');
    expect(chats).not.toBeNull();
    expect((chats as unknown as { getAttribute: (a: string) => string | null }).getAttribute('aria-selected')).toBe('true');
    const tl = tabByLabel(c2, 'Хронология');
    expect((tl as unknown as { getAttribute: (a: string) => string | null }).getAttribute('aria-selected')).toBe('false');
  });

  it('счётчики: задачи — число, события 0 — без счётчика', async () => {
    // feat().taskCount = 5 → счётчик «Задачи» = 5; eventsTotal = 0 → «События» без счётчика.
    const c = render({});
    await settle();
    const tasks = tabByLabel(c, 'Задачи');
    expect(tasks).not.toBeNull();
    expect(textNodes(tasks!).join('')).toContain('5');
    const events = tabByLabel(c, 'События');
    expect(events).not.toBeNull();
    expect(textNodes(events!).join('')).not.toContain('0');
  });

  it('360: полоса вкладок — горизонтальная прокрутка (tablist overflow-x)', async () => {
    const c = render({ isMobile: true });
    await settle();
    const tabs = findNodeByAttr(c, 'data-testid', 'feature-page-tabs');
    expect(tabs).not.toBeNull();
    // Таблицей «прокручивается» = tablist (кит) с overflowX auto; fakeDom держит
    // style-объект на узле.
    const tablist = (tabs as unknown as { childNodes: AnyNode[] }).childNodes.find(n => {
      const p = (n as unknown as { __reactProps$?: never }) && (n as AnyNode & { getAttribute?: (a: string) => string | null });
      return p && (p as AnyNode & { getAttribute: (a: string) => string | null }).getAttribute?.('role') === 'tablist';
    });
    expect(tablist).toBeDefined();
    const style = (tablist as unknown as { style: Record<string, unknown> }).style;
    expect(style.overflowX).toBe('auto');
  });
});

// ═══ 3. Тревога: attention.kind → тон + главное действие ═══════════════════════
describe('Тревога (макет §2)', () => {
  it('wait: тон info, «Ответить {имя}» в шапке (1280), клик → чат', async () => {
    personaMap.set('p-kira', 'Кира');
    mockGetFeature.result = pageData({ state: 'waiting', attention: attention('wait') });
    const c = render({});
    await settle();
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm');
    expect(alarm).not.toBeNull();
    expect((alarm as unknown as { getAttribute: (a: string) => string | null }).getAttribute('data-tone')).toBe('info');
    // Главное действие — в шапке (1280), не в блоке.
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action');
    expect(main).not.toBeNull();
    // Имя — как в сторе персон (именительный); точный падеж (дательный) — полировка, не каркас.
    expect(textNodes(main!).join('')).toContain('Ответить Кира');
    // Клик → чат исполнителя.
    const btn = (main as unknown as { childNodes: AnyNode[] }).childNodes.find(n => n.nodeType === 1 && n.nodeName === 'BUTTON');
    const props = (btn as unknown as { __reactProps$?: string }) && null;
    // reactProps-помощник:
    const { reactProps } = await import('../chat/__tests__/fakeDom');
    const p = reactProps(btn!) as { onClick?: (e: unknown) => void } | null;
    await act(async () => { p?.onClick?.({}); });
    expect(mockOpenChat.calls).toContain('chat-kira');
  });

  it('stopped: тон danger, «Чат {имя}»', async () => {
    personaMap.set('p-kira', 'Кира');
    mockGetFeature.result = pageData({ state: 'stopped', attention: attention('stopped') });
    const c = render({});
    await settle();
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm');
    expect(alarm).not.toBeNull();
    expect((alarm as unknown as { getAttribute: (a: string) => string | null }).getAttribute('data-tone')).toBe('danger');
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action');
    expect(main).not.toBeNull();
    expect(textNodes(main!).join('')).toContain('Чат Кира');
  });

  it('merge_pending: тон info, «Мерж…» (Ф6: кнопка → общая модалка), пара «чат · задача»', async () => {
    mockGetFeature.result = pageData({ state: 'merge_pending', attention: attention('merge') });
    const c = render({});
    await settle();
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm');
    expect(alarm).not.toBeNull();
    expect((alarm as unknown as { getAttribute: (a: string) => string | null }).getAttribute('data-tone')).toBe('info');
    const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action');
    expect(main).not.toBeNull();
    expect(textNodes(main!).join('')).toContain('Мерж…');
    // Пара «чат · задача» в блоке.
    expect(textNodes(alarm!).join('')).toContain('чат');
    expect(textNodes(alarm!).join('')).toContain('задача');
  });

  it('мутация: перепутать merge/wait в тревоге → тон wait=info краснеет (red)', async () => {
    // wait — info; если перепутать с merge-логикой (danger/stop) — тест выше падает.
    // Здесь фиксируем: wait ≠ stopped по тону.
    personaMap.set('p-kira', 'Кира');
    mockGetFeature.result = pageData({ state: 'waiting', attention: attention('wait') });
    const c = render({});
    await settle();
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm')!;
    expect((alarm as unknown as { getAttribute: (a: string) => string | null }).getAttribute('data-tone')).toBe('info');
  });

  it('360: главное действие — в блоке тревоги (не в шапке)', async () => {
    personaMap.set('p-kira', 'Кира');
    mockGetFeature.result = pageData({ state: 'waiting', attention: attention('wait') });
    const c = render({ isMobile: true });
    await settle();
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm');
    expect(alarm).not.toBeNull();
    expect(textNodes(alarm!).join('')).toContain('Ответить Кира');
    // В шапке (1280-слот) главного действия нет: на 360 оно только в блоке.
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-main-action')).toBeNull();
  });

  it('тревоги нет (running) — блока нет', async () => {
    mockGetFeature.result = pageData({ state: 'running', attention: null });
    const c = render({});
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'feature-page-alarm')).toBeNull();
  });

  it('«задача» в тревоге → openTaskInSection(id тревоги)', async () => {
    mockGetFeature.result = pageData({ state: 'waiting', attention: attention('wait', { taskId: 't-alert' }) });
    const c = render({});
    await settle();
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm')!;
    // Найти кнопку «задача» (NavTargetButton) и кликнуть.
    const btns = (function collect(x: AnyNode, out: AnyNode[] = []): AnyNode[] {
      if (x.nodeType === 1 && x.nodeName === 'BUTTON') {
        if (textNodes(x).join('').includes('задача')) out.push(x);
      }
      for (const ch of x.childNodes) collect(ch, out);
      return out;
    })(alarm);
    expect(btns.length).toBeGreaterThan(0);
    const { reactProps } = await import('../chat/__tests__/fakeDom');
    const p = reactProps(btns[0]) as { onClick?: (e: unknown) => void } | null;
    await act(async () => { p?.onClick?.({}); });
    expect(mockOpenTask.calls.some(t => t.id === 't-alert')).toBe(true);
  });
});

// ═══ 4. Чистая функция: главное действие ══════════════════════════════════════
describe('featurePrimaryAction', () => {
  it('wait → «Ответить {имя}» (chat), stopped → «Чат {имя}» (chat), merge → «Мерж…»', () => {
    const a = (kind: FeatureAttention['kind']): FeatureAttention =>
      ({ kind, taskId: 't1', since: null, reason: null, personaId: 'p1', linkedSessionId: 'c1' });
    expect(featurePrimaryAction(a('wait'), 'Кира')?.label).toBe('Ответить Кира');
    expect(featurePrimaryAction(a('stopped'), 'Кира')?.label).toBe('Чат Кира');
    expect(featurePrimaryAction(a('merge'), 'Кира')?.label).toBe('Мерж…');
    expect(featurePrimaryAction(null)).toBeNull();
  });
});

// ═══ 5. Ф6: действие «Мерж…» — гейты, диалог, POST /merge, 409/500, 360 ═══════
//
// «Мерж…» — только state=merge_pending; merging/merged/closed/running — нет (только
// пара «чат · задача»). Клик → общая модалка (портал в body) → «Мерж» → POST /merge.
// Двойной клик → 1 запрос (ref-гард); «Запускаю…» disabled; 409 — без красной ошибки
// (состояние обновляется); 500 — сообщение + «Повторить»; «Отмена» — без запроса;
// 360 — шторка.

// Первый <button> в поддереве.
function firstButton(n: AnyNode): AnyNode | null {
  if (n.nodeType === 1 && n.nodeName === 'BUTTON') return n;
  for (const c of n.childNodes) {
    const hit = firstButton(c);
    if (hit) return hit;
  }
  return null;
}

// <button> в поддереве с ТОЧНЫМ текстом (trim).
function buttonByText(n: AnyNode, text: string): AnyNode | null {
  let hit: AnyNode | null = null;
  const walk = (x: AnyNode) => {
    if (hit) return;
    if (x.nodeType === 1 && x.nodeName === 'BUTTON' && textNodes(x).join('').trim() === text) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(n);
  return hit;
}

// document.body как AnyNode (порталы модалки).
function bodyNode(): AnyNode {
  return (globalThis as { document: { body: unknown } }).document.body as unknown as AnyNode;
}

// Открыть диалог: клик по «Мерж…» (главное действие, 1280).
async function openMergeDialog(c: AnyNode) {
  const main = findNodeByAttr(c, 'data-testid', 'feature-page-main-action');
  expect(main, 'нет главного действия «Мерж…» (merge_pending, 1280)').not.toBeNull();
  const openBtn = firstButton(main as never)!;
  await act(async () => { (reactProps(openBtn)!.onClick as (e: unknown) => void)({}); });
}

describe('Ф6: «Мерж…» на странице фичи', () => {
  // ── Гейты: «Мерж…» только merge_pending ─────────────────────────────────────────
  it('merge_pending → «Мерж…» есть; merging/merged/closed/running → нет', async () => {
    const cases: [FeatureState, FeatureAttention | null, boolean][] = [
      ['merge_pending', attention('merge'), true],
      // merging: attention merge (пара «чат · задача»), но «Мерж…» — нет (state-гейт).
      ['merging', attention('merge'), false],
      ['merged', null, false],
      ['closed', null, false],
      ['running', null, false],
    ];
    for (const [state, att, expectMerge] of cases) {
      mockGetFeature.result = pageData({ state, attention: att });
      const c = render({});
      await settle();
      const all = textNodes(c).join('');
      // «Мерж…» (с многоточием) — только merge_pending.
      expect(all.includes('Мерж…'), `state=${state}: «Мерж…» должен быть ${expectMerge ? 'есть' : 'нет'}`).toBe(expectMerge);
    }
  });

  // ── Подтверждение → POST /merge один раз (двойной клик) ─────────────────────────
  it('«Мерж…» → диалог → «Мерж» → POST /merge (1 вызов) + тост', async () => {
    mockGetFeature.result = pageData({ state: 'merge_pending', attention: attention('merge') });
    mockMerge.result = { id: 'merge-1', title: 'Мерж · Моя фича', status: 'inProgress', description: '', priority: 'medium' } as Task;
    const c = render({});
    await settle();
    await openMergeDialog(c);
    const confirm = buttonByText(bodyNode(), 'Мерж');
    expect(confirm, 'нет кнопки «Мерж» в диалоге').not.toBeNull();
    await act(async () => { (reactProps(confirm!)!.onClick as (e: unknown) => void)({}); });
    await settle();
    expect(mockMerge.calls, 'POST /merge не вызван').toHaveLength(1);
    expect(mockToast.calls.some(([t]) => t === 'Задача мержа создана'), 'нет тоста об успехе').toBe(true);
  });

  it('двойной клик по «Мерж» → POST 1 раз (ref-гард, state батчится)', async () => {
    mockGetFeature.result = pageData({ state: 'merge_pending', attention: attention('merge') });
    mockMerge.pending = true; // запрос в полёте (не разрешается)
    const c = render({});
    await settle();
    await openMergeDialog(c);
    const confirm = buttonByText(bodyNode(), 'Мерж')!;
    // ДВА клика в ОДНОМ act (один тик): setState батчится, ref-гард — синхронный.
    await act(async () => {
      (reactProps(confirm)!.onClick as (e: unknown) => void)({});
      (reactProps(confirm)!.onClick as (e: unknown) => void)({});
    });
    expect(mockMerge.calls, 'двойной клик → >1 запроса (нет ref-гарда)').toHaveLength(1);
  });

  it('после «Мерж»: «Запускаю…» disabled (запрос в полёте)', async () => {
    mockGetFeature.result = pageData({ state: 'merge_pending', attention: attention('merge') });
    mockMerge.pending = true;
    const c = render({});
    await settle();
    await openMergeDialog(c);
    const confirm = buttonByText(bodyNode(), 'Мерж')!;
    await act(async () => { (reactProps(confirm)!.onClick as (e: unknown) => void)({}); });
    // Кнопка стала «Запускаю…» и заблокирована.
    const launching = buttonByText(bodyNode(), 'Запускаю…');
    expect(launching, 'нет «Запускаю…» после подтверждения').not.toBeNull();
    expect(reactProps(launching!)!.disabled, '«Запускаю…» не disabled').toBe(true);
  });

  // ── 409: без красной ошибки, состояние обновляется (диалог закрывается) ──────────
  it('409 «мерж уже идёт»: диалог закрывается, красной ошибки нет, POST 1 раз', async () => {
    mockGetFeature.result = pageData({ state: 'merge_pending', attention: attention('merge') });
    mockMerge.error = { name: 'HttpError', status: 409, message: 'Мерж уже идёт' } as Error & { status?: number };
    const c = render({});
    await settle();
    await openMergeDialog(c);
    const confirm = buttonByText(bodyNode(), 'Мерж')!;
    await act(async () => { (reactProps(confirm)!.onClick as (e: unknown) => void)({}); });
    await settle();
    expect(mockMerge.calls).toHaveLength(1);
    // Красной ошибки (feature-merge-error) нет; диалог закрыт (кнопки «Мерж»/«Отмена» исчезли).
    expect(buttonByText(bodyNode(), 'Запускаю…'), '409: «Запускаю…» должен исчезнуть (диалог закрыт)').toBeNull();
    const all = textNodes(bodyNode()).join('');
    expect(all.includes('Мерж'), '409: диалог не закрылся (кнопка «Мерж» на месте)').toBe(false);
  });

  // ── refetchFeaturePage: после успеха и 409 страница перезапрашивается (minor 4) ──
  it('успех мержа → refetchFeaturePage (getFeature 2-й раз)', async () => {
    mockGetFeature.result = pageData({ state: 'merge_pending', attention: attention('merge') });
    mockMerge.result = { id: 'merge-1', title: 'Мерж · Моя фича', status: 'inProgress', description: '', priority: 'medium' } as Task;
    const c = render({});
    await settle();
    expect(mockGetFeature.calls, 'первичный запрос').toHaveLength(1);
    await openMergeDialog(c);
    const confirm = buttonByText(bodyNode(), 'Мерж')!;
    await act(async () => { (reactProps(confirm)!.onClick as (e: unknown) => void)({}); });
    await settle();
    expect(mockGetFeature.calls, 'refetch после успеха (2-й запрос)').toHaveLength(2);
  });

  it('409 → refetchFeaturePage (getFeature 2-й раз)', async () => {
    mockGetFeature.result = pageData({ state: 'merge_pending', attention: attention('merge') });
    mockMerge.error = { name: 'HttpError', status: 409, message: 'Мерж уже идёт' } as Error & { status?: number };
    const c = render({});
    await settle();
    expect(mockGetFeature.calls, 'первичный запрос').toHaveLength(1);
    await openMergeDialog(c);
    const confirm = buttonByText(bodyNode(), 'Мерж')!;
    await act(async () => { (reactProps(confirm)!.onClick as (e: unknown) => void)({}); });
    await settle();
    expect(mockGetFeature.calls, 'refetch после 409 (2-й запрос)').toHaveLength(2);
  });

  // ── 500: сообщение сервера + «Повторить» (диалог остаётся) ──────────────────────
  it('500: сообщение сервера + «Повторить» (диалог открыт)', async () => {
    mockGetFeature.result = pageData({ state: 'merge_pending', attention: attention('merge') });
    mockMerge.error = { name: 'HttpError', status: 500, message: 'Worktree фичи не найден на диске' } as Error & { status?: number };
    const c = render({});
    await settle();
    await openMergeDialog(c);
    const confirm = buttonByText(bodyNode(), 'Мерж')!;
    await act(async () => { (reactProps(confirm)!.onClick as (e: unknown) => void)({}); });
    await settle();
    const all = textNodes(bodyNode()).join('');
    expect(all, 'нет сообщения сервера (500)').toContain('Worktree фичи не найден на диске');
    expect(buttonByText(bodyNode(), 'Повторить'), 'нет «Повторить» (500)').not.toBeNull();
  });

  // ── «Отмена»: без запроса, диалог закрывается ─────────────────────────────────────
  it('«Отмена» → диалог закрывается, POST /merge не шлётся', async () => {
    mockGetFeature.result = pageData({ state: 'merge_pending', attention: attention('merge') });
    const c = render({});
    await settle();
    await openMergeDialog(c);
    const cancel = buttonByText(bodyNode(), 'Отмена');
    expect(cancel, 'нет «Отмена» в диалоге').not.toBeNull();
    await act(async () => { (reactProps(cancel!)!.onClick as (e: unknown) => void)({}); });
    await settle();
    expect(mockMerge.calls, '«Отмена» послал POST /merge').toHaveLength(0);
    // Диалог закрыт: «Мерж»/«Отмена» исчезли.
    expect(buttonByText(bodyNode(), 'Отмена'), '«Отмена» на месте (диалог не закрыт)').toBeNull();
  });

  // ── 360: «Мерж…» в блоке тревоги, диалог — шторка ────────────────────────────────
  it('360: «Мерж…» в блоке тревоги; диалог — шторка (cc-sheet-card)', async () => {
    mockGetFeature.result = pageData({ state: 'merge_pending', attention: attention('merge') });
    const c = render({ isMobile: true });
    // useIsMobileModal читает window.matchMedia (fakeDom по умолчанию matches:false) —
    // для шторки переопределяем в matches:true (после installFakeDom, до рендера модалки).
    (globalThis as { window: Record<string, unknown> }).window.matchMedia =
      () => ({ matches: true, addEventListener: () => {}, removeEventListener: () => {} });
    await settle();
    // На 360 главное действие — в блоке тревоги (не в шапке).
    const alarm = findNodeByAttr(c, 'data-testid', 'feature-page-alarm')!;
    const openBtn = firstButton(alarm as never);
    expect(openBtn, 'нет «Мерж…» в блоке тревоги (360)').not.toBeNull();
    expect(textNodes(openBtn!).join('').trim()).toBe('Мерж…');
    // Клик → диалог (шторка).
    await act(async () => { (reactProps(openBtn!)!.onClick as (e: unknown) => void)({}); });
    // Шторка (cc-sheet-card) на месте; карточки (cc-modal-card) нет.
    const body = bodyNode();
    const has = (cls: string) => {
      let hit = false;
      const walk = (x: AnyNode) => {
        if (hit) return;
        if (x.nodeType === 1 && (x as unknown as { getAttribute?: (a: string) => string | null }).getAttribute?.('class')?.includes(cls)) { hit = true; return; }
        for (const ch of x.childNodes) walk(ch);
      };
      walk(body);
      return hit;
    };
    expect(has('cc-sheet-card'), '360: нет шторки (cc-sheet-card)').toBe(true);
    expect(has('cc-modal-card'), '360: карточка вместо шторки').toBe(false);
  });
});

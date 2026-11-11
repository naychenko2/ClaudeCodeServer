// FS-FC: рендер карточки FeatureChatCard (макет v5 §2–§4). Окружение — настоящий
// компонент на fakeDom с живыми сторами (featuresIndex/featureRuns/featureBackNav/
// featurePage), как в FeatureBackChip.test и SessionList.test: «run приходит позже
// индекса» и «указатель меняется на живом компоненте» достижимы только так.
//
// Что защищается (постановка): ни одного aria-expanded в карточке; в ключевом чате —
// бейдж вместо кнопки «Ключевой чат»; «К фиче» с указателем ведёт на записанную
// вкладку (returnToFeature), без указателя — на «Ход» (cc-open-feature, fromChat:true);
// действие тревоги в своём чате — фокус поля, не переход; мобила — кнопки 40, тега нет.
//
// Мутации (в отчёте): (1) рисовать «Ключевой чат» и в ключевом чате → краснеет ровно
// тест ключевого чата; (2) «К фиче» без указателя не ставит «Ход» → краснеет клик
// без указателя; (3) опустить minHeight 40 на мобиле → краснеет тест 360.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureChatCard } from './FeatureChatCard';
import { refetchFeatureRuns, __test as runsStore } from '../../../lib/featureRuns';
import { __test as indexStore } from '../../../lib/featuresIndex';
import { __test as backNav } from '../../../lib/featureBackNav';
import { __test as pageStore, setFeatureMainTab, getFeatureMainTab } from '../../../lib/featurePage';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps } from '../__tests__/fakeDom';
import type { FeatureIndexFeature, FeaturesIndex } from '../../../types/featureIndex';
import type { FeatureRun } from '../../../types/featureRun';

const { personaMap, data, openChatCalls } = vi.hoisted(() => ({
  personaMap: new Map<string, string>([['p-kira', 'Кира'], ['p-gleb', 'Глеб']]),
  // Ответы api для сторов, которые их читают: индекс проекта и run'ы чата. Меняются
  // в тесте — это и есть «run приходит позже».
  data: { index: { features: [], chats: {} } as unknown, runs: [] as unknown[] },
  openChatCalls: [] as string[],
}));
vi.mock('../../../lib/personas', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/personas')>();
  return { ...orig, getPersonaById: (id: string) => (personaMap.has(id) ? { id, name: personaMap.get(id) } : undefined) };
});
vi.mock('../../../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/api')>();
  return { ...orig, api: { ...orig.api, tasks: { ...orig.api.tasks,
    featuresIndex: async () => data.index as FeaturesIndex,
    getFeatures: async () => data.runs as FeatureRun[],
  } } };
});
// openChatById — наблюдаемый spy: «один клик — ровно одно действие» мерится числом вызовов.
vi.mock('../../../lib/openChat', () => ({
  openChatById: (id: string) => { openChatCalls.push(id); return Promise.resolve(true); },
}));
// useFeatureRuns.wireRealtime зовёт signalr.onMessage — в node hub не построить (нет URL).
vi.mock('../../../lib/signalr', () => ({ onMessage: () => () => {}, onReconnected: () => () => {} }));

function feat(over: Partial<FeatureIndexFeature> = {}): FeatureIndexFeature {
  return {
    featureId: 'f1', title: 'Правда о прогрессе', projectId: 'p1',
    bucket: 'running', state: 'running', attention: null,
    rail: [], chips: ['open'],
    stepsAccepted: 0, stepsTotal: 0, loops: 0,
    now0: null, lastActivityAt: null, summary: null,
    keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
    ...over,
  } as FeatureIndexFeature;
}

function run(over: Partial<FeatureRun> = {}): FeatureRun {
  return {
    featureId: 'f1', state: 'running',
    epic: { id: 'f1', title: 'Фича', status: 'todo' },
    steps: [], reviews: [], qa: [], final: null, merge: null,
    verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
    progress: 0, ready: false, now: [], attention: null, closed: false, summary: null,
    ...over,
  } as FeatureRun;
}

const sp = (accepted: number, total: number) => ({
  planState: 'cut' as const, total, accepted, review: 0, work: 0, notCreated: 0, withdrawn: 0, unlinkedReworks: 0,
});
// Числа строки индекса (фолбэк без run): те же поля, что отдаёт бэк в features/index.
const indexProgress = { stepsAccepted: 5, stepsTotal: 30, stepsReview: 2, stepsWork: 3, stepsNotCreated: 20, planState: 'cut' as const };

let focusCount = 0;
const roots: import('react-dom/client').Root[] = [];

function seedIndex(f: FeatureIndexFeature, sessionId = 's-me'): void {
  const idx = {
    features: [f],
    chats: { [sessionId]: [{ featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш8', lane: 'front' }] },
  } as FeaturesIndex;
  indexStore.reset();
  indexStore.setIndex('p1', idx);
  data.index = idx; // эффект useFeaturesIndex возвращает тот же срез — seeded не затирается
}

async function renderCard(over: { isMobile?: boolean; sessionId?: string } = {}) {
  const { root, container } = mount(createElement(FeatureChatCard, {
    sessionId: over.sessionId ?? 's-me', projectId: 'p1', isMobile: over.isMobile ?? false,
    onFocusComposer: () => { focusCount++; },
  }));
  roots.push(root);
  // Первичные запросы сторов (effects) — промисы: ждём полный хвост микрозадач.
  await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
  return container;
}

// Диспатчер-журнал: пишет события и исполняет роль воркспейса (handled=true для
// cc-open-feature — как обработчик WorkspacePage), чтобы «один клик — одно действие»
// мерился длиной журнала.
function eventLog(): { type: string; detail: Record<string, unknown> }[] {
  const log: { type: string; detail: Record<string, unknown> }[] = [];
  (globalThis as { window: { dispatchEvent: (e: Event) => boolean } }).window.dispatchEvent = (e: Event) => {
    const detail = (e as CustomEvent).detail as Record<string, unknown>;
    log.push({ type: e.type, detail });
    if (e.type === 'cc-open-feature') detail.handled = true;
    return true;
  };
  return log;
}

function click(node: ReturnType<typeof findNodeByAttr>): void {
  act(() => { (reactProps(node!)!.onClick as () => void)(); });
}

function walk(n: NonNullable<ReturnType<typeof findNodeByAttr>>, fn: (x: NonNullable<ReturnType<typeof findNodeByAttr>>) => void): void {
  fn(n);
  for (const c of (n as { childNodes: NonNullable<ReturnType<typeof findNodeByAttr>>[] }).childNodes) walk(c, fn);
}
function countAriaExpanded(c: NonNullable<ReturnType<typeof findNodeByAttr>>): number {
  let k = 0;
  walk(c, (x) => { if (x.nodeType === 1 && x.getAttribute('aria-expanded') != null) k++; });
  return k;
}
function buttonNodes(c: NonNullable<ReturnType<typeof findNodeByAttr>>) {
  const out: NonNullable<ReturnType<typeof findNodeByAttr>>[] = [];
  walk(c, (x) => { if (x.nodeType === 1 && x.nodeName === 'BUTTON') out.push(x); });
  return out;
}
const textOf = (n: NonNullable<ReturnType<typeof findNodeByAttr>>): string => textNodes(n).join('');

beforeEach(() => {
  indexStore.reset(); runsStore.reset(); backNav.reset(); pageStore.reset();
  data.index = { features: [], chats: {} }; data.runs = [];
  openChatCalls.length = 0; focusCount = 0;
  installFakeDom();
});
afterEach(() => {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
  uninstallFakeDom();
});

describe('FeatureChatCard: чат шага и ключевой чат (c1)', () => {
  it('чат шага: ни одного aria-expanded, нет шеврона/раскрытия; «Ключевой чат» один и ведёт openChatById(keySessionId) ровно один раз', async () => {
    seedIndex(feat({ ...indexProgress, keySessionId: 's-key', nowPersonaIds: ['p-kira'] }));
    const c = await renderCard();
    expect(findNodeByAttr(c, 'data-testid', 'feature-chat-card'), 'карточки нет').not.toBeNull();
    // Инвариант v5: ничего не сворачивается — ни одного aria-expanded во всём дереве карточки.
    expect(countAriaExpanded(c), 'в карточке появился раскрываемый элемент').toBe(0);
    expect(findNodeByAttr(c, 'data-testid', 'feature-row-expand'), 'шеврон старой строки').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-row-body'), 'раскрытие старой строки').toBeNull();
    const key = findNodeByAttr(c, 'data-testid', 'feature-card-to-key');
    expect(key, 'кнопки «Ключевой чат» нет').not.toBeNull();
    expect(textOf(key!)).toBe('Ключевой чат');
    const log = eventLog();
    click(key);
    expect(openChatCalls, 'openChatById зван не один раз/не туда').toEqual(['s-key']);
    expect(log.length, 'один клик — ровно одно действие').toBe(0); // переход — openChatById, событий нет
  });

  it('ключевой чат: кнопки «Ключевой чат» нет, есть бейдж «это ключевой чат»', async () => {
    seedIndex(feat({ ...indexProgress, keySessionId: 's-key' }), 's-key');
    const c = await renderCard({ sessionId: 's-key' });
    expect(findNodeByAttr(c, 'data-testid', 'feature-card-to-key'), 'кнопка «Ключевой чат» в ключевом чате').toBeNull();
    const badge = findNodeByAttr(c, 'data-testid', 'feature-card-key-badge');
    expect(badge, 'бейджа ключевого чата нет').not.toBeNull();
    expect(textOf(badge!)).toBe('это ключевой чат');
    // Бейдж — span (Badge без onClick): не кнопка и не раскрываемый.
    expect(badge!.nodeName).not.toBe('BUTTON');
  });
});

describe('FeatureChatCard: «К фиче» (c2)', () => {
  it('с указателем на эту фичу (tab=materials): подпись «‹ К фиче · Материалы», клик — returnToFeature (на записанную вкладку, fromChat:false)', async () => {
    seedIndex(feat({ ...indexProgress }));
    setFeatureMainTab('materials');
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 's-me' }, 'Правда о прогрессе');
    const c = await renderCard();
    const btn = findNodeByAttr(c, 'data-testid', 'feature-card-to-feature');
    expect(btn, 'кнопки «К фиче» нет').not.toBeNull();
    expect(textOf(btn!), 'подпись с вкладкой').toContain('Материалы');
    setFeatureMainTab('review'); // вкладку могли сменить, пока страница была скрыта — возврат на записанную
    const log = eventLog();
    click(btn);
    expect(getFeatureMainTab(), 'вернулись не на записанную вкладку').toBe('materials');
    const ev = log.find((d) => d.type === 'cc-open-feature');
    expect(ev, 'страница фичи не открылась').toBeDefined();
    expect(ev!.detail).toMatchObject({ featureId: 'f1', fromChat: false });
  });

  it('без указателя: клик ставит «Ход» и диспатчит cc-open-feature { featureId, fromChatId, fromChat:true } — ровно одно событие', async () => {
    seedIndex(feat({ ...indexProgress }));
    setFeatureMainTab('review'); // «progress» не должна получиться по умолчанию
    const c = await renderCard();
    const btn = findNodeByAttr(c, 'data-testid', 'feature-card-to-feature');
    expect(textOf(btn!), 'без указателя нет подписи «К фиче ›»').toBe('К фиче ›');
    const log = eventLog();
    click(btn);
    expect(getFeatureMainTab(), 'вкладка «Ход» не выставлена').toBe('progress');
    expect(log.length, 'один клик — ровно одно событие').toBe(1);
    expect(log[0].type).toBe('cc-open-feature');
    expect(log[0].detail).toMatchObject({ featureId: 'f1', fromChatId: 's-me', fromChat: true });
  });

  it('указатель на другую фичу — «К фиче» карточки ведёт по-своему (returnToFeature чужого указателя не зовёт)', async () => {
    seedIndex(feat({ ...indexProgress }));
    backNav.recordFeatureReturn('f2', { kind: 'chat', id: 's-me' }, 'Чужая фича');
    const c = await renderCard();
    const btn = findNodeByAttr(c, 'data-testid', 'feature-card-to-feature');
    expect(textOf(btn!), 'подпись почужала').toBe('К фиче ›');
    const log = eventLog();
    click(btn);
    expect(getFeatureMainTab()).toBe('progress');
    expect(log[0].detail).toMatchObject({ featureId: 'f1', fromChat: true });
  });

  it('конкурентность: указатель записан на живом компоненте — подпись переключается без перемонтирования', async () => {
    seedIndex(feat({ ...indexProgress }));
    const c = await renderCard();
    expect(textOf(findNodeByAttr(c, 'data-testid', 'feature-card-to-feature')!)).toBe('К фиче ›');
    setFeatureMainTab('review');
    act(() => { backNav.recordFeatureReturn('f1', { kind: 'chat', id: 's-me' }, 'Правда о прогрессе'); });
    const btn = findNodeByAttr(c, 'data-testid', 'feature-card-to-feature');
    expect(textOf(btn!), 'запись указателя не перекрасила живую карточку').toContain('‹ К фиче · Разбор');
  });
});

describe('FeatureChatCard: действие тревоги и приход run (c4)', () => {
  it('ждут В ЭТОМ чате: «Ответить здесь» → onFocusComposer ровно один раз, openChatById ноль раз', async () => {
    seedIndex(feat({
      ...indexProgress, state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'нужен ответ', personaId: 'p-kira', sessionId: 's-me', taskId: 't-1' },
    }));
    const c = await renderCard();
    const btn = findNodeByAttr(c, 'data-testid', 'feature-card-action');
    expect(btn, 'кнопки действия нет').not.toBeNull();
    expect(textOf(btn!)).toBe('Ответить здесь');
    const log = eventLog();
    click(btn);
    expect(focusCount, 'фокус поля').toBe(1);
    expect(openChatCalls.length, 'переход вместо фокуса').toBe(0);
    expect(log.length, 'лишнего действия нет').toBe(0);
  });

  it('run=null → run: первый рендер — счёт индекса без срока; после прихода run — счёт и срок из run (тот же корень, без перемонтирования)', async () => {
    seedIndex(feat({ ...indexProgress, nowPersonaIds: ['p-kira'] }));
    const c = await renderCard();
    const count = () => textOf(findNodeByAttr(c, 'data-testid', 'feature-card-count')!);
    expect(count(), 'счёт индекса').toContain('5 из 30');
    expect(count(), 'срок выдуман до прихода run').not.toContain('≈');
    // Run приходит позже — через стор (тот же путь, что в продукте: refetch после task_changed).
    data.runs = [run({
      stepProgress: sp(6, 44),
      epic: { id: 'f1', title: 'Фича', status: 'todo', note: { text: 'голос', at: '2026-11-10T10:00:00Z', byUser: false, stageAtWrite: 'prod', stateAtWrite: 'running', estimate: { etaMinutes: 160 } } },
    })];
    await act(async () => { refetchFeatureRuns('s-me'); await new Promise((r) => setTimeout(r, 0)); });
    expect(count(), 'числа run не победили').toContain('6 из 44');
    expect(count(), 'срока из run нет').toContain('≈');
    expect(findNodeByAttr(c, 'data-testid', 'feature-chat-card'), 'карточка перемонтирована').not.toBeNull();
  });
});

describe('FeatureChatCard: несколько фич у чата', () => {
  it('карточка — первой фичи из featureOf; «ещё N» перед «Ключевой чат» перебирает фичи', async () => {
    const f1 = feat({ featureId: 'f1', title: 'Первая фича', ...indexProgress });
    const f2 = feat({ featureId: 'f2', title: 'Вторая фича' });
    const idx = {
      features: [f1, f2],
      chats: { 's-me': [
        { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш8', lane: 'front' },
        { featureId: 'f2', kind: 'executor', taskRole: null, stepRef: null, lane: null },
      ] },
    } as FeaturesIndex;
    indexStore.reset(); indexStore.setIndex('p1', idx); data.index = idx;
    const c = await renderCard();
    expect(textOf(findNodeByAttr(c, 'data-testid', 'feature-card-title')!)).toBe('Первая фича');
    const cycle = findNodeByAttr(c, 'data-testid', 'feature-card-cycle');
    expect(cycle, 'кнопки «ещё N» нет').not.toBeNull();
    expect(textOf(cycle!)).toBe('ещё 1');
    click(cycle);
    expect(textOf(findNodeByAttr(c, 'data-testid', 'feature-card-title')!), 'цикл не показал вторую фичу').toBe('Вторая фича');
  });

  it('смена чата на живом компоненте: выбор сбрасывается — карточка нового чата показывает ЕГО первую фичу', async () => {
    // s1 и s2 — по две фичи у каждого: без сброса selIdx после «ещё 1» в s1 карточка s2
    // показала бы вторую фичу s2. Тот же корень, что в воркспейсе (ChatPanel не перемонтируется).
    const f1 = feat({ featureId: 'f1', title: 'Первая фича s1', ...indexProgress });
    const f2 = feat({ featureId: 'f2', title: 'Вторая фича s1' });
    const f3 = feat({ featureId: 'f3', title: 'Первая фича s2', ...indexProgress });
    const f4 = feat({ featureId: 'f4', title: 'Вторая фича s2' });
    const idx = {
      features: [f1, f2, f3, f4],
      chats: {
        's1': [
          { featureId: 'f1', kind: 'executor', taskRole: 'step', stepRef: 'Ш8', lane: 'front' },
          { featureId: 'f2', kind: 'executor', taskRole: null, stepRef: null, lane: null },
        ],
        's2': [
          { featureId: 'f3', kind: 'executor', taskRole: 'step', stepRef: 'Ш1', lane: 'front' },
          { featureId: 'f4', kind: 'executor', taskRole: null, stepRef: null, lane: null },
        ],
      },
    } as FeaturesIndex;
    indexStore.reset(); indexStore.setIndex('p1', idx); data.index = idx;
    const { root, container } = mount(createElement(FeatureChatCard, {
      sessionId: 's1', projectId: 'p1', isMobile: false,
      onFocusComposer: () => { focusCount++; },
    }));
    roots.push(root);
    await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
    const title = () => textOf(findNodeByAttr(container, 'data-testid', 'feature-card-title')!);
    expect(title()).toBe('Первая фича s1');
    click(findNodeByAttr(container, 'data-testid', 'feature-card-cycle'));
    expect(title(), 'цикл внутри s1 не показал вторую фичу').toBe('Вторая фича s1');
    // Воркспейс переключил чат: ТОТ ЖЕ корень, sessionId → s2.
    act(() => { root.render(createElement(FeatureChatCard, { sessionId: 's2', projectId: 'p1', isMobile: false, onFocusComposer: () => { focusCount++; } })); });
    await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
    expect(findNodeByAttr(container, 'data-testid', 'feature-chat-card'), 'карточка перемонтирована').not.toBeNull();
    expect(title(), 'после смены чата карточка показывает не первую фичу').toBe('Первая фича s2');
  });
});

describe('FeatureChatCard: мобила 360', () => {
  it('тега статуса нет; все кнопки minHeight 40 (цель на тач)', async () => {
    seedIndex(feat({
      ...indexProgress, state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'про отступы', personaId: 'p-kira', sessionId: 's-alex', taskId: 't-1' },
      keySessionId: 's-key',
    }));
    const c = await renderCard({ isMobile: true });
    expect(findNodeByAttr(c, 'data-testid', 'feature-card-tag'), 'тег на мобиле').toBeNull();
    const btns = buttonNodes(c);
    expect(btns.length, 'на мобиле должны быть действие, «К фиче» и «Ключевой»').toBe(3);
    for (const b of btns) {
      expect((b as { style: Record<string, string> }).style.minHeight, `кнопка «${textOf(b)}» ниже 40`).toBe('40px');
    }
  });
});

describe('FeatureChatCard: закрытая фича', () => {
  it('полосы трекера и действия нет (showTracker=false)', async () => {
    seedIndex(feat({
      featureId: 'f1', title: 'Смержена', projectId: 'p1', bucket: 'closed', state: 'merged',
      attention: null, rail: [], chips: ['closed'], stepsAccepted: 12, stepsTotal: 12, loops: 0,
      now0: null, lastActivityAt: null, summary: { mergedAt: '2026-10-19T12:00:00Z', durationMinutes: 913 },
      keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
    }));
    // Итог («12 шагов · 15 ч 13 мин», «Смержена …») — из run.summary; без run модель
    // честно деградирует до корзин индекса (это кейс featureChatCardModel.test).
    data.runs = [run({ state: 'merged', summary: { mergedAt: '2026-10-19T12:00:00Z', durationMinutes: 913, tasks: 14, steps: 12, loops: 1 } })];
    const c = await renderCard();
    expect(findNodeByAttr(c, 'data-testid', 'feature-card-bar'), 'полоса у закрытой').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-card-action'), 'действия у закрытой').toBeNull();
    expect(textOf(findNodeByAttr(c, 'data-testid', 'feature-card-count')!)).toContain('12 шагов');
    expect(textOf(findNodeByAttr(c, 'data-testid', 'feature-card-phrase')!)).toContain('Смержена');
  });
});

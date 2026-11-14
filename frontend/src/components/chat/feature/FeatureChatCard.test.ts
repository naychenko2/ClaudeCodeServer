// FP-C3: контейнер FeatureChatCard — фича как часть шапки чата (вариант А макета
// chat-feature-fit-v1). Окружение — настоящий компонент на fakeDom с живыми сторами
// (featuresIndex/featureRuns/featureBackNav/featurePage), как раньше: «run приходит позже
// индекса», «указатель меняется на живом компоненте» и «смена чата при открытом поповере»
// достижимы только так. Render-prop children(parts) здесь рисует три части подряд (в
// продукте их раскладывает ChatHeaderBar): строка, кромка, кнопка тревоги.
//
// Что защищается: строка → поповер по клику (aria-expanded живёт ТОЛЬКО у строки);
// «Ключевой чат» — в поповере, в ключевом чате вместо него бейдж; «К фиче» с указателем ведёт
// на записанную вкладку (returnToFeature), без указателя — на «Ход»; действие тревоги в своём
// чате — фокус поля, не переход; конкурентность: приход run дорисовывает срок и трекер, строка
// не перемонтируется; смена чата закрывает поповер и сбрасывает «ещё N»; клик мимо поповера и
// Esc закрывают его; 360 — шторка, кнопки 40, на 360 кнопки тревоги в шапке нет.
//
// Мутации (в отчёте): см. итог задачи FP-C3.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FeatureChatCard, type FeatureHeaderParts } from './FeatureChatCard';
import { Fragment } from 'react';
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

// Раскладка частей без шапки: строка, кромка, тревога подряд (в продукте — ChatHeaderBar).
const layout = (parts: FeatureHeaderParts | null) =>
  parts ? createElement(Fragment, null, parts.subtitle, parts.edge, parts.alarm) : null;

async function renderCard(over: { isMobile?: boolean; sessionId?: string } = {}) {
  const { root, container } = mount(createElement(FeatureChatCard, {
    sessionId: over.sessionId ?? 's-me', projectId: 'p1', isMobile: over.isMobile ?? false,
    onFocusComposer: () => { focusCount++; }, children: layout,
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
  act(() => { (reactProps(node!)!.onClick as (e?: unknown) => void)({ stopPropagation: () => {} }); });
}

type N = NonNullable<ReturnType<typeof findNodeByAttr>>;
function walk(n: N, fn: (x: N) => void): void {
  fn(n);
  for (const c of (n as unknown as { childNodes: N[] }).childNodes) walk(c, fn);
}
const doc = () => (globalThis as unknown as { document: { body: N; listeners?: Record<string, Set<(e: unknown) => void>> } }).document;
// Поповер (Menu) и шторка (Modal) живут порталом в body, строка/кромка/тревога — в контейнере.
const inBody = (testId: string) => findNodeByAttr(doc().body, 'data-testid', testId);
const textOf = (n: N): string => textNodes(n).join('');
function countAriaExpanded(c: N): number {
  let k = 0;
  walk(c, (x) => { if (x.nodeType === 1 && x.getAttribute('aria-expanded') != null) k++; });
  return k;
}
function pressEscape(): void {
  const handlers = [...(doc().listeners?.keydown ?? new Set<(e: unknown) => void>())];
  act(() => { for (const h of handlers) h({ key: 'Escape', preventDefault: () => {}, defaultPrevented: false }); });
}
// Подложка Menu: fixed-слой на весь экран без детей с onClick (клик мимо поповера).
function menuBackdrop(): N | null {
  let found: N | null = null;
  walk(doc().body, (x) => {
    if (found || x.nodeType !== 1) return;
    const st = (x as unknown as { style: Record<string, string> }).style;
    if (st.position === 'fixed' && st.inset === '0' && reactProps(x)?.onClick) found = x;
  });
  return found;
}

const subtitleOf = (c: N) => findNodeByAttr(c, 'data-testid', 'feature-chat-subtitle')!;
const openPopover = (c: N) => { click(subtitleOf(c)); };

beforeEach(() => {
  indexStore.reset(); runsStore.reset(); backNav.reset(); pageStore.reset();
  data.index = { features: [], chats: {} }; data.runs = [];
  openChatCalls.length = 0; focusCount = 0;
  installFakeDom();
});
afterEach(() => {
  for (const r of roots) act(() => { r.unmount(); });
  roots.length = 0;
  vi.unstubAllGlobals();
  uninstallFakeDom();
});

describe('FeatureChatCard: строка, поповер и «Ключевой чат»', () => {
  it('строка — единственный раскрываемый элемент; клик открывает поповер (aria-expanded=true), «Ключевой чат» один и ведёт openChatById(keySessionId) ровно один раз', async () => {
    seedIndex(feat({ ...indexProgress, keySessionId: 's-key', nowPersonaIds: ['p-kira'] }));
    const c = await renderCard();
    // Отдельной полосы с карточкой больше нет: ни старой карточки, ни двух строк.
    expect(findNodeByAttr(c, 'data-testid', 'feature-chat-card'), 'старая карточка-полоса').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-card-row1'), 'старая строка 1').toBeNull();
    expect(countAriaExpanded(c), 'раскрываемых элементов должно быть ровно одно — строка').toBe(1);
    expect(subtitleOf(c).getAttribute('aria-expanded')).toBe('false');
    expect(inBody('feature-popover'), 'поповер открыт без клика').toBeNull();
    openPopover(c);
    expect(subtitleOf(c).getAttribute('aria-expanded'), 'строка не отметила «открыт»').toBe('true');
    expect(inBody('feature-popover'), 'поповер не открылся').not.toBeNull();
    const key = inBody('feature-popover-to-key');
    expect(key, 'кнопки «Ключевой чат» нет').not.toBeNull();
    expect(textOf(key!)).toBe('Ключевой чат');
    const log = eventLog();
    click(key);
    expect(openChatCalls, 'openChatById зван не один раз/не туда').toEqual(['s-key']);
    expect(log.length, 'один клик — ровно одно действие').toBe(0);
    expect(inBody('feature-popover'), 'после перехода поповер остался висеть').toBeNull();
  });

  it('ключевой чат: кнопки нет, в поповере бейдж «это ключевой чат»', async () => {
    seedIndex(feat({ ...indexProgress, keySessionId: 's-key' }), 's-key');
    const c = await renderCard({ sessionId: 's-key' });
    openPopover(c);
    expect(inBody('feature-popover-to-key'), 'кнопка «Ключевой чат» в ключевом чате').toBeNull();
    const badge = inBody('feature-popover-key-badge');
    expect(badge, 'бейджа ключевого чата нет').not.toBeNull();
    expect(textOf(badge!)).toBe('это ключевой чат');
    expect(badge!.nodeName).not.toBe('BUTTON');
  });
});

describe('FeatureChatCard: «К фиче»', () => {
  // Дефект 762b7c03 (после C3d): первая вкладка фичи с маршрутом называется «Процесс»,
  // и кнопка «К фиче» обязана называть её так же, как страница. Имя берётся из записи
  // возврата (FeatureBackNavInfo.tabLabel), а не восстанавливается из значения вкладки.
  // Мутация (в отчёте): (20) вернуть backTabName на словарь FEATURE_MAIN_TAB_LABEL →
  // «· Процесс» краснеет (тест «Материалы» выше остаётся зелёным — это и есть проверка
  // мутацией: словарь даёт те же имена для всех случаев, кроме маршрута).
  it('фича с маршрутом, ушли с «Процесса»: подпись «‹ К фиче · Процесс» (дефект 762b7c03)', async () => {
    seedIndex(feat({ ...indexProgress }));
    // Открытая страница 'f1' с подтверждённым маршрутом — признак (featureHasRoute) читает её run.
    pageStore.setFid('f1');
    pageStore.setState({
      status: 'ok',
      page: {
        run: run({ epic: { id: 'f1', title: 'Фича', status: 'todo', workflow: { templateId: 't', scope: 'global', stages: [], skipped: [], confirmedVersion: 1 } } }),
        events: [], eventsTotal: 0,
      },
    });
    setFeatureMainTab('progress');
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 's-me' }, 'Правда о прогрессе');
    const c = await renderCard();
    openPopover(c);
    const btn = inBody('feature-popover-to-feature');
    expect(btn, 'кнопки «К фиче» нет').not.toBeNull();
    expect(textOf(btn!), 'кнопка назвала вкладку по словарю, а не по странице').toContain('· Процесс');
  });

  it('с указателем на эту фичу (tab=materials): подпись «‹ К фиче · Материалы», клик — returnToFeature (на записанную вкладку, fromChat:false)', async () => {
    seedIndex(feat({ ...indexProgress }));
    setFeatureMainTab('materials');
    backNav.recordFeatureReturn('f1', { kind: 'chat', id: 's-me' }, 'Правда о прогрессе');
    const c = await renderCard();
    openPopover(c);
    const btn = inBody('feature-popover-to-feature');
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
    setFeatureMainTab('review');
    const c = await renderCard();
    openPopover(c);
    const btn = inBody('feature-popover-to-feature');
    expect(textOf(btn!), 'без указателя нет подписи «К фиче ›»').toBe('К фиче ›');
    const log = eventLog();
    click(btn);
    expect(getFeatureMainTab(), 'вкладка «Ход» не выставлена').toBe('progress');
    expect(log.length, 'один клик — ровно одно событие').toBe(1);
    expect(log[0].type).toBe('cc-open-feature');
    expect(log[0].detail).toMatchObject({ featureId: 'f1', fromChatId: 's-me', fromChat: true });
  });

  it('указатель на другую фичу — «К фиче» ведёт по-своему (returnToFeature чужого указателя не зовёт)', async () => {
    seedIndex(feat({ ...indexProgress }));
    backNav.recordFeatureReturn('f2', { kind: 'chat', id: 's-me' }, 'Чужая фича');
    const c = await renderCard();
    openPopover(c);
    const btn = inBody('feature-popover-to-feature');
    expect(textOf(btn!), 'подпись почужала').toBe('К фиче ›');
    const log = eventLog();
    click(btn);
    expect(getFeatureMainTab()).toBe('progress');
    expect(log[0].detail).toMatchObject({ featureId: 'f1', fromChat: true });
  });

  it('конкурентность: указатель записан на живом компоненте при открытом поповере — подпись переключается без перемонтирования', async () => {
    seedIndex(feat({ ...indexProgress }));
    const c = await renderCard();
    openPopover(c);
    expect(textOf(inBody('feature-popover-to-feature')!)).toBe('К фиче ›');
    setFeatureMainTab('review');
    act(() => { backNav.recordFeatureReturn('f1', { kind: 'chat', id: 's-me' }, 'Правда о прогрессе'); });
    expect(textOf(inBody('feature-popover-to-feature')!), 'запись указателя не перекрасила живой поповер').toContain('‹ К фиче · Разбор');
  });
});

describe('FeatureChatCard: действие тревоги и приход run', () => {
  it('ждут В ЭТОМ чате: кнопка тревоги в шапке (1280) и «Ответить здесь» в поповере → onFocusComposer по разу, openChatById ноль раз', async () => {
    seedIndex(feat({
      ...indexProgress, state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'нужен ответ', personaId: 'p-kira', sessionId: 's-me', taskId: 't-1' },
    }));
    const c = await renderCard();
    const btn = findNodeByAttr(c, 'data-testid', 'feature-card-action');
    expect(btn, 'кнопки тревоги в шапке нет').not.toBeNull();
    expect(textOf(btn!)).toBe('Ответить здесь');
    const log = eventLog();
    click(btn);
    expect(focusCount, 'фокус поля').toBe(1);
    expect(openChatCalls.length, 'переход вместо фокуса').toBe(0);
    openPopover(c);
    click(inBody('feature-popover-action'));
    expect(focusCount, 'действие поповера — тот же маршрут').toBe(2);
    expect(openChatCalls.length).toBe(0);
    expect(log.length, 'лишнего действия нет').toBe(0);
  });

  it('тревога в ДРУГОМ чате: кнопка шапки открывает чат тревоги, не фокусирует поле', async () => {
    seedIndex(feat({
      ...indexProgress, state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'нужен ответ', personaId: 'p-kira', sessionId: 's-alex', taskId: 't-1' },
    }));
    const c = await renderCard();
    click(findNodeByAttr(c, 'data-testid', 'feature-card-action'));
    expect(openChatCalls).toEqual(['s-alex']);
    expect(focusCount).toBe(0);
  });

  it('run=null → run: строка и кромка по индексу без срока; после прихода run — счёт и срок из run, тот же узел строки (без перемонтирования)', async () => {
    seedIndex(feat({ ...indexProgress, nowPersonaIds: ['p-kira'] }));
    const c = await renderCard();
    const count = () => textOf(findNodeByAttr(c, 'data-testid', 'feature-subtitle-count')!);
    const row = subtitleOf(c);
    expect(count(), 'счёт индекса').toContain('5 из 30');
    expect(count(), 'срок выдуман до прихода run').not.toContain('≈');
    expect(findNodeByAttr(c, 'data-testid', 'feature-card-bar'), 'кромки по индексу нет').not.toBeNull();
    data.runs = [run({
      stepProgress: sp(6, 44),
      epic: { id: 'f1', title: 'Фича', status: 'todo', note: { text: 'голос', at: '2026-11-10T10:00:00Z', byUser: false, stageAtWrite: 'prod', stateAtWrite: 'running', estimate: { etaMinutes: 160 } } },
    })];
    await act(async () => { refetchFeatureRuns('s-me'); await new Promise((r) => setTimeout(r, 0)); });
    expect(count(), 'числа run не победили').toContain('6 из 44');
    expect(count(), 'срока из run нет').toContain('≈');
    expect(subtitleOf(c), 'строка перемонтирована').toBe(row);
  });

  it('кромка — трекер 2 px без зазора и скругления', async () => {
    seedIndex(feat({ ...indexProgress }));
    const c = await renderCard();
    const st = (findNodeByAttr(c, 'data-testid', 'feature-card-bar') as unknown as { style: Record<string, string> }).style;
    expect(st.height).toBe('2px');
    expect(st.gap, 'зазор между сегментами').toBe('0');
    expect(st.borderRadius, 'скругление кромки').toBe('0');
  });
});

describe('FeatureChatCard: подпись тревоги в поповере', () => {
  it('ask на десктопе: подпись — отдельный элемент до текста вопроса', async () => {
    seedIndex(feat({
      ...indexProgress, state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'нужен ответ', personaId: 'p-kira', sessionId: 's-alex', taskId: 't-1' },
      ask: { text: 'нужно решение по отступам', source: 'wait', personaId: 'p-kira', sessionId: 's-alex' },
    }));
    const cWait = await renderCard();
    openPopover(cWait);
    expect(textOf(inBody('feature-popover-phrase-label')!), 'подпись не слова модели').toBe('Нужно от тебя:');
    expect(textOf(inBody('feature-popover-phrase')!)).toBe('Нужно от тебя: нужно решение по отступам');

  });

  it('без ask (идущая фича) подписи нет', async () => {
    seedIndex(feat({ ...indexProgress, nowPersonaIds: ['p-kira'] }));
    const cRun = await renderCard();
    openPopover(cRun);
    expect(inBody('feature-popover'), 'поповер не открылся').not.toBeNull();
    expect(inBody('feature-popover-phrase-label'), 'подпись без ask').toBeNull();
  });
});

describe('FeatureChatCard: несколько фич у чата', () => {
  function seedTwo() {
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
  }

  it('строка — первой фичи; «ещё N» последней кнопкой поповера перебирает фичи, поповер остаётся открытым', async () => {
    seedTwo();
    const c = await renderCard();
    const name = () => textOf(findNodeByAttr(c, 'data-testid', 'feature-subtitle-name')!);
    expect(name()).toBe('Первая фича');
    openPopover(c);
    const cycle = inBody('feature-popover-cycle');
    expect(cycle, 'кнопки «ещё N» нет').not.toBeNull();
    expect(textOf(cycle!)).toBe('ещё 1');
    click(cycle);
    expect(name(), 'цикл не показал вторую фичу').toBe('Вторая фича');
    expect(inBody('feature-popover'), 'цикл закрыл поповер').not.toBeNull();
  });

  it('смена чата на живом компоненте при ОТКРЫТОМ поповере: поповер закрыт, выбор сброшен — строка нового чата показывает ЕГО первую фичу', async () => {
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
    const props = (sessionId: string) => ({ sessionId, projectId: 'p1', isMobile: false, onFocusComposer: () => { focusCount++; }, children: layout });
    const { root, container } = mount(createElement(FeatureChatCard, props('s1')));
    roots.push(root);
    await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
    const name = () => textOf(findNodeByAttr(container, 'data-testid', 'feature-subtitle-name')!);
    expect(name()).toBe('Первая фича s1');
    openPopover(container);
    click(inBody('feature-popover-cycle'));
    expect(name(), 'цикл внутри s1 не показал вторую фичу').toBe('Вторая фича s1');
    expect(inBody('feature-popover'), 'поповер должен быть открыт перед сменой чата').not.toBeNull();
    // Воркспейс переключил чат: ТОТ ЖЕ корень, sessionId → s2.
    // Кадр смены чата до эффектов: children зовётся на каждый рендер, и в слоте строки не
    // должно быть поповера уже в первом же рендере s2 (иначе кадр с чужим поповером).
    const frames: boolean[] = [];
    const spy = (parts: FeatureHeaderParts | null) => {
      const kids = (parts?.subtitle as { props: { children: unknown[] } } | undefined)?.props.children;
      frames.push(Boolean(kids?.[1]));
      return layout(parts);
    };
    act(() => { root.render(createElement(FeatureChatCard, { ...props('s2'), children: spy })); });
    await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
    expect(frames.length, 'после смены чата не было ни одного рендера').toBeGreaterThan(0);
    expect(frames.some(Boolean), 'в кадре смены чата поповер s1 был смонтирован (открытие без привязки к sessionId)').toBe(false);
    expect(inBody('feature-popover'), 'поповер чата s1 остался висеть в чате s2').toBeNull();
    expect(subtitleOf(container).getAttribute('aria-expanded')).toBe('false');
    expect(name(), 'после смены чата показана не первая фича').toBe('Первая фича s2');
  });
});

describe('FeatureChatCard: закрытие поповера', () => {
  it('клик мимо (по подложке Menu) и Escape закрывают поповер', async () => {
    // Возврат фокуса у Menu проверяет `el instanceof HTMLElement` — в node глобала нет.
    vi.stubGlobal('HTMLElement', class {});
    seedIndex(feat({ ...indexProgress }));
    const c = await renderCard();
    openPopover(c);
    const bd = menuBackdrop();
    expect(bd, 'подложки Menu нет — клик мимо нечем ловить').not.toBeNull();
    click(bd);
    expect(inBody('feature-popover'), 'клик мимо не закрыл поповер').toBeNull();
    expect(subtitleOf(c).getAttribute('aria-expanded')).toBe('false');
    openPopover(c);
    expect(inBody('feature-popover')).not.toBeNull();
    pressEscape();
    expect(inBody('feature-popover'), 'Escape не закрыл поповер').toBeNull();
  });
});

describe('FeatureChatCard: щит всплытия слота строки', () => {
  const slotOf = (c: N) => findNodeByAttr(c, 'data-testid', 'feature-chat-subtitle-slot')!;
  const fire = (node: N, name: 'onClick' | 'onKeyDown', key?: string): boolean => {
    let stopped = false;
    (reactProps(node)![name] as (e: unknown) => void)({ key, stopPropagation: () => { stopped = true; } });
    return stopped;
  };

  for (const isMobile of [false, true]) {
    it(`${isMobile ? '360' : '1280'}: клик и Enter/Space гасятся (не доходят до блока заголовка), Escape и Tab — нет`, async () => {
      seedIndex(feat({ ...indexProgress }));
      const c = await renderCard({ isMobile });
      const slot = slotOf(c);
      expect(fire(slot, 'onClick'), 'клик всплывает к ссылке на персону / переключателю чатов').toBe(true);
      expect(fire(slot, 'onKeyDown', 'Enter'), 'Enter всплывает к блоку заголовка').toBe(true);
      expect(fire(slot, 'onKeyDown', ' '), 'Space всплывает к блоку заголовка').toBe(true);
      expect(fire(slot, 'onKeyDown', 'Escape'), 'Escape погашен — Modal/Menu не увидят его на document').toBe(false);
      expect(fire(slot, 'onKeyDown', 'Tab'), 'Tab погашен — обход фокуса').toBe(false);
    });
  }
});

describe('FeatureChatCard: мобила 360', () => {
  it('зона цели строки 40 целиком внутри тулбара: в потоке 16, вниз не дальше запаса до низа тулбара', async () => {
    seedIndex(feat({ ...indexProgress }));
    const c = await renderCard({ isMobile: true });
    const st = (subtitleOf(c) as unknown as { style: Record<string, string> }).style;
    const slotH = parseFloat((findNodeByAttr(c, 'data-testid', 'feature-chat-subtitle-slot') as unknown as { style: Record<string, string> }).style.height);
    const padTop = parseFloat(st.padding.split(' ')[0]);
    const padBottom = parseFloat(st.padding.split(' ')[2]);
    // Слот держит 16, зона = padding + строка; margin отрицательный и равен padding — раскладка не растёт.
    expect(slotH).toBe(16);
    expect(padTop + slotH + padBottom, 'зона ниже 40').toBeGreaterThanOrEqual(40);
    expect(st.margin, 'margin не компенсирует padding — тулбар раздует').toBe(`-${padTop}px 0 -${padBottom}px`);
    // Высота тулбара 56, блок заголовка ≈ 38 по центру: под подписью до низа тулбара (56 − 38) / 2 = 9 px.
    expect(padBottom, 'зона уходит ниже тулбара — под кромку и ленту').toBeLessThanOrEqual(9);
  });


  it('кнопки тревоги в шапке нет; строка — цель 40; шторка: действие, «К фиче», «Ключевой» на всю ширину, minHeight 40', async () => {
    seedIndex(feat({
      ...indexProgress, state: 'waiting',
      attention: { kind: 'wait', since: null, reason: 'про отступы', personaId: 'p-kira', sessionId: 's-alex', taskId: 't-1' },
      keySessionId: 's-key',
    }));
    const c = await renderCard({ isMobile: true });
    expect(findNodeByAttr(c, 'data-testid', 'feature-card-action'), 'кнопка тревоги на 360').toBeNull();
    expect((subtitleOf(c) as unknown as { style: Record<string, string> }).style.minHeight, 'цель строки ниже 40').toBe('40px');
    openPopover(c);
    for (const id of ['feature-popover-action', 'feature-popover-to-feature', 'feature-popover-to-key']) {
      const b = inBody(id);
      expect(b, `в шторке нет «${id}»`).not.toBeNull();
      expect((b as unknown as { style: Record<string, string> }).style.minHeight, `«${id}» ниже 40`).toBe('40px');
    }
  });
});

describe('FeatureChatCard: закрытая фича', () => {
  it('кромки и кнопки тревоги нет; в строке слово «смержена»; в поповере нет трекера', async () => {
    seedIndex(feat({
      featureId: 'f1', title: 'Смержена', projectId: 'p1', bucket: 'closed', state: 'merged',
      attention: null, rail: [], chips: ['closed'], stepsAccepted: 12, stepsTotal: 12, loops: 0,
      now0: null, lastActivityAt: null, summary: { mergedAt: '2026-10-19T12:00:00Z', durationMinutes: 913 },
      keySessionId: null, keyArchived: false, keyProjectId: null, chatCount: 0,
    }));
    data.runs = [run({ state: 'merged', summary: { mergedAt: '2026-10-19T12:00:00Z', durationMinutes: 913, tasks: 14, steps: 12, loops: 1 } })];
    const c = await renderCard();
    expect(findNodeByAttr(c, 'data-testid', 'feature-card-bar'), 'кромка у закрытой').toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-card-action'), 'тревога у закрытой').toBeNull();
    expect(textOf(findNodeByAttr(c, 'data-testid', 'feature-subtitle-count')!)).toContain('12 шагов');
    expect(textOf(findNodeByAttr(c, 'data-testid', 'feature-subtitle-word')!)).toContain('смержена');
    openPopover(c);
    expect(inBody('feature-popover-bar'), 'трекер в поповере закрытой').toBeNull();
  });
});

describe('FeatureChatCard: чат без фичи', () => {
  it('children зовётся с null: ничего не рисуется, дерево то же', async () => {
    indexStore.reset(); indexStore.setIndex('p1', { features: [], chats: {} } as FeaturesIndex);
    const c = await renderCard();
    expect(findNodeByAttr(c, 'data-testid', 'feature-chat-subtitle')).toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'feature-card-bar')).toBeNull();
  });
});

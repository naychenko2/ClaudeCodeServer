// Ари-лейбл кольцевой пилюли: строка «Локальная модель: <хвост/лицо>» под флагом
// local-model-stats и только для локального чата (m1, ревью Глеба). Статический рендер —
// как соседние тесты шапки (ToolUseView.test, sphereGate.test).
// Вторая часть — циферблат кэша промпта (F-1b): появление по данным ленты, текст на
// десктопе и его отсутствие на мобиле, «идёт ход» без минут.
import { act, createElement } from 'react';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterEach, beforeAll, afterAll, describe, expect, it, vi } from 'vitest';
// Перенос (F-4): запрос handoff идёт через offline.request — подменяем его до импорта
// api (сети в тестах нет); переход в новый чат — через openChatById, его тоже подменяем,
// чтобы не тянуть навигацию в тест шапки
const handoff = vi.hoisted(() => ({ fn: () => Promise.resolve({}) }));
const openedChats = vi.hoisted(() => [] as string[]);
// Управляемый ответ openChatById и собраные тосты (F-4r): провал перехода — отдельный тест
const openOk = vi.hoisted(() => ({ value: true }));
const toasts = vi.hoisted(() => [] as string[]);
vi.mock('../../../lib/offline', () => ({
  __esModule: true,
  request: (url: string, opts?: { method?: string; timeoutMs?: number }) => handoff.fn(url, opts),
  readStoredToken: () => 'tok',
  cachedToken: () => 'tok',
  invalidateTokenCache: () => {},
}));
vi.mock('../../../lib/openChat', () => ({
  __esModule: true,
  openChatById: (id: string) => { openedChats.push(id); return Promise.resolve(openOk.value); },
}));
vi.mock('../../../lib/toast', () => ({
  __esModule: true,
  showToast: (title: string, body?: string) => { toasts.push(`${title} · ${body ?? ''}`); },
}));
// Хаб в тесте не поднимаем: useLocalModelLive с sessionId подписывается на signalr —
// в node-окружении '/hubs/session' не разрешится
vi.mock('../../../lib/signalr', () => ({
  __esModule: true,
  onLocalModelMessage: () => () => {},
}));
import { installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } from './fakeDom';
import { CacheDialPip } from '../../ui/CacheDial';
import type { PromptCacheState } from '../../../lib/promptCache';
import { setAllFlags } from '../../../lib/featureFlags';
import { RingPillBadge, type CostStats, type FalCostStats, type ProviderBalance } from '../ChatHeaderBar';
import type { GlifGenStats } from '../glifStats';
import type { ClaudeBilling, ChatItem } from '../../../types';
import type { ContextEstimate } from '../../../lib/context';
import type { RateWindow } from '../../../lib/rateLimit';
import type { LocalModelLiveState } from '../../../features/localModel/useLocalModelLive';
import type { PromptCacheWire } from '../../../lib/promptCache';

const cost: CostStats = { cost: 0, input: 0, output: 0, cacheRead: 0, cacheCreate: 0, turns: 0, results: 0 };
const falCost: FalCostStats = { total: 0, count: 0, byModel: new Map() };
const glifCost: GlifGenStats = { count: 0, credits: 0, hasCredits: false, byType: new Map() };
// pct задан, чтобы пилюля рендерилась (строка контекста) даже когда строки
// «Локальная модель» нет — иначе негативные тесты проверяли бы тривиально (пилюля скрыта)
const estimate: ContextEstimate = { window: 200000, pct: 42, fresh: false, level: 'normal' };
const balance: ProviderBalance | null = null;
const billing: ClaudeBilling = 'subscription';
const windows: RateWindow[] = [];

// Генерация со скоростью: десктоп — «38 ток/с», мобила — «38 т/с»
const liveGenerating: LocalModelLiveState = { seen: true, phase: 'generating', genTps: 38, waitingTotal: null, lastTurn: null, lastActivityAt: null };
// Idle без скорости последнего хода — хвоста/лица нет
const liveIdle: LocalModelLiveState = { seen: true, phase: 'idle', genTps: null, waitingTotal: null, lastTurn: null, lastActivityAt: null };

// aria-label пилюли (атрибут aria-label в разметке) или null, если пилюли нет
function renderAriaLabel(live: LocalModelLiveState | undefined, isMobile = false): string | null {
  const html = renderToStaticMarkup(createElement(RingPillBadge, {
    estimate, isWaiting: false, isCompacting: false, canCompact: false, onCompact: () => {},
    online: true, assistantName: 'Тест',
    isCliProvider: false, providerName: 'Claude', cost, falCost, glifCost, balance, billing, windows,
    isMobile, forceLocalModel: live,
  }));
  const m = /aria-label="([^"]*)"/.exec(html);
  return m ? m[1] : null;
}

// Есть ли в aria-label строка «Локальная модель: …» (null-лейбл — пилюли нет — строки тоже нет)
const hasLmLine = (label: string | null): boolean => label != null && label.includes('Локальная модель:');

afterEach(() => setAllFlags({}));

describe('RingPillBadge aria-label: локальная модель (m1)', () => {
  it('десктоп, генерация, флаг вкл — строка «Локальная модель: 38 ток/с»', () => {
    setAllFlags({ 'local-model-stats': true });
    const label = renderAriaLabel(liveGenerating);
    expect(label).toContain('Локальная модель: 38 ток/с');
  });

  it('мобила, генерация, контекст в норме — строка со словом-лицом «38 т/с»', () => {
    setAllFlags({ 'local-model-stats': true });
    const label = renderAriaLabel(liveGenerating, true);
    expect(label).toContain('Локальная модель: 38 т/с');
  });

  it('флаг выкл — строки нет (хотя чат локальный)', () => {
    setAllFlags({ 'local-model-stats': false });
    expect(hasLmLine(renderAriaLabel(liveGenerating))).toBe(false);
  });

  it('idle без скорости последнего хода — строки нет (активного состояния нет)', () => {
    setAllFlags({ 'local-model-stats': true });
    expect(hasLmLine(renderAriaLabel(liveIdle))).toBe(false);
  });

  it('idle с lastTurn: null — строки нет', () => {
    setAllFlags({ 'local-model-stats': true });
    expect(hasLmLine(renderAriaLabel(undefined))).toBe(false);
  });
});

// === Циферблат кэша (F-1b) ===
// Якорь привязан к «сейчас» теста: остаток 60 мин — тёплый, 5 мин — остывает, −1 мин — холодный.
const MIN = 60_000;
const cacheItem = (over: Partial<PromptCacheWire> = {}): ChatItem => ({
  kind: 'result', subtype: 'success', durationMs: 1000, numTurns: 1,
  promptCache: { readTokens: 184000, writtenTokens: 1000, anchorAtMs: Date.now(), ttl: '1h', missReason: 'none', ...over },
} as ChatItem);
const plainResult = (): ChatItem => ({ kind: 'result', subtype: 'success', durationMs: 1000, numTurns: 1 } as ChatItem);

function renderPill(items: ChatItem[], isMobile = false, isWaiting = false): string {
  return renderToStaticMarkup(createElement(RingPillBadge, {
    estimate, isWaiting, isCompacting: false, canCompact: false, onCompact: () => {},
    online: true, assistantName: 'Тест',
    isCliProvider: false, providerName: 'Claude', cost, falCost, glifCost, balance, billing, windows,
    isMobile, items,
  }));
}

const ariaOf = (html: string): string => /aria-label="([^"]*)"/.exec(html)?.[1] ?? '';
// Видимый текст минуты/«остыл» рядом с циферблатом (в атрибутах не ищем)
const visibleLeftText = (html: string): string | null => />(\d+ мин|остыл)</.exec(html)?.[1] ?? null;

describe('RingPillBadge: циферблат кэша промпта (F-1b)', () => {
  it('до первого result’а с данными кэша — ни циферблата, ни разделителя', () => {
    const html = renderPill([plainResult()]);
    expect(html).not.toContain('<svg width="14"');
    expect(html).not.toContain('width:1px;height:12px');
    expect(ariaOf(html)).not.toContain('Кэш');
  });

  it('тёплый: циферблат есть, текста с минутами нет', () => {
    const html = renderPill([cacheItem()]);
    expect(html).toContain('<svg width="14"');
    expect(visibleLeftText(html)).toBeNull();
    expect(ariaOf(html)).toContain('Кэш промпта');
  });

  it('остывает: минуты на десктопе, «дыхание» на секторе', () => {
    const html = renderPill([cacheItem({ anchorAtMs: Date.now() - 55 * MIN })]);
    expect(visibleLeftText(html)).toBe('5 мин');
    expect(ariaOf(html)).toContain('Кэш промпта');
    expect(html).toContain('cc-cache-breathe');
  });

  it('холодный: пунктирный контур и «остыл»', () => {
    const html = renderPill([cacheItem({ anchorAtMs: Date.now() - 61 * MIN })]);
    expect(html).toContain('stroke-dasharray="3 2.2"');
    expect(visibleLeftText(html)).toBe('остыл');
    expect(ariaOf(html)).toContain('Кэш промпта');
  });

  it('мобила: циферблат есть, текста у него нет ни в каком состоянии', () => {
    expect(visibleLeftText(renderPill([cacheItem({ anchorAtMs: Date.now() - 55 * MIN })], true))).toBeNull();
    expect(visibleLeftText(renderPill([cacheItem({ anchorAtMs: Date.now() - 61 * MIN })], true))).toBeNull();
    // сам циферблат остаётся
    expect(renderPill([cacheItem({ anchorAtMs: Date.now() - 55 * MIN })], true)).toContain('<svg width="14"');
  });

  it('aria-label и title не несут меняющегося со времени: ни статуса, ни минут в части про кэш (F-1b-r2)', () => {
    // Пилюля не тикает: любое «тёплый · 60 мин» в атрибутах через час противоречило бы циферблату.
    // Запрет — только части про кэш (строка кэша идёт последней в joined-лейбле): в других
    // строках aria-label минуты легальны — окна лимита и срок возврата (минор R3-F1b).
    const cachePart = (s: string): string => {
      const i = s.indexOf('Кэш');
      return i < 0 ? '' : s.slice(i);
    };
    for (const age of [0, 55 * MIN, 61 * MIN]) {
      const html = renderPill([cacheItem({ anchorAtMs: Date.now() - age })]);
      const a = ariaOf(html);
      expect(a).toContain('Кэш промпта');
      expect(cachePart(a)).not.toMatch(/тёплый|остынет|остыл|\d+ мин/);
      expect(cachePart(/title="([^"]*)"/.exec(html)?.[1] ?? '')).not.toMatch(/тёплый|остынет|остыл|\d+ мин/);
    }
  });

  it('идёт ход: показывается, но не как статус — без минут', () => {
    const html = renderPill([cacheItem()], false, true);
    expect(html).toContain('<svg width="14"');
    expect(visibleLeftText(html)).toBeNull();
    expect(ariaOf(html)).toContain('Кэш · идёт ход');
  });

  it('без items вообще — рендер не падает и циферблата нет (стабильный EMPTY_ITEMS)', () => {
    const html = renderToStaticMarkup(createElement(RingPillBadge, {
      estimate, isWaiting: false, isCompacting: false, canCompact: false, onCompact: () => {},
      online: true, assistantName: 'Тест',
      isCliProvider: false, providerName: 'Claude', cost, falCost, glifCost, balance, billing, windows,
    }));
    expect(html).not.toContain('<svg width="14"');
    expect(ariaOf(html)).not.toContain('Кэш');
  });
});

// Мини-циферблат строки подсказки (tip) — тот же компонент, что в подсказке пилюли (F-1b-r)
describe('CacheDialPip: мини-циферблат в подсказке', () => {
  const state = (over: Partial<PromptCacheState> = {}): PromptCacheState => ({
    status: 'warm', ttl: '1h', anchorAtMs: Date.now(), expiresAt: Date.now() + 60 * MIN,
    remainingMs: 60 * MIN, readTokens: 1000, writtenTokens: 500, ...over,
  });
  it('тёплый: пип 12 px с сектором на весь круг', () => {
    const html = renderToStaticMarkup(createElement(CacheDialPip, { state: state(), isWaiting: false }));
    expect(html).toContain('<svg width="12"');
    expect(html).toContain('r="7"');
    expect(html).not.toContain('stroke-dasharray');
  });
  it('остывающий: «дыхание» сектора в пипе', () => {
    // Якорь 55 мин назад — liveState сам считает остаток 5 мин и статус «остывает»
    const html = renderToStaticMarkup(createElement(CacheDialPip, {
      state: state({ anchorAtMs: Date.now() - 55 * MIN, expiresAt: Date.now() + 5 * MIN, remainingMs: 5 * MIN }),
      isWaiting: false,
    }));
    expect(html).toContain('cc-cache-breathe');
  });
  it('идёт ход: пип серый, без сектора', () => {
    const html = renderToStaticMarkup(createElement(CacheDialPip, { state: state(), isWaiting: true }));
    expect(html).not.toContain('fill-opacity');
  });
});

// Лента шапки передаётся как есть: литерал `props.items ?? []` даёт новую ссылку на каждый
// рендер, и заморозка ленты уходит в цикл «Too many re-renders». SSR цикл не воспроизводит,
// поэтому охраняем сам исходник (F-1b-r2).
describe('ChatHeaderBar: лента кэша без нового литерала на рендер', () => {
  it('нет `items ?? []` и копий ленты в компоненте пилюли', () => {
    const src = readFileSync(fileURLToPath(new URL('../ChatHeaderBar.tsx', import.meta.url)), 'utf8');
    // Guard — про пилюлю: лента замораживается внутри RingPillBadge. В ExtractTasksButton
    // свой `dialog.items` — список кандидатов, не лента (F-4r), поэтому берём исходник
    // компонента, а не весь файл.
    const pill = src.slice(
      src.indexOf('export function RingPillBadge('),
      src.indexOf('function RetitleButton('),
    );
    expect(pill.length > 0, 'исходник пилюли найден').toBe(true);
    expect(pill).not.toMatch(/items\s*\?\?\s*\[\]/);
    // .slice(…)/.concat(…) и Array.from(items…) — та же ловушка: новая ссылка на каждый
    // рендер, заморозка ленты уходит в цикл «Too many re-renders» (минор R3-F1b).
    // Расширение guard'а (минор ревью F-2): аргументы у slice не ограничены пустыми
    // скобками, плюс spread-копия [...items] и .filter/.map рядом с items. Lookbehind
    // `(?<![.\w])` не ловил целевую форму `props.items.map(` — дыра была ровно в ней
    // (F-4r): теперь допускается любой префикс, кроме `dialog.` — список задачи не лента.
    const feedGuard = /(?<!\bdialog\.)\bitems\s*\.(slice|concat|filter|map)\(/;
    expect(pill).not.toMatch(feedGuard);
    expect(pill).not.toMatch(/Array\.from\(\s*items\b/);
    expect(pill).not.toMatch(/(?<![.\w])\[\s*\.\.\.items\s*\]/);
    // Сам guard — на обе стороны (F-4r): целевые формы ловит, диалог — нет
    expect(feedGuard.test('const heads = props.items.map(h => h);'), 'props.items.map( — целевая форма').toBe(true);
    expect(feedGuard.test('items.slice(0, 3)'), 'bare items.slice( — тоже ловится').toBe(true);
    expect(feedGuard.test('dialog.items.filter(t => t.sel)'), 'dialog.items.filter( — не лента').toBe(false);
  });
});

// === Перенос в новый чат (F-4): вторая точка входа и кнопка ===
// Мутации:
//  • убрать строку предложения в parts (уровень danger) → первый тест краснеет;
//  • убрать `&& cache` в условии предложения → тест про гейт краснеет (F-4r);
//  • убрать вывод handoffError → тест про 409 краснеет;
//  • сделать тост успеха безусловным (до ok) → тест про openChatById=false краснеет (F-4r);
//  • убрать маппинг 404 без тела → тест про «Чат не найден или удалён» краснеет (F-4r);
//  • убрать проверку фич-флага у кнопки → красный негативный тест про флаг.
const fullEstimate: ContextEstimate = { window: 200000, pct: 92, fresh: false, level: 'danger' };

function renderPillWith(est: ContextEstimate, items: ChatItem[]): string {
  return renderToStaticMarkup(createElement(RingPillBadge, {
    estimate: est, isWaiting: false, isCompacting: false, canCompact: false, onCompact: () => {},
    online: true, assistantName: 'Тест',
    isCliProvider: false, providerName: 'Claude', cost, falCost, glifCost, balance, billing, windows,
    items, sessionId: 's1',
  }));
}

describe('RingPillBadge: предложение переноса при полном контексте (F-4, вторая точка входа)', () => {
  it('флаг вкл, уровень danger — подсказка предлагает новый чат с запиской', () => {
    setAllFlags({ 'prompt-cache': true });
    const html = renderPillWith(fullEstimate, [cacheItem()]);
    expect(ariaOf(html), 'предложение — в подсказке и aria-label пилюли').toContain('можно начать новый чат с запиской');
  });

  it('флаг выкл — предложения нет (фича за флагом)', () => {
    setAllFlags({});
    expect(ariaOf(renderPillWith(fullEstimate, [cacheItem()]))).not.toContain('можно начать новый чат');
  });

  it('уровень normal — предложения нет, даже с включённым флагом', () => {
    setAllFlags({ 'prompt-cache': true });
    expect(ariaOf(renderPillWith(estimate, [cacheItem()]))).not.toContain('можно начать новый чат');
  });

  it('нет данных кэша — предложения нет: обещание гейтится тем же `cache`, что кнопка (F-4r)', () => {
    setAllFlags({ 'prompt-cache': true });
    // Danger-контекст без строк кэша в ленте: в таком чате перенос нечем подтвердить —
    // обещание без действия (тупик)
    expect(ariaOf(renderPillWith(fullEstimate, []))).not.toContain('можно начать новый чат');
  });
});

describe('RingPillBadge: кнопка «Перенести в новый чат» (F-4)', () => {
  const pendingStore: Record<string, string> = {};
  beforeAll(() => {
    installFakeDom();
    // В node-окружении нет sessionStorage — заглушка под проверку префилла композера
    (globalThis as { sessionStorage?: unknown }).sessionStorage = {
      setItem: (k: string, v: string) => { pendingStore[k] = v; },
      getItem: (k: string) => pendingStore[k] ?? null,
      removeItem: (k: string) => { delete pendingStore[k]; },
    };
  });
  afterAll(() => uninstallFakeDom());

  const mountPill = () => mount(createElement(RingPillBadge, {
    estimate, isWaiting: false, isCompacting: false, canCompact: false, onCompact: () => {},
    online: true, assistantName: 'Тест',
    isCliProvider: false, providerName: 'Claude', cost, falCost, glifCost, balance, billing, windows,
    items: [cacheItem()], sessionId: 's1',
  }));

  type Node = { nodeType: number; nodeName: string; childNodes?: Node[] };
  const walk = (x: Node, f: (n: Node) => void): void => {
    if (x.nodeType === 1) f(x);
    for (const c of x.childNodes ?? []) walk(c, f);
  };
  const buttonByLabel = (root: Node, label: string): Node | null => {
    let hit: Node | null = null;
    walk(root, (x) => {
      if (!hit && x.nodeName === 'BUTTON' && textNodes(x as never).join('').includes(label)) hit = x;
    });
    return hit;
  };
  // Контент поповера монтируется только при открытии (open в BadgeShell) — открываем
  // клик по самой пилюле: её кнопка — первая BUTTON в контейнере
  const openPopover = (container: Node) => {
    let pill: Node | null = null;
    walk(container, (x) => { if (!pill && x.nodeName === 'BUTTON') pill = x; });
    act(() => { (reactProps(pill as never)!.onClick as () => void)(); });
  };

  it('флаг вкл — кнопка в поповере; флаг выкл — её нет', () => {
    setAllFlags({ 'prompt-cache': true });
    let { root, container } = mountPill();
    try {
      openPopover(container);
      expect(textNodes(container as never).join(' '), 'кнопка за включённым флагом').toContain('Перенести в новый чат');
    } finally {
      act(() => root.unmount());
    }
    setAllFlags({});
    ({ root, container } = mountPill());
    try {
      openPopover(container);
      expect(textNodes(container as never).join(' '), 'за выключенным флагом кнопки нет').not.toContain('Перенести в новый чат');
    } finally {
      act(() => root.unmount());
    }
  });

  it('409: текст ошибки бэка показывается как есть, а не «сбой»', async () => {
    setAllFlags({ 'prompt-cache': true });
    handoff.fn = () => Promise.reject(
      Object.assign(new Error('чат занят другим ходом: просьба о передаче поставлена в очередь'), { status: 409 }),
    );
    openedChats.length = 0;
    const { root, container } = mountPill();
    try {
      openPopover(container);
      const btn = buttonByLabel(container, 'Перенести в новый чат');
      expect(btn, 'кнопка смонтирована').not.toBeNull();
      act(() => { (reactProps(btn as never)!.onClick as () => void)(); });
      await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
      expect(textNodes(container as never).join(' ')).toContain('чат занят другим ходом');
      expect(openedChats, 'перехода нет — преемника не создано').length === 0;
    } finally {
      act(() => root.unmount());
    }
  });

  it('успех: переход в новый чат, черновик — в префилл композера', async () => {
    setAllFlags({ 'prompt-cache': true });
    handoff.fn = () => Promise.resolve({ chatId: 'new-chat', projectId: null, draft: 'Короткая запись о разговоре', warm: true });
    openedChats.length = 0;
    toasts.length = 0;
    delete pendingStore['cc_pending_chat_prompt'];
    const { root, container } = mountPill();
    try {
      openPopover(container);
      const btn = buttonByLabel(container, 'Перенести в новый чат');
      act(() => { (reactProps(btn as never)!.onClick as () => void)(); });
      await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
      expect(openedChats).toEqual(['new-chat']);
      expect(pendingStore['cc_pending_chat_prompt'], 'черновик ждёт композер нового чата').toBe('Короткая запись о разговоре');
      expect(toasts.some(t => t.includes('Новый чат открыт')), 'тост успеха — после состоявшегося перехода').toBe(true);
      expect(textNodes(container as never).join(' ')).not.toContain('Переношу…');
    } finally {
      act(() => root.unmount());
    }
  });

  it('openChatById=false — тоста «открыт» нет, черновик снят (F-4r)', async () => {
    setAllFlags({ 'prompt-cache': true });
    handoff.fn = () => Promise.resolve({ chatId: 'ghost-chat', projectId: null, draft: 'Запись о разговоре', warm: false });
    openOk.value = false;
    openedChats.length = 0;
    toasts.length = 0;
    delete pendingStore['cc_pending_chat_prompt'];
    const { root, container } = mountPill();
    try {
      openPopover(container);
      const btn = buttonByLabel(container, 'Перенести в новый чат');
      act(() => { (reactProps(btn as never)!.onClick as () => void)(); });
      await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
      expect(toasts.some(t => t.includes('открыт')), 'перехода нет — тоста об успехе тоже нет').toBe(false);
      expect(pendingStore['cc_pending_chat_prompt'], 'черновик не утекает в следующий открытый чат').toBeUndefined();
    } finally {
      act(() => root.unmount());
      openOk.value = true;
    }
  });

  it('404 без тела — человек читает «Чат не найден или удалён», а не «Not Found» (F-4r)', async () => {
    setAllFlags({ 'prompt-cache': true });
    // Так приходит 404 без тела: message = statusText (offline.ts), текст нечеловеческий
    handoff.fn = () => Promise.reject(Object.assign(new Error('Not Found'), { status: 404 }));
    openedChats.length = 0;
    const { root, container } = mountPill();
    try {
      openPopover(container);
      const btn = buttonByLabel(container, 'Перенести в новый чат');
      act(() => { (reactProps(btn as never)!.onClick as () => void)(); });
      await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
      expect(textNodes(container as never).join(' ')).toContain('Чат не найден или удалён');
    } finally {
      act(() => root.unmount());
    }
  });
});

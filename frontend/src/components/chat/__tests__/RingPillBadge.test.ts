// Ари-лейбл кольцевой пилюли: строка «Локальная модель: <хвост/лицо>»
// показывается только для локального чата (m1, ревью Глеба). Статический рендер —
// как соседние тесты шапки (ToolUseView.test, sphereGate.test).
// Вторая часть — циферблат кэша промпта (F-1b): появление по данным ленты, текст на
// десктопе и его отсутствие на мобиле, «идёт ход» без минут.
import { act, createElement } from 'react';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterEach, beforeEach, beforeAll, afterAll, describe, expect, it, vi } from 'vitest';
// Сети в тестах нет: offline подменяем до импорта api. request — пустой ответ: путь
// mount с sessionId зовёт localModel.lastTurn через useLocalModelLive — «данных о ходе
// нет», состояние хука не трогается
vi.mock('../../../lib/offline', () => ({
  __esModule: true,
  request: () => Promise.resolve({ lastTurn: null }),
  readStoredToken: () => 'tok',
  cachedToken: () => 'tok',
  invalidateTokenCache: () => {},
  onPassiveEvict: () => () => {},
  parseFileContentCacheKey: () => null,
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
    const label = renderAriaLabel(liveGenerating);
    expect(label).toContain('Локальная модель: 38 ток/с');
  });

  it('мобила, генерация, контекст в норме — строка со словом-лицом «38 т/с»', () => {
    const label = renderAriaLabel(liveGenerating, true);
    expect(label).toContain('Локальная модель: 38 т/с');
  });

  it('idle без скорости последнего хода — строки нет (активного состояния нет)', () => {
    expect(hasLmLine(renderAriaLabel(liveIdle))).toBe(false);
  });

  it('idle с lastTurn: null — строки нет', () => {
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
  // Обвязка циферблата (сам циферблат, строка в подсказке, секция поповера) показывается всегда;
  // сброс флагов перед каждым тестом.
  beforeEach(() => setAllFlags({}));
  // Тест про секцию поповера монтирует пилюлю в fakeDom: контент поповера живёт только
  // при открытии (open в BadgeShell) — открываем клик по самой пилюле (её кнопка —
  // первая BUTTON в контейнере)
  beforeAll(() => installFakeDom());
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
  const openPopover = (container: Node) => {
    let pill: Node | null = null;
    walk(container, (x) => { if (!pill && x.nodeName === 'BUTTON') pill = x; });
    act(() => { (reactProps(pill as never)!.onClick as () => void)(); });
  };

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

  // Дефект QA: кнопка порогов и вся секция «Кэш промпта» оставались при выключенном
  // флаге — секция поповера гейтуется целиком тем же promptCacheOn, что и циферблат.
  // Инвариант F-3: в секции остаются причина промаха («Последний промах») и порог —
  // кнопки «Перенести в новый чат» больше нет: перенос удалён из шапки целиком.
  it('в поповере секция «Кэш промпта»: с причиной промаха и порогом, без кнопки переноса', () => {
    const { root, container } = mountPill();
    try {
      openPopover(container);
      const text = textNodes(container as never).join(' ');
      expect(text, 'при включённом флаге секция и кнопка на месте').toContain('Кэш промпта');
      expect(text).toContain('Когда предупреждать об остывании');
      expect(text).toContain('Последний промах');
      expect(text, 'перенос чата удалён из шапки').not.toContain('Перенести в новый чат');
    } finally {
      act(() => root.unmount());
    }
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

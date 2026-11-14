// W7c · Встройка «Исполнителей ночи» в строку места в ApplyTab:
// — блок рисуется только месту с флагом каталога parallelExecutors (у wiki-triage его нет)
//   и только админу: не-админ строк мест не видит вовсе → 0 вхождений;
// — при непустом списке места выбор маршрута подписан «ночь идёт по списку исполнителей»,
//   при пустом — подписи нет;
// — saveThenPick_lastWins: сохранение списка и выбор маршрута в одной строке подряд —
//   до экрана доходит ответ последней записи: ответ старого перечитывания /usage не
//   затирает новый маршрут. Мутация «применять любой ответ usage» (в ApplyTab.tsx снять
//   страж `if (!isLastWrite(a.key, my)) return;` в saveExecutors) — тест красный: строка
//   вернулась бы к «Локальная · …» из устаревшего снимка;
// — отказ PUT: текст валидатора показывает сам блок (Notice, W7b), строка места цела,
//   перечитки /usage после отказа нет;
// — подзадача (ревью W7b, minor 1) закрыта тестом в NightExecutorsEditor.test.ts.
//
// request() мокаем роутером по адресу (приём как в localActionExecutorsApi.test.ts):
// ответы /usage и оба PUT — то, что держит в руках тест (порядок их прихода задаёт тест).
// Секция «Картинки» и loadModels вне задачи — заглушены.

import { act, createElement, useState, type ReactElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../components/chat/__tests__/fakeDom';

const { request } = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../../../lib/offline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/offline')>();
  return { ...orig, request };
});
vi.mock('../ImageGenSection', () => ({ __esModule: true, ImageGenSection: () => null }));
vi.mock('../../../lib/models', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/models')>();
  return { ...orig, loadModels: vi.fn(async () => []) };
});

import { ApplyTab } from '../ApplyTab';
import type { ProviderData } from '../../../lib/modelProvidersShared';
import type { OllamaActionInfo, OllamaUsageInfo } from '../../../types';

type Node = ReturnType<typeof mount>['container'];

const NIGHTLY = 'wiki-nightly';
const TRIAGE = 'wiki-triage';
const CAPTION = 'ночь идёт по списку исполнителей';
// Подпись маршрута места до выбора (route='local' + модель основной локали из снимка)
const LOCAL_LABEL = 'Локальная · qwen3.8-27b';
// Карточка уровня в панели пикера (TIERS.strong.title) — чем админ меняет маршрут
const TIER_TITLE = 'Сильная';

// Движки — форма ollama.localEngines (W2c), как их строит бэк: «local» первым
const ENGINES = [
  { name: 'local', title: 'Qwen3.8-27B', maxConcurrency: 2 },
  { name: 'gemma', title: 'Gemma 4', maxConcurrency: 2 },
];
// Непустой список места — форма actions[].executors (W7a)
const LIST = { maxTotal: 2, items: [{ engine: 'local', parallel: 1 }, { engine: 'gemma', parallel: 1 }] };

const action = (key: string, title: string, over: Partial<OllamaActionInfo> = {}): OllamaActionInfo =>
  ({ key, title, group: 'Вики', routedToOllama: true, ...over });

// Снимок: два места одной группы — flagged (wiki-nightly) и обычное (wiki-triage).
// Ответ /usage — обёрнут как UsageResponse: ApplyTab читает d.ollama, мок обязан
// давать ту же форму, иначе «перечитка» ничего не меняет и проверка проверяла бы вакуум.
const usage = (over: Partial<OllamaActionInfo> = {}): OllamaUsageInfo => ({
  enabled: true, model: 'qwen3.8-27b', provider: 'ollama', localEngines: ENGINES,
  actions: [
    action(NIGHTLY, 'Ночной разбор вики', { parallelExecutors: true, route: 'local', ...over }),
    action(TRIAGE, 'Отбор чатов', { route: 'local' }),
  ],
});
const usageResponse = (over: Partial<OllamaActionInfo> = {}): { ollama: OllamaUsageInfo } =>
  ({ ollama: usage(over) });

const SETTINGS = { version: 1, global: {}, presets: [] };
const PREVIEW = { model: null, source: null, tier: null, tierOrigin: null, preset: null, chain: [] };
const TIER_MODELS = { strong: 'opus-x', medium: 'sonnet-x', weak: 'haiku-x' };

type Gate = { p: Promise<unknown>; resolve: (v: unknown) => void; reject: (e: unknown) => void };
function gate(): Gate {
  let resolve!: (v: unknown) => void; let reject!: (e: unknown) => void;
  const p = new Promise<unknown>((res, rej) => { resolve = res; reject = rej; });
  return { p, resolve, reject };
}

type Call = { kind: string; url: string; body?: string };
// Ответы по порядку вызовов: thunk может вернуть Gate-обещание (ответ держит тест)
// или бросить (отказ). Не названный тестом — дефолт.
let calls: Call[] = [];
let counts: Record<string, number> = {};
let answers: Record<string, (() => unknown)[]> = {};

function kindOf(url: string): string {
  if (url === '/usage') return 'usage';
  if (url.startsWith('/admin/local-actions/') && url.endsWith('/executors')) return 'putExecutors';
  if (url.startsWith('/admin/local-actions/')) return 'putRoute';
  if (url === '/specialties/settings') return 'settings';
  if (url.startsWith('/models/preview')) return 'preview';
  return 'other';
}

beforeEach(() => {
  calls = []; counts = {}; answers = {};
  request.mockReset();
  request.mockImplementation((url: string, opts?: { method?: string; body?: string }) => {
    const kind = kindOf(url);
    calls.push({ kind, url, body: opts?.body });
    counts[kind] = (counts[kind] ?? 0) + 1;
    const i = counts[kind] - 1;
    try {
      if (kind === 'settings') return Promise.resolve(SETTINGS);
      if (kind === 'preview') return Promise.resolve(PREVIEW);
      if (kind === 'usage') return Promise.resolve((answers.usage?.[i] ?? (() => usageResponse()))());
      if (kind === 'putExecutors') return Promise.resolve((answers.putExecutors?.[i] ?? (() => ({ key: NIGHTLY, maxTotal: 0, executors: [] })))());
      if (kind === 'putRoute') return Promise.resolve((answers.putRoute?.[i] ?? (() => ({ key: NIGHTLY, route: 'tier:strong', source: 'admin', preset: null, engine: null })))());
    } catch (e) { return Promise.reject(e); }
    return Promise.resolve({});
  });
});
const callCount = (kind: string): number => calls.filter((x) => x.kind === kind).length;

function Harness({ isAdmin, initial }: { isAdmin: boolean; initial: OllamaUsageInfo }): ReactElement {
  const [info, setInfo] = useState<OllamaUsageInfo | undefined>(initial);
  const data: ProviderData = {
    info, setInfo, globalSettings: null, setGlobalSettings: () => {}, users: [],
    ownTiers: null, setOwnTiers: () => {}, userTiers: null, setUserTiers: () => {},
    selectedTiers: null, globalTiers: null,
    effectiveTierModel: (t) => TIER_MODELS[t],
  };
  return createElement(ApplyTab, {
    isAdmin, data, models: [], tierModels: TIER_MODELS, savingScope: null, onSaveLayer: async () => {},
  });
}

const roots: Root[] = [];

function render(opts: { isAdmin?: boolean; initial?: OllamaUsageInfo } = {}): { c: Node; root: Root } {
  installFakeDom();
  // Положение панели пикера считается от viewport (RoutePicker, useLayoutEffect) —
  // без этих чисел оно NaN; задаём явно, как в Menu.test.ts и Tooltip.test.ts
  const w = (globalThis as { window?: Record<string, unknown> }).window!;
  w.innerWidth = 1280; w.innerHeight = 900;
  const { root, container } = mount(createElement(Harness, { isAdmin: opts.isAdmin ?? true, initial: opts.initial ?? usage() }));
  roots.push(root);
  return { c: container, root };
}

async function settle(times = 25): Promise<void> {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

function texts(c: Node): string[] { return textNodes(c).map((t) => t.trim()).filter(Boolean); }
function joined(c: Node): string { return texts(c).join(' '); }
function has(c: Node, needle: string): boolean { return joined(c).includes(needle); }
function countAttr(c: Node, attr: string, value: string): number {
  let n = 0;
  const walk = (x: Node): void => {
    if (x.nodeType === 1 && x.getAttribute(attr) === value) n++;
    for (const ch of x.childNodes) walk(ch);
  };
  walk(c);
  return n;
}

const row = (c: Node, key: string): Node | null => findNodeByAttr(c, 'data-testid', `action-row-${key}`);
const block = (scope: Node): Node | null => findNodeByAttr(scope, 'data-testid', 'night-executors');
const caption = (scope: Node): Node | null => findNodeByAttr(scope, 'data-testid', 'night-executors-caption');
const engineRow = (scope: Node, name: string): Node | null => findNodeByAttr(scope, 'data-testid', `night-exec-row-${name}`);
function checked(scope: Node, name: string): boolean {
  const r = engineRow(scope, name);
  const cb = r && findNodeByAttr(r, 'role', 'checkbox');
  return cb?.getAttribute('aria-checked') === 'true';
}
function click(node: Node): void {
  const p = reactProps(node) as { onClick?: (e: unknown) => void };
  act(() => { p.onClick!(undefined); });
}
// Отметка движка в блоке строки и её сохранение
function markEngine(c: Node, name: string): void {
  click(findNodeByAttr(engineRow(row(c, NIGHTLY)!, name)!, 'role', 'checkbox')!);
}
function clickSave(c: Node): void {
  click(findNodeByAttr(row(c, NIGHTLY)!, 'data-testid', 'night-executors-save')!);
}
// Кнопка по тексту внутри узла: триггер пикера несёт текущую подпись маршрута,
// карточка уровня в панели — её название
function buttonWithText(scope: Node, needle: string): Node | null {
  let hit: Node | null = null;
  const walk = (x: Node): void => {
    if (hit) return;
    if (x.nodeType === 1 && x.nodeName === 'BUTTON' && textNodes(x).join('').includes(needle)) { hit = x; return; }
    for (const ch of x.childNodes) walk(ch);
  };
  walk(scope);
  return hit;
}
// Выбор маршрута — как его делает человек: открыть пикер строки и взять карточку уровня
function pickRoute(c: Node): void {
  click(buttonWithText(row(c, NIGHTLY)!, LOCAL_LABEL)!);
  click(buttonWithText(c, TIER_TITLE)!);
}

const httpError = (status: number, body: unknown): Error =>
  Object.assign(new Error(`HTTP ${status}`), { status, body });

afterEach(() => {
  for (const root of roots.splice(0)) void root.unmount();
  uninstallFakeDom();
});

describe('«Исполнители ночи» в строке места (W7c)', () => {
  it('блок — только месту с флагом каталога и только админу', () => {
    const { c } = render();
    // В снимке ровно одно место с флагом — ровно один блок, и он в строке wiki-nightly
    expect(countAttr(c, 'data-testid', 'night-executors')).toBe(1);
    const blk = block(row(c, NIGHTLY)!);
    expect(blk).not.toBeNull();
    expect(block(row(c, TRIAGE)!)).toBeNull();
    // Блок стоит во всей ширине строки (ячейка на обе колонки сетки), а не рядом с
    // пикером: шире строки он быть не может, и горизонтального переполнения нет
    const holder = blk!.parentElement!;
    const st = (reactProps(holder) as { style?: Record<string, string> }).style!;
    expect(st.gridColumn).toBe('1 / -1');

    // Не-админ: ApplyTab не показывает строк мест (назначение мест — только админ)
    const off = render({ isAdmin: false });
    expect(countAttr(off.c, 'data-testid', 'night-executors')).toBe(0);
    expect(row(off.c, NIGHTLY)).toBeNull();
  });

  it('непустой список: выбор маршрута подписан; пустой — подписи нет', () => {
    const withList = render({ initial: usage({ executors: LIST }) });
    expect(caption(row(withList.c, NIGHTLY)!)).not.toBeNull();
    expect(has(withList.c, CAPTION)).toBe(true);
    // Подпись относится к месту с флагом: у обычного места её нет
    expect(caption(row(withList.c, TRIAGE)!)).toBeNull();

    const empty = render({ initial: usage({ executors: null }) });
    expect(caption(row(empty.c, NIGHTLY)!)).toBeNull();
    expect(has(empty.c, CAPTION)).toBe(false);
    // сам блок при этом есть: пустой список — состояние места, а не отсутствие настройки
    expect(block(row(empty.c, NIGHTLY)!)).not.toBeNull();
  });

  it('saveThenPick_lastWins: сохранение списка и выбор маршрута подряд — доходит ответ последнего', async () => {
    const execGate = gate(); const usageGate = gate(); const routeGate = gate();
    answers = {
      putExecutors: [() => execGate.p],
      // Перечитка после сохранения отвечает снимком ДО выбора маршрута: сервер ещё не видел
      // PUT маршрута — в этом снимке строка идёт по-прежнему «Локальная»
      usage: [() => usageGate.p],
      putRoute: [() => routeGate.p],
    };

    const { c } = render();
    markEngine(c, 'local');
    clickSave(c);
    expect(callCount('putExecutors')).toBe(1);
    execGate.resolve({ key: NIGHTLY, maxTotal: 1, executors: [{ engine: 'local', parallel: 1 }] });
    await settle();
    // сохранение запустило перечитку снимка
    expect(callCount('usage')).toBe(1);

    // Пока перечитка в полёте, админ выбирает маршрут той же строки
    const nightly = () => row(c, NIGHTLY)!;
    expect(has(nightly(), LOCAL_LABEL)).toBe(true);
    pickRoute(c);
    expect(callCount('putRoute')).toBe(1);
    routeGate.resolve({ key: NIGHTLY, route: 'tier:strong', source: 'admin', preset: null, engine: null });
    await settle();
    expect(has(nightly(), TIER_TITLE)).toBe(true);

    // И только теперь приходит ответ БОЛЕЕ СТАРОГО запроса: он не имеет права вернуть
    // строку к прежнему маршруту (мутация «применять любой ответ usage» — красное).
    // Проверяем свою строку: соседнее место (wiki-triage) по своему маршруту не тронуто.
    usageGate.resolve(usageResponse({ route: 'local', executors: LIST }));
    await settle();
    expect(has(nightly(), LOCAL_LABEL)).toBe(false);
    expect(has(nightly(), TIER_TITLE)).toBe(true);
    // строка места целая: её блок на месте
    expect(block(nightly())).not.toBeNull();
  });

  it('отказ PUT: текст валидатора в Notice блока, строка места цела, перечитки /usage нет', async () => {
    const errors = ['engine «gemma» не среди движков', 'maxTotal 5 — допустимо 1..4'];
    answers = { putExecutors: [() => { throw httpError(400, { errors }); }] };
    const { c } = render();
    markEngine(c, 'gemma');
    clickSave(c);
    await settle();
    expect(callCount('putExecutors')).toBe(1);
    // админ видит, ЧТО именно не принято (контракт W7a/W7b)
    expect(has(c, errors[0])).toBe(true);
    expect(has(c, errors[1])).toBe(true);
    // строка места не сломана: её заголовок и её блок на месте, черновик отметки цел
    expect(has(c, 'Ночной разбор вики')).toBe(true);
    expect(block(row(c, NIGHTLY)!)).not.toBeNull();
    expect(checked(row(c, NIGHTLY)!, 'gemma')).toBe(true);
    // после отказа снимок не перечитывается
    expect(callCount('usage')).toBe(0);
  });
});

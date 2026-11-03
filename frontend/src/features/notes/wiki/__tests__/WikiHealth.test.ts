// Рендер-тест «Здоровья» (S5f): каждый вид находки из _health.md — своей строкой (c2),
// с путём страницы, «связанными», ключом и счётом; сообщение с « · » внутри себя остаётся
// целым (разбор — зеркало бэкового FindingLine, а не деление по « · »); счётчики последнего
// отчёта; «нет данных» при 404, а не пустой экран; отказ прогона — русским текстом по коду,
// прежние находки при этом не затираются «Находки (0)»; кнопка блокируется на время запроса;
// запоздалый ответ прежней загрузки не затирает свежий отчёт; предложение повышения — только
// подсказка, кнопки «Повысить» нет (Ф2). Доработка S5f (major): при смене раздела A→B отчёт A
// уходит сразу — ни строк A при загрузке B, ни строк A при отказе B; запоздалый ответ A после
// переключения не возвращается (поколение запросов остаётся).
//
// Мокан только транспорт (offline.request) и signalr — тот же приём, что в WikiInbox.test.
//
// Блок F5c («Принять как мою правку»): его мутации перечислены в шапке своего describe.
//
// Мутации этого прогона (вывод — в отчёте задачи):
//  (1) убрать «предложение повышения» из таблицы видов → «каждый вид своей строкой»
//      краснеет (10 строк не хватает);
//  (2) делить строку находки по « · » целиком (не снимать хвост с конца) →
//      «сообщение с точками внутри себя» краснеет;
//  (3) снять проверку seq в загрузке → «запоздалый ответ» краснеет;
//  (4) снять disabled={busy} у кнопки → «блокировка на время запроса» краснеет;
//  (5) не сбрасывать report в начале load (доработка S5f) → «при смене раздела A→B…» краснеет;
//  (6) KIND_BY_LABEL — обычный объект с обращением по ключу (W4-4) → «чужая метка вида, в т. ч. имя
//      из прототипа» краснеет;
//  (7) 404 после прогона — в actionError (W4-4) → «404 после успешного прогона» краснеет;
//  (8) не читать «## Не проверено» → «непроверенные страницы» краснеет;
//  (9) убрать сверку total/строк → «Находки (N) не равны строкам» краснеет;
//  (10) счёт без пояснения вида («счёт: N») → «счёт подписан по виду» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiHealth, parseHealth } from '../WikiHealth';

// Мок с сохранением остальных экспортов: цепочка мокованного доходит до кита хоста
// (WikiHealth → kit) — full-replacement мок может рваться на недостающем имени
// из цепочки. Паттерн — как в NotesPage.test.ts.
vi.mock('../../../../lib/signalr', async (orig) => ({
  ...await orig<typeof import('../../../../lib/signalr')>(),
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));
vi.mock('../../../../lib/offline', async (orig) => ({
  ...await orig<typeof import('../../../../lib/offline')>(),
  request: vi.fn(),
  readStoredUserId: vi.fn(() => 'u1'),
}));

// Флаги управляются тестом: по умолчанию выключены — прежние тесты видят прежний вид;
// блок F5c включает llm-wiki и notes-wiki-unified (тот же приём, что в WikiPageView.test).
// useFeature — хук, и мок зовёт настоящий хук при каждом вызове (F5c-rw2): если второй
// useFeature вызывается условно, при перерисовке с другим флагом число хуков меняется и
// React падает — тест «переключение флага при открытом Здоровье» краснеет. При безусловных
// вызовах число хуков постоянно в любом порядке флагов, и остальные тесты это не задевает:
// в их пределах флаг на одном экземпляре не меняется.
const flags = vi.hoisted(() => ({ on: new Set<string>() }));
vi.mock('../../../../lib/featureFlags', async (orig) => {
  const { useState } = await import('react');
  return {
    ...await orig<typeof import('../../../../lib/featureFlags')>(),
    useFeature: (k: string) => { useState(0); return flags.on.has(k); },
  };
});

import { request } from '../../../../lib/offline';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;
type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

// _health.md как его пишет бэк (WikiLintService.RenderHealth/FindingLine): вид · страница ·
// сообщение · связанные · ключ · счёт. Дрейф — с « · » внутри сообщения, «упоминание» —
// с экранированным backtick, «повышение» — о разделе целиком.
const HEALTH = [
  '# Здоровье раздела «projects/p1»',
  '',
  'Проверено: 2026-10-31T03:00:00.0000000+00:00. Код: v1.',
  '',
  '## Находки (10)',
  '- призрак · страница `Проекты/p1/Тема.md` · ссылка ведёт в пустоту · связанные: `Проекты/p1/Другое.md` · счёт: 2',
  '- почти дубль · страница `Проекты/p1/А.md` · та же тема, что у другой страницы · связанные: `Проекты/p1/Б.md`, `Проекты/p1/В.md`',
  '- нет источников · страница `Проекты/p1/Г.md` · у страницы нет ни одной сноски',
  '- дрейф · страница `Проекты/p1/Д.md` · файл изменён · сверка с манифестом не сошлась',
  '- ссылка вне раздела · страница `Проекты/p1/Е.md` · ссылка ведёт в чужой проект',
  '- сирота · страница `Проекты/p1/Ж.md` · на страницу никто не ссылается',
  '- без изменений · страница `Проекты/p1/З.md` · 90 дней без изменений',
  // как это пишет бэк: backtick в недоверенном тексте экранирован «\`» (Escape)
  '- упоминание без ссылки · страница `Проекты/p1/И.md` · имя \\`Альфа\\` названо без ссылки',
  '- один owns у нескольких · страница `Проекты/p1/К.md` · owns встречается дважды · ключ: `owns/альфа`',
  '- предложение повышения · раздел · та же сущность в двух проектах · ключ: `owns/альфа`',
  '',
].join('\n');

const roots: Root[] = [];

function mountAt(key: string | null): { root: Root; container: Cont } {
  installFakeDom();
  const { root, container } = mount(createElement(WikiHealth, { selectedKey: key }));
  roots.push(root);
  return { root, container };
}

function render(): Cont {
  return mountAt('projects/p1').container;
}

// Смена раздела на том же экземпляре: wikiTabs рендерит подвкладку без key (wikiTabs.tsx:42),
// значит при смене selectedKey React обновляет props, а состояние компонента остаётся —
// ровно тот путь, на котором живёт дефект доработки S5f
function selectSection(root: Root, key: string) {
  act(() => { root.render(createElement(WikiHealth, { selectedKey: key })); });
}

async function settle(times = 10) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

function texts(container: Cont): string[] {
  return textNodes(container).map(t => t.trim()).filter(Boolean);
}

function has(container: Cont, needle: string): boolean {
  return texts(container).join(' ').includes(needle);
}

function nodeByTestId(container: Cont, testId: string): AnyNode | null {
  return findNodeByAttr(container, 'data-testid', testId);
}

function rows(container: Cont): AnyNode[] {
  const out: AnyNode[] = [];
  const walk = (n: AnyNode) => {
    if (n.getAttribute('data-testid') === 'wiki-health-row') out.push(n);
    for (const c of n.childNodes) walk(c);
  };
  walk(container);
  return out;
}

function clickButton(container: Cont, testId: string) {
  const node = nodeByTestId(container, testId);
  if (!node) throw new Error(`нет кнопки ${testId}`);
  const props = reactProps(node) as { onClick?: () => void };
  if (!props?.onClick) throw new Error(`у ${testId} нет onClick`);
  act(() => { props.onClick(); });
}

function buttonsText(container: Cont): string[] {
  const all: AnyNode[] = [];
  const walk = (n: AnyNode) => { all.push(n); for (const ch of n.childNodes) walk(ch); };
  walk(container);
  return all.filter(n => n.nodeName === 'BUTTON').map(n => textNodes(n).join(''));
}

function mockHealth(content: string) {
  req.mockImplementation((url: string) => {
    if (url.startsWith('/wiki/health/')) return Promise.resolve({ section: 'projects/p1', content });
    return Promise.resolve(null);
  });
}

// ─── Помощники блока F5c ───

const bothFlags = () => { flags.on.add('llm-wiki'); flags.on.add('notes-wiki-unified'); };

const docBody = (): AnyNode => (globalThis as { document: { body: AnyNode } }).document.body;
const docText = (): string => textNodes(docBody()).map(t => t.trim()).filter(Boolean).join(' ');

function allNodes(root: AnyNode, out: AnyNode[] = []): AnyNode[] {
  out.push(root);
  for (const c of root.childNodes) allNodes(c, out);
  return out;
}

function buttonByText(root: AnyNode, text: string): AnyNode | null {
  return allNodes(root).find(n => n.nodeName === 'BUTTON' && textNodes(n).join('').trim() === text) ?? null;
}

// помощник клика НЕ кликает disabled-кнопку (тот же, что в WikiPageEditor.test):
// выключенная на время запроса кнопка не принимает повторный клик
function click(node: AnyNode | null, what: string) {
  if (!node) throw new Error(`нет узла: ${what}`);
  const p = reactProps(node) as { onClick?: (e: unknown) => void; disabled?: boolean } | null;
  if (!p?.onClick) throw new Error(`у «${what}» нет onClick`);
  if (p.disabled) return;
  act(() => { p.onClick!(undefined); });
}

// атрибуты узла: disabled у выключенной кнопки — '' (React), у активной — атрибута нет
const attrs = (n: AnyNode | null): Record<string, string> =>
  ((n as { attributes?: Record<string, string> } | null)?.attributes) ?? {};

const adoptBtn = (root: AnyNode) => buttonByText(root, 'Принять как мою правку');
const confirmBtn = () => buttonByText(docBody(), 'Принять как мою правку');

// плашка исхода (Notice) под своим testid — узел с заголовком и текстом
const notice = (root: AnyNode) => findNodeByAttr(root, 'data-testid', 'wiki-health-adopt-mine-flash');

// строка «дрейфа» из HEALTH — единственная страница с дрейфом в моканом отчёте
const driftRow = (c: Cont) => rows(c).find(r => textNodes(r).join(' ').includes('файл изменён')) ?? null;

// Весь поток F5c: здоровье (каждый ответ — firstContent: GET health отдаёт _health.md как
// есть, бэк его не пересчитывает — второй ответ = тот же контент, вердикт R-F5c),
// GET page с хэшем, POST /wiki/adopt — отложенным промисом, чтобы тест держал запрос
// открытым (гонка). health/post — счётчики и тела вызовов (url — чтобы отличать разделы).
function mockAdoptFlow(firstContent: string, secondContent?: string) {
  const health: { url: string }[] = [];
  const post: { body: unknown }[] = [];
  const ctl: { resolve?: (v: unknown) => void; reject?: (e: unknown) => void } = {};
  req.mockImplementation((url: string, init?: { method?: string; body?: string }) => {
    if (String(url).startsWith('/wiki/health/')) {
      health.push({ url: String(url) });
      const content = health.length === 1 || !secondContent ? firstContent : secondContent;
      return Promise.resolve({ section: 'projects/p1', content });
    }
    if (String(url).startsWith('/wiki/page?')) {
      return Promise.resolve({ pageKey: 'Проекты/p1/Д.md', content: 'текст страницы', hash: 'h-base' });
    }
    if ((init?.method ?? '') === 'POST' && String(url).endsWith('/wiki/adopt')) {
      post.push({ body: JSON.parse(String(init!.body)) });
      return new Promise<unknown>((r, j) => { ctl.resolve = r; ctl.reject = j; });
    }
    return Promise.resolve(null);
  });
  return {
    health, post,
    resolve: (v: unknown) => { ctl.resolve!(v); },
    reject: (e: unknown) => { ctl.reject!(e); },
  };
}

beforeEach(() => {
  flags.on.clear();
  req.mockReset();
  req.mockImplementation(() => Promise.resolve(null));
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  uninstallFakeDom();
});

describe('«Здоровье» раздела (S5f)', () => {
  it('каждый вид находки — своей строкой, с путём, связанными, ключом и счётом (c2)', async () => {
    mockHealth(HEALTH);
    const c = render();
    await settle();
    expect(rows(c)).toHaveLength(10);
    for (const label of [
      'призрак', 'почти дубль', 'нет источников', 'дрейф', 'ссылка вне раздела',
      'сирота', 'без изменений', 'упоминание без ссылки', 'один owns у нескольких',
      'предложение повышения',
    ]) {
      expect(has(c, label)).toBe(true);
    }
    // содержательное из строки бэка — текстом: путь страницы, «связанные», ключ, счёт
    expect(has(c, 'Проекты/p1/Тема.md')).toBe(true);
    expect(has(c, 'ссылка ведёт в пустоту')).toBe(true);
    expect(has(c, 'Проекты/p1/Другое.md')).toBe(true);
    // счёт подписан по виду: у призрака это число ссылающихся страниц, не безымянное «2»
    expect(has(c, 'ссылаются страниц: 2')).toBe(true);
    expect(has(c, 'счёт: 2')).toBe(false);
    expect(has(c, 'ключ: owns/альфа')).toBe(true);
    // экранированный бэком backtick показывается как кавычка, а не как «\`»
    expect(has(c, 'имя `Альфа` названо без ссылки')).toBe(true);
    expect(has(c, '\\`Альфа\\`')).toBe(false);
  });

  it('сообщение с « · » внутри себя остаётся целым (разбор — не деление по « · »)', async () => {
    mockHealth(HEALTH);
    const c = render();
    await settle();
    expect(has(c, 'файл изменён · сверка с манифестом не сошлась')).toBe(true);
  });

  it('счётчики последнего отчёта — по видам, которых он касается', async () => {
    mockHealth(HEALTH);
    const c = render();
    await settle();
    const counters = nodeByTestId(c, 'wiki-health-counters');
    expect(counters).not.toBeNull();
    // плитка вида = метка + число находок этого вида (из отчёта, не выдуманное)
    expect(textNodes(nodeByTestId(c, 'wiki-health-count-dangling')!).join(' ')).toContain('призрак 1');
    expect(textNodes(nodeByTestId(c, 'wiki-health-count-nearDuplicate')!).join(' ')).toContain('почти дубль 1');
    // «Повышение» — не счётчик находок: оно своим блоком, без плитки в ряду
    expect(nodeByTestId(c, 'wiki-health-count-promote')).toBeNull();
    // динамики метрик нет: честная пометка вместо выдуманных чисел
    expect(has(c, 'Динамика метрик — позже')).toBe(true);
  });

  it('предложение повышения — только подсказка: блока «Просится наверх» хватает, кнопки нет (Ф2)', async () => {
    mockHealth(HEALTH);
    const c = render();
    await settle();
    expect(nodeByTestId(c, 'wiki-health-promote')).not.toBeNull();
    expect(has(c, 'Просится наверх')).toBe(true);
    expect(has(c, 'та же сущность в двух проектах')).toBe(true);
    // кнопки «Повысить» нигде на экране нет: повышение — Ф2
    expect(buttonsText(c).some(t => t.includes('Повысить'))).toBe(false);
  });

  it('отчёта нет (404) — «Нет данных» с подсказкой прогнать проверку, а не пустой экран', async () => {
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/health/')) return Promise.reject({ status: 404, body: undefined });
      return Promise.resolve(null);
    });
    const c = render();
    await settle();
    expect(has(c, 'Нет данных')).toBe(true);
    expect(has(c, 'Отчёта по этому разделу ещё нет')).toBe(true);
    expect(rows(c)).toHaveLength(0);
    expect(nodeByTestId(c, 'wiki-health-run')).not.toBeNull();
  });

  it('409 «функция выключена» — русским текстом, прежний отчёт не затёрт, сырого кода нет', async () => {
    mockHealth(HEALTH);
    const c = render();
    await settle();
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') {
        return Promise.reject({ status: 409, body: { code: 'wiki_llm_wiki_off', message: 'проверка не выполнена: функция выключена' } });
      }
      if (url.startsWith('/wiki/health/')) return Promise.resolve({ section: 'projects/p1', content: HEALTH });
      return Promise.resolve(null);
    });
    clickButton(c, 'wiki-health-run');
    await settle();
    expect(has(c, 'функция «Вики» выключена')).toBe(true);
    expect(has(c, 'wiki_llm_wiki_off')).toBe(false);
    // отказ — не «Находки (0)»: прежний отчёт остаётся на экране
    expect(rows(c)).toHaveLength(10);
    expect(has(c, 'Находок нет')).toBe(false);
  });

  it('пока проверка в пути, кнопка заблокирована (урок S5b/S5e/S7c-1)', async () => {
    mockHealth(HEALTH);
    const c = render();
    await settle();
    let resolvePost: (v: unknown) => void = () => {};
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') return new Promise<unknown>(res => { resolvePost = res; });
      if (url.startsWith('/wiki/health/')) return Promise.resolve({ section: 'projects/p1', content: HEALTH });
      return Promise.resolve(null);
    });
    clickButton(c, 'wiki-health-run');
    await settle();
    const btn = nodeByTestId(c, 'wiki-health-run')!;
    expect((reactProps(btn) as { disabled?: boolean }).disabled).toBe(true);
    // POST ещё висит: свой отчёт ещё не перечитан
    expect(req.mock.calls.filter(([, init]) => (init as { method?: string } | undefined)?.method === 'POST')).toHaveLength(1);
    resolvePost({ ran: true, sectionsOk: 1, sectionsFailed: 0 });
    await settle();
    // прогон закончен: кнопка снова активна, отчёт перечитан
    expect((reactProps(nodeByTestId(c, 'wiki-health-run')!) as { disabled?: boolean }).disabled).toBe(false);
    expect(rows(c)).toHaveLength(10);
  });

  it('запоздалый ответ прежней загрузки не затирает свежий отчёт (поколение запросов)', async () => {
    let resolveOld: (v: unknown) => void = () => {};
    let n = 0;
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') return Promise.resolve({ ran: true, sectionsOk: 1, sectionsFailed: 0 });
      if (url.startsWith('/wiki/health/')) {
        n++;
        // первый GET (при открытии) висит; второй (после прогона) отвечает сразу и свежо
        if (n === 1) return new Promise(res => { resolveOld = res; });
        return Promise.resolve({ section: 'projects/p1', content: HEALTH.replace('ссылка ведёт в пустоту', 'СВЕЖИЙ-ОТЧЕТ') });
      }
      return Promise.resolve(null);
    });
    const c = render();
    await settle();
    expect(rows(c)).toHaveLength(0);
    clickButton(c, 'wiki-health-run');
    await settle();
    expect(has(c, 'СВЕЖИЙ-ОТЧЕТ')).toBe(true);
    // запоздалый ответ первой загрузки приходит ПОСЛЕ свежего: не затрёт его
    resolveOld({ section: 'projects/p1', content: HEALTH });
    await settle();
    expect(has(c, 'СВЕЖИЙ-ОТЧЕТ')).toBe(true);
    expect(has(c, 'ссылка ведёт в пустоту')).toBe(false);
  });

  it('смена раздела A→B: при загрузке B находок A нет; при отказе B (500) — находок A нет, ошибка показана (доработка S5f)', async () => {
    let rejectB: (e: unknown) => void = () => {};
    req.mockImplementation((url: string) => {
      if (url.includes('/wiki/health/projects/p1')) return Promise.resolve({ section: 'projects/p1', content: HEALTH });
      // ответ B намеренно висит: проверяем экран ДО него и ПОСЛЕ его отказа
      if (url.includes('/wiki/health/projects/p2')) return new Promise<unknown>((_res, rej) => { rejectB = rej; });
      return Promise.resolve(null);
    });
    const { root, container: c } = mountAt('projects/p1');
    await settle();
    // отчёт раздела A на экране — именно он обязан уйти при переключении
    expect(rows(c)).toHaveLength(10);
    expect(has(c, 'ссылка ведёт в пустоту')).toBe(true);
    // переключаем раздел на том же экземпляре (так же, что делает wikiTabs): ответ B ещё в пути
    selectSection(root, 'projects/p2');
    await settle();
    // пока ответа B нет — ни одной строки находок A: под шапкой B не должно быть чужого отчёта
    expect(rows(c)).toHaveLength(0);
    expect(has(c, 'ссылка ведёт в пустоту')).toBe(false);
    expect(has(c, 'Не удалось выполнить действие')).toBe(false);
    // B отвечает отказом 500: показана ошибка, а не прежнее молчание и не находки A
    rejectB({ status: 500, body: undefined });
    await settle();
    expect(rows(c)).toHaveLength(0);
    expect(has(c, 'ссылка ведёт в пустоту')).toBe(false);
    expect(has(c, 'Не удалось выполнить действие')).toBe(true);
  });

  it('запоздалый ответ раздела A после переключения на B не появляется на экране (поколение остаётся)', async () => {
    let resolveA: (v: unknown) => void = () => {};
    req.mockImplementation((url: string) => {
      if (url.includes('/wiki/health/projects/p1')) return new Promise(res => { resolveA = res; });
      if (url.includes('/wiki/health/projects/p2')) {
        return Promise.resolve({ section: 'projects/p2', content: HEALTH.replace('ссылка ведёт в пустоту', 'ОТВЕТ-B') });
      }
      return Promise.resolve(null);
    });
    const { root, container: c } = mountAt('projects/p1');
    await settle();
    // ответ A ещё висит — экрана с находками A не было
    expect(rows(c)).toHaveLength(0);
    selectSection(root, 'projects/p2');
    await settle();
    // ответ B пришёл — его отчёт на экране
    expect(has(c, 'ОТВЕТ-B')).toBe(true);
    // ответ A приходит ПОСЛЕ переключения: не должен вернуться под шапку B
    resolveA({ section: 'projects/p1', content: HEALTH });
    await settle();
    expect(has(c, 'ссылка ведёт в пустоту')).toBe(false);
    expect(has(c, 'ОТВЕТ-B')).toBe(true);
  });

  it('«## Отказ» прогона — названный отказ, а не «Находок нет»', async () => {
    mockHealth([
      '# Здоровье раздела «projects/p1»',
      '',
      'Проверено: 2026-10-31T03:00:00.0000000+00:00. Код: v1.',
      '',
      '## Находки (0)',
      '',
      '## Отказ',
      '- читатель раздела не ответил',
    ].join('\n'));
    const c = render();
    await settle();
    expect(has(c, 'Проверка этого раздела не завершилась')).toBe(true);
    expect(has(c, 'читатель раздела не ответил')).toBe(true);
    expect(has(c, 'Находок нет')).toBe(false);
  });

  it('чужая метка вида (в т. ч. имя из прототипа) — вид unknown, строка остаётся как есть', async () => {
    const r = parseHealth([
      '## Находки (3)',
      '- constructor · страница `a.md` · странная',
      '- toString · раздел · ещё',
      '- ВидИзБудущего · страница `b.md` · новая',
    ].join('\n'));
    expect(r.findings.map(f => f.kind)).toEqual(['unknown', 'unknown', 'unknown']);
    expect(r.findings.map(f => f.kindLabel)).toEqual(['constructor', 'toString', 'ВидИзБудущего']);
    mockHealth(['## Находки (1)', '- constructor · страница `a.md` · странная'].join('\n'));
    const c = render();
    await settle();
    expect(rows(c)).toHaveLength(1);
    expect(has(c, 'constructor')).toBe(true);
  });

  it('«Находки (N)» не равны числу строк — предупреждение; равны — молчание', async () => {
    mockHealth(HEALTH.replace('## Находки (10)', '## Находки (14)'));
    const c = render();
    await settle();
    expect(nodeByTestId(c, 'wiki-health-mismatch')).not.toBeNull();
    expect(has(c, 'указано находок: 14, в списке строк: 10')).toBe(true);
  });

  it('число в шапке сходится со строками — предупреждения нет', async () => {
    mockHealth(HEALTH);
    const c = render();
    await settle();
    expect(nodeByTestId(c, 'wiki-health-mismatch')).toBeNull();
  });

  it('счёт подписан по виду: дни у «без изменений», расстояние у дубля, у упоминания (всегда 1) — скрыт', () => {
    const r = parseHealth([
      '## Находки (3)',
      '- без изменений · страница `a.md` · 120 дн. · счёт: 120',
      '- почти дубль · раздел · близкие заголовки · связанные: `a.md`, `b.md` · счёт: 2',
      '- упоминание без ссылки · страница `c.md` · м · связанные: `d.md` · ключ: `Д` · счёт: 1',
    ].join('\n'));
    expect(r.findings.map(f => f.count)).toEqual([120, 2, 1]);
  });

  it('счёт подписан по виду на экране', async () => {
    mockHealth([
      '## Находки (3)',
      '- без изменений · страница `a.md` · давно · счёт: 120',
      '- почти дубль · раздел · близко · счёт: 2',
      '- упоминание без ссылки · страница `c.md` · м · счёт: 1',
    ].join('\n'));
    const c = render();
    await settle();
    expect(has(c, 'дней без изменений: 120')).toBe(true);
    expect(has(c, 'различий в заголовках: 2')).toBe(true);
    // у упоминания счёт всегда 1 — ничего не добавляет, не показан вовсе
    expect(has(c, 'счёт: 1')).toBe(false);
    expect(has(c, 'страниц: 1')).toBe(false);
  });

  it('404 после успешного прогона — «Нет данных», а не отказ проверки', async () => {
    mockHealth(HEALTH);
    const c = render();
    await settle();
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') return Promise.resolve({ ran: true, sectionsOk: 1, sectionsFailed: 0 });
      if (url.startsWith('/wiki/health/')) return Promise.reject({ status: 404, body: undefined });
      return Promise.resolve(null);
    });
    clickButton(c, 'wiki-health-run');
    await settle();
    expect(has(c, 'Нет данных')).toBe(true);
    expect(has(c, 'Не удалось начать проверку')).toBe(false);
    expect(has(c, 'Не найдено')).toBe(false);
    expect(rows(c)).toHaveLength(0);
  });

  it('404 самого прогона — по-прежнему отказ, прежний отчёт не затёрт', async () => {
    mockHealth(HEALTH);
    const c = render();
    await settle();
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') return Promise.reject({ status: 404, body: undefined });
      return Promise.resolve({ section: 'projects/p1', content: HEALTH });
    });
    clickButton(c, 'wiki-health-run');
    await settle();
    expect(has(c, 'Нет данных')).toBe(false);
    expect(rows(c)).toHaveLength(10);
  });

  it('непроверенные страницы («## Не проверено») названы; «Находок нет» не пишется', async () => {
    const content = [
      '# Здоровье раздела «projects/p1»',
      '',
      'Проверено: 2026-10-31T03:00:00.0000000+00:00. Код: v1.',
      '',
      '## Находки (0)',
      '',
      '## Не проверено',
      '- страниц без фактов (находки «нет источников» и «без изменений» их не видят): 7',
    ].join('\n');
    expect(parseHealth(content).unchecked).toBe(7);
    mockHealth(content);
    const c = render();
    await settle();
    expect(nodeByTestId(c, 'wiki-health-unchecked')).not.toBeNull();
    expect(has(c, 'Не проверено страниц без фактов: 7')).toBe(true);
    expect(has(c, 'Среди проверенных страниц находок нет')).toBe(true);
    expect(has(c, 'ничего не нашла')).toBe(false);
    expect(has(c, 'Находок нет')).toBe(false);
  });

  it('нет непроверенных страниц — прежнее «Находок нет»', async () => {
    mockHealth('## Находки (0)\n');
    const c = render();
    await settle();
    expect(parseHealth('## Находки (0)\n').unchecked).toBeNull();
    expect(has(c, 'Находок нет')).toBe(true);
    expect(nodeByTestId(c, 'wiki-health-unchecked')).toBeNull();
  });

  it('плашка отказа не тащит за собой пустые счётчики и «динамику»', async () => {
    mockHealth(['## Находки (0)', '', '## Отказ', '- читатель раздела не ответил'].join('\n'));
    const c = render();
    await settle();
    expect(has(c, 'Проверка этого раздела не завершилась')).toBe(true);
    expect(nodeByTestId(c, 'wiki-health-counters')).toBeNull();
    expect(has(c, 'Динамика метрик')).toBe(false);
  });

  it('«нет данных» говорит, что проверка идёт по всей вики', async () => {
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/health/')) return Promise.reject({ status: 404, body: undefined });
      return Promise.resolve(null);
    });
    const c = render();
    await settle();
    expect(has(c, 'проверяет всю вики')).toBe(true);
  });

  it('разбор отчёта: строка «Проверено:» без хвоста «Код:», число из шапки, все строки находок', () => {
    const r = parseHealth(HEALTH);
    expect(r.checkedAt).toBe('2026-10-31T03:00:00.0000000+00:00');
    expect(r.total).toBe(10);
    expect(r.findings).toHaveLength(10);
    expect(r.failure).toBeNull();
  });
});

// ─── «Принять как мою правку» на строке дрейфа (F5c, ADR-038 «Контракт для фронта», §adopt;
// доработка F5c-rw по вердикту R-F5c: без ложных обещаний свежести) ───
// Кнопка — только на строке «дрейфа» с путём страницы и только при обоих флагах; клик —
// подтверждение текстом записки notes-wiki-unified-v1 (§«д. Изменили снаружи», дословно,
// кроме «✎ вы» вместо имени макета); до подтверждения — ни одного запроса. Подтверждение →
// GET page (хэш) → POST /wiki/adopt {section, page, mode:"mine", baseHash}. «Здоровье» —
// отчёт ПОСЛЕДНЕГО прогона линта (бэк его не пересчитывает), поэтому после 200 и после 409
// wiki_page_changed второй ответ здоровья = ТОТ ЖЕ контент: строка «дрейфа» остаётся на
// экране, а текст плашки ведёт к «Проверить сейчас». Страницы фронт нигде не записывает
// (ни одного PUT/PATCH): ровно один POST /wiki/adopt и одна плашка исхода. Отказы — Notice
// русским текстом по коду: 409 wiki_page_changed (ничего не перезаписано), 409 wiki_unified_off,
// 400 wiki_edit_invalid (issues, без них — message бэка). Конкурентность: двойной клик —
// один запрос (замок в ref); ответ после смены страницы отбрасывается по ключу «раздел + путь».
//
// Мутации этого блока (вывод — в отчёте задачи):
//  (F1) рендерить кнопку без условия флагов (убрать canAdoptMine) → «только при обоих
//      флагах» краснеет;
//  (F2) убрать подтверждение (звать runAdoptMine прямо из кнопки строки) → «до подтверждения
//      ни одного запроса» краснеет;
//  (F3) снять перечитку здоровья после 200 (load() в успехе) → «200 → …» краснеет
//      (проверка f.health.length 2);
//  (F4) не перечитывать здоровье при 409 wiki_page_changed → «409 …» краснеет (та же проверка);
//  (F5) снять замок inFlightRef в начале runAdoptMine → «двойной клик — один запрос»
//      краснеет;
//  (F6) снять проверку ключа пути после await (activeRef в успехе) → «ответ после смены
//      страницы отбрасывается по пути» краснеет;
//  (F7) вернуть обещание свежести в текст плашки (например, «Решение применено.» вместо
//      «Отчёт устарел…») → «409 …» краснеет (проверка точного текста);
//  (F8) вернуть условный вызов флагов — `useFeature(llmWiki) && useFeature(notesWikiUnified)`
//      одной строкой → «переключение флага при открытом „Здоровье“» краснеет: при выключенном
//      первом флага второй хук не вызван, при включении число хуков растёт и React падает
//      («Should have a queue. You are likely calling Hooks conditionally»).

describe('«Принять как мою правку» на строке дрейфа (F5c)', () => {
  it('кнопка — только на строке «дрейфа» и только при обоих флагах', async () => {
    mockHealth(HEALTH);
    const c0 = render();
    await settle();
    expect(adoptBtn(c0), 'при выключенных флагах').toBeNull();
    flags.on.add('llm-wiki');
    const c1 = render();
    await settle();
    expect(adoptBtn(c1), 'при одном llm-wiki').toBeNull();
    bothFlags();
    const c2 = render();
    await settle();
    // ровно одна кнопка на всём экране: в моканом отчёте одна страница с дрейфом,
    // прочие виды находок действия не получают
    const btns = allNodes(c2).filter(n => n.nodeName === 'BUTTON' && textNodes(n).join('').trim() === 'Принять как мою правку');
    expect(btns).toHaveLength(1);
    const row = driftRow(c2);
    expect(row, 'строка дрейфа на экране').not.toBeNull();
    expect(allNodes(row!).some(n => n === btns[0]), 'кнопка — на строке дрейфа').toBe(true);
  });

  it('клик — подтверждение текстом записки; до подтверждения ни одного запроса', async () => {
    bothFlags();
    mockHealth(HEALTH);
    const c = render();
    await settle();
    click(adoptBtn(c), 'Принять как мою правку');
    const body = docText();
    expect(body).toContain('Принять как мою правку?');
    expect(body).toContain('Как мою правку — изменённые места станут вашими (✎ вы). Как есть — текст останется, а у этих мест будет пометка «изменено снаружи».');
    expect(confirmBtn(), 'кнопка подтверждения').not.toBeNull();
    // страница не читана и ничего не послано: сначала подтверждение
    expect(req.mock.calls.filter(([u]) => String(u).startsWith('/wiki/page?'))).toHaveLength(0);
    expect(req.mock.calls.filter(([, init]) => (init as { method?: string } | undefined)?.method === 'POST')).toHaveLength(0);
  });

  it('200 → одна плашка с честным текстом; строка «дрейфа» остаётся (бэк отчёт не пересчитывает), страница не записана', async () => {
    bothFlags();
    // второй ответ здоровья = ТОТ ЖЕ контент: GET health отдаёт _health.md как есть
    const f = mockAdoptFlow(HEALTH);
    const c = render();
    await settle();
    click(adoptBtn(c), 'кнопка строки');
    click(confirmBtn(), 'подтверждение');
    // поток двухшаговый: сначала GET page (хэш), и только после него POST adopt — до
    // разрешения/отказа надо довести микротaskи до начала POST
    await settle();
    f.resolve({ status: 'applied', hash: 'h-new' });
    await settle();
    // тело запроса: раздел и путь страницы, режим mine, baseHash — хэш только что прочитанного
    // файла (единственный источник хэша на этом экране)
    expect(f.post).toHaveLength(1);
    expect(f.post[0].body).toEqual({ section: 'projects/p1', page: 'Проекты/p1/Д.md', mode: 'mine', baseHash: 'h-base' });
    // страница нигде не записана: ни одного PUT/PATCH — единственный запрос на запись это POST adopt
    expect(req.mock.calls.filter(([, init]) => {
      const m = (init as { method?: string } | undefined)?.method ?? '';
      return m === 'PUT' || m === 'PATCH';
    }), 'записи страницы').toHaveLength(0);
    const view = texts(c);
    expect(view).toContain('Принято как ваша правка');
    expect(view).toContain('Строка «дрейфа» останется в отчёте до следующей проверки — нажмите «Проверить сейчас».');
    // ровно одна плашка исхода
    expect(notice(c), 'плашка исхода').not.toBeNull();
    expect(allNodes(c).filter(n => n.getAttribute('data-testid') === 'wiki-health-adopt-mine-flash')).toHaveLength(1);
    // здоровье перечитано (разрешено задачей), но перечитан = то же: бэк не пересчитывает
    // отчёт последнего прогона — строка «дрейфа» остаётся на экране до следующей проверки
    expect(f.health).toHaveLength(2);
    expect(view).toContain('файл изменён · сверка с манифестом не сошлась');
    // подтверждение закрыто исходом
    expect(docText()).not.toContain('Принять как мою правку?');
  });

  it('409 wiki_page_changed — русский текст без обещаний, ничего не перезаписано; строка «дрейфа» остаётся (тот же отчёт)', async () => {
    bothFlags();
    const f = mockAdoptFlow(HEALTH);
    const c = render();
    await settle();
    click(adoptBtn(c), 'кнопка строки');
    click(confirmBtn(), 'подтверждение');
    await settle(); // двухшаговый поток: дождаться начала POST
    // «файл изменился после открытия сравнения»: бэк ничего не написал и вернул свежую версию
    f.reject({ status: 409, body: { code: 'wiki_page_changed', message: 'файл изменился после открытия сравнения', page: { pageKey: 'Проекты/p1/Д.md', content: 'СВЕЖАЯ-ВЕРСИЯ', hash: 'h-new' } } });
    await settle();
    const view = texts(c);
    expect(view).toContain('Страницу поправили ещё раз');
    expect(view).toContain('Страница изменилась. Отчёт устарел — нажмите «Проверить сейчас».');
    expect(view).not.toContain('wiki_page_changed');
    expect(view).not.toContain('Принято как ваша правка');
    // ровно одна плашка исхода
    expect(notice(c), 'плашка исхода').not.toBeNull();
    expect(allNodes(c).filter(n => n.getAttribute('data-testid') === 'wiki-health-adopt-mine-flash')).toHaveLength(1);
    // свежая страница из ответа никуда не вставлена: ничего не перезаписано
    expect(view).not.toContain('СВЕЖАЯ-ВЕРСИЯ');
    expect(req.mock.calls.filter(([, init]) => {
      const m = (init as { method?: string } | undefined)?.method ?? '';
      return m === 'PUT' || m === 'PATCH';
    }), 'записи страницы').toHaveLength(0);
    // здоровье перечитано (разрешено задачей), но перечитан = то же: строка «дрейфа»
    // остаётся на экране до следующей проверки — обещать её снятие здесь нельзя
    expect(f.health).toHaveLength(2);
    expect(view).toContain('файл изменён · сверка с манифестом не сошлась');
  });

  it('409 wiki_unified_off — русский текст по коду, без сырого кода; здоровье не перечитано', async () => {
    bothFlags();
    const f = mockAdoptFlow(HEALTH);
    const c = render();
    await settle();
    click(adoptBtn(c), 'кнопка строки');
    click(confirmBtn(), 'подтверждение');
    await settle(); // двухшаговый поток: дождаться начала POST
    f.reject({ status: 409, body: { code: 'wiki_unified_off', message: 'правка страниц выключена: функция недоступна' } });
    await settle();
    expect(texts(c)).toContain('Правка страниц сейчас выключена');
    expect(texts(c)).not.toContain('wiki_unified_off');
    // этот отказ — не про изменённый файл: своей плашки-текста про «отчёт устарел» нет
    expect(texts(c)).not.toContain('Отчёт устарел');
    expect(f.health, 'этот отказ — не про изменённый файл: перечитки нет').toHaveLength(1);
  });

  it('400 wiki_edit_invalid — причины (issues), а без них — message бэка', async () => {
    bothFlags();
    const f1 = mockAdoptFlow(HEALTH);
    const c1 = render();
    await settle();
    click(adoptBtn(c1), 'кнопка строки');
    click(confirmBtn(), 'подтверждение');
    await settle(); // двухшаговый поток: дождаться начала POST
    f1.reject({ status: 400, body: { code: 'wiki_edit_invalid', message: 'версия страницы из манифеста не сохранена: сравнить нечем. Примите расхождение как есть…', issues: [{ code: 'wiki_parse', message: 'текст не удалось разобрать как страницу' }] } });
    await settle();
    expect(texts(c1)).toContain('текст не удалось разобрать как страницу');
    expect(texts(c1)).not.toContain('wiki_edit_invalid');

    // без issues (старый бэк) — message как есть
    const f2 = mockAdoptFlow(HEALTH);
    const c2 = render();
    await settle();
    click(adoptBtn(c2), 'кнопка строки');
    click(confirmBtn(), 'подтверждение');
    await settle(); // двухшаговый поток: дождаться начала POST
    f2.reject({ status: 400, body: { code: 'wiki_edit_invalid', message: 'версия страницы из манифеста не сохранена: сравнить нечем. Примите расхождение как есть…', issues: [] } });
    await settle();
    expect(texts(c2)).toContain('версия страницы из манифеста не сохранена: сравнить нечем. Примите расхождение как есть…');
  });

  it('двойной клик по подтверждению — один запрос', async () => {
    bothFlags();
    const f = mockAdoptFlow(HEALTH);
    const c = render();
    await settle();
    click(adoptBtn(c), 'кнопка строки');
    const confirm = confirmBtn();
    // настоящий двойной клик — два клика ДО перерисовки: помощник click() disabled-кнопку
    // не кликает, поэтому пара вызовов — одним act(), а не два click() подряд
    const p = reactProps(confirm) as { onClick?: (e: unknown) => void };
    act(() => { p.onClick!(undefined); p.onClick!(undefined); });
    await settle(); // двухшаговый поток: GET page → POST — первый запрос реально начался
    expect(f.post, 'второй клик заблокирован замком в ref').toHaveLength(1);
    // пока запрос идёт, кнопка строки выключена (DOM-атрибут)
    expect(attrs(adoptBtn(c)).disabled, 'на время запроса').not.toBeUndefined();
    f.resolve({ status: 'applied', hash: 'h-new' });
    await settle();
    expect(f.post).toHaveLength(1);
  });

  it('ответ после смены страницы отбрасывается по пути: ни плашки, ни перечитки прежнего раздела', async () => {
    bothFlags();
    const f = mockAdoptFlow(HEALTH);
    const { root, container: c } = mountAt('projects/p1');
    await settle();
    click(adoptBtn(c), 'кнопка строки');
    click(confirmBtn(), 'подтверждение');
    await settle(); // двухшаговый поток: дождаться начала POST
    // пока запрос шёл, пользователь переключился на другой раздел (так же, что делает wikiTabs)
    selectSection(root, 'projects/p2');
    await settle();
    f.resolve({ status: 'applied', hash: 'h-new' });
    await settle();
    const view = texts(c);
    expect(view, 'ответ чужой страницы не показал плашки').not.toContain('Принято как ваша правка');
    // здоровья раздела A перечитано не было: отброшенный ответ не вызвал перечитки
    expect(f.health.filter(h => h.url.includes('/wiki/health/projects/p1')), 'перечитки прежнего раздела').toHaveLength(1);
    // подтверждение прежней страницы не перешло в новый раздел
    expect(docText()).not.toContain('Принять как мою правку?');
  });

  it('переключение флага при открытом «Здоровье» не роняет экран: оба useFeature безусловны (F5c-rw2)', async () => {
    mockHealth(HEALTH);
    const { root, container: c } = mountAt('projects/p1');
    await settle();
    // старт с выключенными флагами: действия нет, экран живой
    expect(adoptBtn(c), 'при выключенных флагах').toBeNull();
    // пользователь включает оба флага при открытом «Здоровье»: перерисовка того же
    // экземпляра с теми же props (тот же путь, что делает родитель при ререндере)
    bothFlags();
    act(() => { root.render(createElement(WikiHealth, { selectedKey: 'projects/p1' })); });
    await settle();
    // экран не упал (число хуков не изменилось) и действие появилось
    expect(adoptBtn(c), 'после включения флагов').not.toBeNull();
    // и обратно: выключение флага на том же экземпляре — тоже без падения, действие исчезло
    flags.on.clear();
    act(() => { root.render(createElement(WikiHealth, { selectedKey: 'projects/p1' })); });
    await settle();
    expect(adoptBtn(c), 'после выключения флагов').toBeNull();
  });
});

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
// Мутации этого прогона (вывод — в отчёте задачи):
//  (1) убрать «предложение повышения» из таблицы видов → «каждый вид своей строкой»
//      краснеет (10 строк не хватает);
//  (2) делить строку находки по « · » целиком (не снимать хвост с конца) →
//      «сообщение с точками внутри себя» краснеет;
//  (3) снять проверку seq в загрузке → «запоздалый ответ» краснеет;
//  (4) снять disabled={busy} у кнопки → «блокировка на время запроса» краснеет;
//  (5) не сбрасывать report в начале load (доработка S5f) → «при смене раздела A→B…» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiHealth, parseHealth } from '../WikiHealth';

vi.mock('../../../../lib/signalr', () => ({
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));
vi.mock('../../../../lib/offline', () => ({
  request: vi.fn(),
  readStoredUserId: vi.fn(() => 'u1'),
}));

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

beforeEach(() => {
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
    expect(has(c, 'счёт: 2')).toBe(true);
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
    expect(has(c, 'ещё не проверялся')).toBe(true);
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

  it('разбор отчёта: строка «Проверено:» без хвоста «Код:», число из шапки, все строки находок', () => {
    const r = parseHealth(HEALTH);
    expect(r.checkedAt).toBe('2026-10-31T03:00:00.0000000+00:00');
    expect(r.total).toBe(10);
    expect(r.findings).toHaveLength(10);
    expect(r.failure).toBeNull();
  });
});

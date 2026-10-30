// Рендер-тест страницы вики (S5b): обычная страница — четыре секции в фиксированном
// порядке; хроника — «О чём» строкой-описанием + три свои секции; сноска — маркер в тексте
// и блок «Источники» из определений; дрейф — плашка + «Принять как есть» (только при
// drift; блокируется на время запроса; ошибка называется); сирота — плашка «проект удалён»;
// на странице нет ни одного контрола правки текста (критерий c3).
//
// Стор S5a НЕ мокан: мокан только его транспорт (offline.request) и signalr — adopt идёт
// по настоящему пути lib/wiki → request (тот же приём, что в WikiTab.test).
//
// Мутации (проверены прогоном, вывод — в отчёте задачи):
//  (1) не рендерить секцию «История» → «четыре секции в порядке» краснеет;
//  (2) не убирать плашку после успешного adopt → «успешный принимается» краснеет;
//  (3) не показывать блок «Источники» → «блок из сносок» краснеет.

import { act } from 'react';
import { createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiPageView } from '../WikiPageView';
import { __test as wikiStore } from '../../../../lib/wiki';

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

const SECTION = {
  key: 'projects/p1', name: 'AI Home', orphan: false, local: false, sphereReserved: false, pageCount: 5,
};
const ORPHAN_SECTION = { ...SECTION, key: 'projects/dead', name: null, orphan: true };
const PAGE_KEY = 'projects/p1/gleb';

// Обычная страница — как её пишет Хранитель (5 сносок — та же плотность, что в c4)
const PAGE_PLAIN = `---
type: entity
kind: person
summary: Ревьюер AI Home: находки по severity, фронт и звук
aliases: [Глеб, @gleb]
sources: [карточка-1, карточка-2, карточка-3, карточка-4, карточка-5]
updated: 2026-10-27
status: active
---

# Глеб (ревьюер)

## Сводка
- Ведёт код-ревью AI Home: находки по severity, соглашения проекта.[^s1]
- Закрывает ревью фронта и звука до пятницы.[^s2]
- Ревью заметного UI идёт после прогона субагента.[^s3]

## Хронология
- 01.09.2026 Взял ревью линии «Персоны-Fold».[^s1]
- 20.10.2026 Принял ревью звука.[^s2]

## Противоречия
- Кто ревьюит звук: источник говорит «Глеб».[^s2]

## История
- 01.09.2026 → 20.10.2026 Глеб ревьюит только бэкенд.[^s4] · заменено[^s5]

[^s1]: [[Чат «Ревью фронта»]] — «Глеб ведёт код-ревью фронта с августа»
[^s2]: [[Чат «Ревью звука»]] — «закрываю ревью фронта и звука до пятницы»
[^s3]: [[Задача «Ревью UI»]] — «сначала прогон designer, потом ревью»
[^s4]: [[Досье «волна 1»]] — «Глеб ревьюит только бэкенд»
[^s5]: [[Чат «Ревью звука»]] — «звук тоже на Глебе»`;

// Та же страница в дрейфе: шапка status: drift — единственный канал дрейфа для фронта
// (манифест бэк не отдаёт; статус в шапке — тот же источник, что у каталога)
const PAGE_DRIFT = PAGE_PLAIN.replace('status: active', 'status: drift');

const PAGE_CHRONICLE = `---
type: chronicle
summary: Кто берёт ревью звука
sources: [карточка-1]
updated: 2026-10-23
status: active
---

# Ревью звука · 20.10.2026

## О чём
Кто берёт ревью звука, пока Вера на QA редактора видео.

## Что решили
- Глеб закрывает ревью фронта и звука до пятницы.[^s1]
- Вера ведёт QA редактора видео.[^s1]

## Что выяснили
- Без отдельного ревьюера находки по звуку копились две недели.[^s1]

## Открытые вопросы
- Кто ревьюит звук после пятницы — не решили.

[^s1]: [[Чат «Ревью звука»]] — «закрываю ревью фронта и звука до пятницы»`;

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

const roots: Root[] = [];

function renderPage(content: string, section = SECTION, onBack = () => {}): Cont {
  installFakeDom();
  const { root, container } = mount(
    createElement(WikiPageView, { section, pageKey: PAGE_KEY, content, onBack }),
  );
  roots.push(root);
  return container;
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

function allNodes(root: AnyNode, out: AnyNode[] = []): AnyNode[] {
  out.push(root);
  for (const c of root.childNodes) allNodes(c, out);
  return out;
}

function buttonByText(root: AnyNode, text: string): AnyNode | null {
  return allNodes(root).find(n => n.nodeName === 'BUTTON' && textNodes(n).join('').includes(text)) ?? null;
}

function click(node: AnyNode | null, what: string) {
  if (!node) throw new Error(`нет узла: ${what}`);
  const props = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!props?.onClick) throw new Error(`у «${what}» нет onClick`);
  act(() => { props.onClick!(undefined); });
}

// Порядок секций на экране — по обходу DOM, не по тексту: порядок — свойство разметки
function sectionNames(container: Cont): string[] {
  return allNodes(container)
    .filter(n => n.getAttribute('data-testid') === 'wiki-section')
    .map(n => String(n.getAttribute('data-section-name')));
}

beforeEach(() => {
  wikiStore.reset();
  req.mockReset();
  req.mockImplementation(() => Promise.resolve(null));
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  uninstallFakeDom();
});

describe('Страница вики (S5b)', () => {
  it('обычная страница: заголовок, мета, плашка «только чтение», четыре секции в порядке', async () => {
    const c = renderPage(PAGE_PLAIN);
    await settle();
    expect(has(c, 'Глеб (ревьюер)')).toBe(true);
    expect(has(c, 'Ревьюер AI Home: находки по severity, фронт и звук')).toBe(true);
    expect(has(c, 'Проекты / AI Home /')).toBe(true);
    expect(has(c, 'обновлено 27.10.2026')).toBe(true);
    expect(has(c, '5 ист.')).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-readonly')).not.toBeNull();
    expect(has(c, 'Только чтение')).toBe(true);
    // Фиксированный порядок — как в файле и как требует ADR-035 (решение 3)
    expect(sectionNames(c)).toEqual(['Сводка', 'Хронология', 'Противоречия', 'История']);
    // status: active — кнопки решения дрейфа нет
    expect(buttonByText(c, 'Принять как есть')).toBeNull();
  });

  it('сноски: маркеры в тексте вместо [^sN], «Источники» из определений сносок', async () => {
    const c = renderPage(PAGE_PLAIN);
    await settle();
    // сырой маркер в тексте не остаётся — он становится надстрочником
    expect(has(c, '[^s1]')).toBe(false);
    expect(has(c, 's1')).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-sources')).not.toBeNull();
    expect(has(c, 'Чат «Ревью фронта»')).toBe(true);
    expect(has(c, 'закрываю ревью фронта и звука до пятницы')).toBe(true);
    expect(has(c, 'Блок собирается из сносок')).toBe(true);
  });

  it('хроника: «О чём» описанием и три свои секции; «Сводки» и «Хронологии» нет', async () => {
    const c = renderPage(PAGE_CHRONICLE, SECTION, () => {});
    await settle();
    expect(has(c, 'хроника · чат')).toBe(true);
    expect(has(c, 'О чём:')).toBe(true);
    expect(has(c, 'Кто берёт ревью звука, пока Вера на QA редактора видео.')).toBe(true);
    expect(sectionNames(c)).toEqual(['Что решили', 'Что выяснили', 'Открытые вопросы']);
    expect(has(c, 'Хроника пишется сама')).toBe(true);
    expect(has(c, 'Сводка')).toBe(false);
    expect(has(c, 'Хронология')).toBe(false);
  });

  it('дрейф: плашка и «Принять как есть»; запрос блокирует кнопку; успех снимает плашку', async () => {
    let resolveAdopt: (v: unknown) => void = () => {};
    const adopt = new Promise<unknown>(r => { resolveAdopt = r; });
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') return adopt;
      if (url.startsWith('/api/wiki/tree')) return Promise.resolve({ initialized: true, sections: [] });
      return Promise.resolve(null);
    });
    const c = renderPage(PAGE_DRIFT);
    await settle();
    expect(has(c, 'Расходится с журналом Хранителя')).toBe(true);
    const btn = findNodeByAttr(c, 'data-testid', 'wiki-adopt-btn');
    expect(btn).not.toBeNull();
    click(btn, 'Принять как есть');
    await settle();
    // пока запрос идёт — кнопка блокируется (урок постановки: не повторить нажатие)
    expect(reactProps(btn!)!.disabled).toBe(true);
    resolveAdopt({ status: 'applied' });
    await settle(16);
    const postCalls = req.mock.calls.filter(([, init]) => (init as { method?: string } | undefined)?.method === 'POST');
    expect(postCalls).toHaveLength(1);
    expect(String(postCalls[0][0])).toContain('/api/wiki/adopt');
    // принято — плашки и кнопки нет (файл не меняется, спорить с ответом сервера витрина не будет)
    expect(has(c, 'Расходится с журналом')).toBe(false);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-adopt-btn')).toBeNull();
  });

  it('отказ adopt называется русским текстом, плашка остаётся, сырой код не виден', async () => {
    req.mockImplementation((url: string, init?: { method?: string }) => {
      if (init?.method === 'POST') return Promise.reject({ status: 409, body: { code: 'wiki_refused', message: 'x' } });
      if (url.startsWith('/api/wiki/tree')) return Promise.resolve({ initialized: true, sections: [] });
      return Promise.resolve(null);
    });
    const c = renderPage(PAGE_DRIFT);
    await settle();
    click(findNodeByAttr(c, 'data-testid', 'wiki-adopt-btn'), 'Принять как есть');
    await settle();
    expect(has(c, 'Правку нельзя применить к этому состоянию страницы')).toBe(true);
    expect(has(c, 'wiki_refused')).toBe(false);
    // отказ — дрейф не решён: плашка на месте
    expect(has(c, 'Расходится с журналом Хранителя')).toBe(true);
  });

  it('сирота: плашка «проект удалён»', async () => {
    const c = renderPage(PAGE_PLAIN, ORPHAN_SECTION);
    await settle();
    expect(has(c, 'Проект удалён')).toBe(true);
    expect(has(c, 'Раздел скрыт от всех чатов, агентов и персон')).toBe(true);
  });

  it('«Назад» возвращает к каталогу', async () => {
    let back = 0;
    const c = renderPage(PAGE_PLAIN, SECTION, () => { back += 1; });
    await settle();
    click(findNodeByAttr(c, 'data-testid', 'wiki-page-back'), 'Назад');
    expect(back).toBe(1);
  });

  it('на странице нет ни одного контрола правки текста (c3)', async () => {
    const c = renderPage(PAGE_PLAIN);
    await settle();
    const editors = allNodes(c).filter(n =>
      n.nodeName === 'TEXTAREA' || n.nodeName === 'INPUT' || n.getAttribute('contenteditable') !== null);
    expect(editors).toEqual([]);
  });
});

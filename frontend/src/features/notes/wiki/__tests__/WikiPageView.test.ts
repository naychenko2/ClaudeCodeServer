// Рендер-тест страницы вики (S5b + доработка + хвосты ревью S5b): обычная страница —
// заголовок, мета, плашка «только чтение»; порядок секций на экране — порядок ФАЙЛА,
// нарочно неканоничного в фикстуре (канонический порядок пишет Хранитель, ADR-035
// решение 3 — витрина не переупорядочивает); хроника — «О чём» строкой-описанием + три
// свои секции; сноска — маркер в тексте и блок «Источники» из определений; длинная строка
// без пробелов — стиль переноса задан; неразобранная шапка — плашка и имя файла, а не
// молчаливый pageKey; дрейф — поле ответа бэка (не шапка status): плашка + «Принять как
// есть» только при drift=true, после успешного adopt страница перечитывается и плашка
// уходит по drift=false нового ответа; отказ adopt называется, плашка остаётся; сирота —
// плашка «проект удалён»; на странице нет ни одного контрола правки текста (критерий c3).
//
// Стор S5a НЕ мокан: мокан только его транспорт (offline.request) и signalr — загрузка
// страницы и adopt идут по настоящему пути lib/wiki → request (тот же приём, что в
// WikiTab.test). Экран читает слот «последней прочитанной страницы» (доработка S5b M2),
// поэтому тест начинает с настоящей загрузки: моканный GET /wiki/page → стор → экран.
//
// Мутации этого прогона (вывод — в отчёте задачи):
//  (1) сортировать секции по каноническому порядку → «порядок секций» краснеет;
//  (2) снять overflowWrap у строки секции → «длинная строка» краснеет;
//  (3) убрать плашку нераспознанной шапки → «шапка не разобрана» краснеет;
//  (4) считать разобранной любую закрытую ограду (не проверять тип) → «тип вне семи» краснеет;
//  (5) убрать state из PAGE_TYPE → «шапка rule/term/state разобрана» краснеет (d7ea8ba0).
// Прежние мутации шага S5b (дрейф из поля ответа, перечитывание после adopt) проверялись там.

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { act } from 'react';
import { createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiPageView, parseWikiPage } from '../WikiPageView';
import { ReplacementDot } from '../WikiUnifiedChrome';
import { __test as wikiStore, loadWikiPage, wikiActiveLines, wikiOverridesOf, type WikiPageDto, type WikiPageAuthoredDto, type WikiOverrideDto, type WikiPageOverriddenDto } from '../../../../lib/wiki';
import { FOOTNOTE_TEXT, fmtDayMonth } from '../WikiFootnotePopover';

// Мок с сохранением остальных экспортов: компонент берёт эти модули через кит хоста,
// а цепочка кита шире собственных импортов теста — full-replacement мок рвётся на
// первом недостающем имени. Паттерн — как в NotesPage.test.ts.
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
// блок F3a включает и llm-wiki
const flags = vi.hoisted(() => ({ on: new Set<string>() }));
vi.mock('../../../../lib/featureFlags', async (orig) => ({
  ...await orig<typeof import('../../../../lib/featureFlags')>(),
  useFeature: (k: string) => flags.on.has(k),
}));
const openChat = vi.hoisted(() => vi.fn());
vi.mock('../../../../lib/openChat', async (orig) => ({
  ...await orig<typeof import('../../../../lib/openChat')>(),
  openChatById: openChat,
}));

import { request } from '../../../../lib/offline';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

const SECTION = {
  key: 'projects/p1', name: 'AI Home', orphan: false, local: false, sphereReserved: false, pageCount: 5,
};
const ORPHAN_SECTION = { ...SECTION, key: 'projects/dead', name: null, orphan: true };
const PAGE_KEY = 'projects/p1/gleb';
const ORPHAN_PAGE_KEY = 'projects/dead/gleb';

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

// Та же страница с status: drift в шапке. Шапку пишет модель, и сама по себе она плашку не
// поднимает: сигнал дрейфа — поле ответа бэка (доработка S5b M1)
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

// Та же страница, но секции нарочно НЕ в каноническом порядке (Противоречия и История
// раньше Сводки и Хронологии): так тест видит, что экран отдаёт порядок файла, а не
// пересобирает его по канону (канон пишет Хранитель — ADR-035, решение 3)
const PAGE_SCRAMBLED = `---
type: entity
kind: person
summary: Ревьюер AI Home
updated: 2026-10-27
status: active
---

# Глеб (ревьюер)

## Противоречия
- Кто ревьюит звук: источник говорит «Глеб».

## История
- 01.09.2026 → 20.10.2026 Глеб ревьюит только бэкенд.

## Сводка
- Ведёт код-ревью AI Home.

## Хронология
- 01.09.2026 Взял ревью линии «Персоны-Fold».
`;

// Страница без шапки вовсе (ни ограды, ни заголовка): витрина её всё равно показывает,
// но называет: плашка + имя файла в заголовке вместо молчаливого pageKey
const PAGE_NO_FM = `## Сводка
- Ведёт код-ревью AI Home.
`;

// Ограда есть, но тип вне семи — бэк такую шапку не принимает (WikiFrontmatter.cs:129),
// и витрина не притворяется, что прочитала её
const PAGE_BAD_TYPE = `---
type: mystery
status: active
---

# Загадка

## Сводка
- Типа из семи здесь нет.
`;

// Новые виды синтеза rule/term/state (ADR-039, решение 3): бэк принимает их шапки
// (WikiFrontmatter.cs:33-35), и витрина обязана читать их как разобранные — плашка
// на валидной шапке была дефектом d7ea8ba0. Заголовки и текст страниц не содержат
// подписей словаря — чтобы проверка «подпись на экране» ловила кикер, а не текст страницы.
const PAGE_RULE = `---
type: rule
summary: Порядок, по которому витрина читает шапку
status: active
---

# Чтение шапки

## Сводка
- Ключ словаря — значение поля type.
`;

const PAGE_TERM = `---
type: term
summary: Что значит слово «прочитанная» на этой странице
status: active
---

# Прочитанная

## Сводка
- Так витрина называет шапку с известным типом.
`;

const PAGE_STATE = `---
type: state
as_of: 2026-11-11
summary: Как обстоит дело с видами страниц сейчас
status: active
---

# Виды страниц сейчас

## Сводка
- Бэк знает десять видов, витрина читает все десять.
`;

// Строка из 200 символов без пробелов — и в секции, и вне секций: без заданного переноса
// она расходится за панель (тот же класс, что у заголовка h2 и целей «Источников»)
const LONG_LINE = 'и'.repeat(200);
const PAGE_LONG = `---
type: entity
status: active
---

# Длинная строка

${LONG_LINE}

## Сводка
- ${LONG_LINE}
`;

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

const roots: Root[] = [];

// Мок транспорта и настоящая загрузка страницы в стор: экран читает слот «последней
// прочитанной страницы» (доработка S5b M2), поэтому страница попадает в него тем же
// путём, что в приложении. extra — чем отвечать на прочие вызовы (POST adopt, перечитанный
// ответ), если это нужно тесту; без него страница всегда та же, что в dto
async function seedPage(
  dto: WikiPageDto | WikiPageAuthoredDto,
  sectionKey = SECTION.key,
  pageKey = PAGE_KEY,
  extra?: (url: string, init?: { method?: string }) => unknown,
): Promise<void> {
  req.mockImplementation((url: string, init?: { method?: string }) => {
    const viaExtra = extra?.(url, init);
    if (viaExtra !== undefined) return viaExtra as Promise<unknown>;
    if (url.startsWith('/wiki/page')) return Promise.resolve(dto);
    if (url.startsWith('/wiki/tree')) return Promise.resolve({ initialized: true, sections: [] });
    return Promise.resolve(null);
  });
  await act(async () => { await loadWikiPage(sectionKey, pageKey); });
}

function renderPage(section = SECTION, pageKey = PAGE_KEY, onBack = () => {}): Cont {
  installFakeDom();
  const { root, container } = mount(createElement(WikiPageView, { section, pageKey, onBack }));
  roots.push(root);
  return container;
}

// Было ли перечитывание страницы — видно по числу её GET-запросов
const pageGets = () => req.mock.calls.filter(([u]) => String(u).startsWith('/wiki/page'));

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

// Инлайн-стиль узла — из props React (в fakeDom он не дублируется в атрибут): тест читает
// ровно то, что задал компонент
function styleOf(n: AnyNode): Record<string, string> {
  return ((reactProps(n) ?? {}).style ?? {}) as Record<string, string>;
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
  flags.on.clear();
  openChat.mockReset();
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
  it('обычная страница: заголовок, мета, плашка «только чтение»', async () => {
    await seedPage({ pageKey: PAGE_KEY, content: PAGE_PLAIN });
    const c = renderPage();
    await settle();
    expect(has(c, 'Глеб (ревьюер)')).toBe(true);
    expect(has(c, 'Ревьюер AI Home: находки по severity, фронт и звук')).toBe(true);
    expect(has(c, 'Проекты / AI Home /')).toBe(true);
    expect(has(c, 'обновлено 27.10.2026')).toBe(true);
    expect(has(c, '5 ист.')).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-readonly')).not.toBeNull();
    expect(has(c, 'Только чтение')).toBe(true);
    // Порядок секций здесь не проверяется: фикстура канонична и проверка была бы
    // тавтологией — порядок ловится тестом на неканоничной фикстуре ниже
    expect(has(c, 'Шапка страницы не разобрана')).toBe(false);
    // status: active — кнопки решения дрейфа нет
    expect(buttonByText(c, 'Принять как есть')).toBeNull();
  });

  it('порядок секций — порядок файла, а не канонический (витрина не переупорядочивает)', async () => {
    await seedPage({ pageKey: PAGE_KEY, content: PAGE_SCRAMBLED });
    const c = renderPage();
    await settle();
    // Фикстура нарочно неканонична (Противоречия и История раньше Сводки и Хронологии):
    // экран отдаёт её как в файле — канонический порядок пишет Хранитель (ADR-035,
    // решение 3). Мутация «сортировать по канону» краснит ровно этот тест
    expect(sectionNames(c)).toEqual(['Противоречия', 'История', 'Сводка', 'Хронология']);
  });

  it('шапка не разобрана: плашка и имя файла вместо молчаливого pageKey', async () => {
    await seedPage({ pageKey: PAGE_KEY, content: PAGE_NO_FM });
    const c = renderPage();
    await settle();
    expect(has(c, 'Шапка страницы не разобрана — показано имя файла')).toBe(true);
    // заголовок — имя файла (фолбэк h2), содержимое видно как есть
    expect(has(c, PAGE_KEY)).toBe(true);
    expect(has(c, 'Ведёт код-ревью AI Home.')).toBe(true);
  });

  it('шапка с типом вне семи — тоже не разобрана', async () => {
    await seedPage({ pageKey: PAGE_KEY, content: PAGE_BAD_TYPE });
    const c = renderPage();
    await settle();
    expect(has(c, 'Шапка страницы не разобрана — показано имя файла')).toBe(true);
  });

  // rule/term/state — валидные виды бэка (WikiFrontmatter.cs:33-35): плашка на них —
  // дефект d7ea8ba0. Мутация «убрать state из PAGE_TYPE» краснит ровно этот тест
  it('шапка с типом rule/term/state разобрана: плашки нет, тип назван подписью', async () => {
    for (const [content, label] of [[PAGE_RULE, 'Правило'], [PAGE_TERM, 'Термин'], [PAGE_STATE, 'Состояние']] as const) {
      await seedPage({ pageKey: PAGE_KEY, content });
      const c = renderPage();
      await settle();
      expect(has(c, 'Шапка страницы не разобрана')).toBe(false);
      // кикер называет тип подписью словаря, а не «Страница»
      expect(has(c, label)).toBe(true);
    }
  });

  it('длинная строка без пробелов: перенос задан и в секции, и вне секций', async () => {
    await seedPage({ pageKey: PAGE_KEY, content: PAGE_LONG });
    const c = renderPage();
    await settle();
    const li = allNodes(c).find(n => n.nodeName === 'LI' && textNodes(n).join('').includes(LONG_LINE));
    expect(li).not.toBeNull();
    expect(styleOf(li!).overflowWrap).toBe('anywhere');
    const stray = allNodes(c).find(n =>
      n.nodeName === 'DIV' && n.childNodes.some(ch => ch.nodeType === 3 && ch.nodeValue.includes(LONG_LINE)));
    expect(stray).not.toBeNull();
    expect(styleOf(stray!).overflowWrap).toBe('anywhere');
  });

  it('сноски: маркеры в тексте вместо [^sN], «Источники» из определений сносок', async () => {
    await seedPage({ pageKey: PAGE_KEY, content: PAGE_PLAIN });
    const c = renderPage();
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
    await seedPage({ pageKey: PAGE_KEY, content: PAGE_CHRONICLE });
    const c = renderPage();
    await settle();
    expect(has(c, 'хроника · чат')).toBe(true);
    expect(has(c, 'О чём:')).toBe(true);
    expect(has(c, 'Кто берёт ревью звука, пока Вера на QA редактора видео.')).toBe(true);
    expect(sectionNames(c)).toEqual(['Что решили', 'Что выяснили', 'Открытые вопросы']);
    expect(has(c, 'Хроника пишется сама')).toBe(true);
    expect(has(c, 'Сводка')).toBe(false);
    expect(has(c, 'Хронология')).toBe(false);
  });

  it('дрейф — поле ответа, не шапка: status: drift без drift=true — ни плашки, ни кнопки', async () => {
    await seedPage({ pageKey: PAGE_KEY, content: PAGE_DRIFT, drift: false });
    const c = renderPage();
    await settle();
    expect(has(c, 'Расходится с журналом Хранителя')).toBe(false);
    expect(buttonByText(c, 'Принять как есть')).toBeNull();
  });

  it('дрейф — поле ответа, не шапка: drift=true с обычной шапкой — плашка и кнопка есть', async () => {
    await seedPage({ pageKey: PAGE_KEY, content: PAGE_PLAIN, drift: true });
    const c = renderPage();
    await settle();
    expect(has(c, 'Расходится с журналом Хранителя')).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-adopt-btn')).not.toBeNull();
  });

  it('успешный принимается: страница перечитывается, плашка снимается по drift=false ответа', async () => {
    let accepted = false;
    let resolveAdopt: (v: unknown) => void = () => {};
    const adopt = new Promise<unknown>(r => { resolveAdopt = r; });
    await seedPage(
      { pageKey: PAGE_KEY, content: PAGE_PLAIN, drift: true },
      SECTION.key,
      PAGE_KEY,
      (url, init) => {
        if (init?.method === 'POST') return adopt;
        // После принятого бэк отдаёт ту же страницу без дрейфа: файл == манифест
        if (accepted && url.startsWith('/wiki/page')) {
          return Promise.resolve({ pageKey: PAGE_KEY, content: PAGE_PLAIN, drift: false });
        }
        return undefined;
      },
    );
    const c = renderPage();
    await settle();
    expect(has(c, 'Расходится с журналом Хранителя')).toBe(true);
    const btn = findNodeByAttr(c, 'data-testid', 'wiki-adopt-btn');
    expect(btn).not.toBeNull();
    click(btn, 'Принять как есть');
    await settle();
    // пока запрос идёт — кнопка блокируется (урок постановки: не повторить нажатие)
    expect(reactProps(btn!)!.disabled).toBe(true);
    accepted = true;
    resolveAdopt({ status: 'applied' });
    await settle(16);
    const postCalls = req.mock.calls.filter(([, init]) => (init as { method?: string } | undefined)?.method === 'POST');
    expect(postCalls).toHaveLength(1);
    expect(String(postCalls[0][0])).toContain('/wiki/adopt');
    // Страницу перечитали вторым GET: без перечитывания плашка осталась бы на принятой
    // странице, а «Назад» → открыть снова показал бы дрейф (ревью S5b M2)
    expect(pageGets()).toHaveLength(2);
    // принято — плашки и кнопки нет: их снял ответ бэка, а не локальный флаг витрины
    expect(has(c, 'Расходится с журналом')).toBe(false);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-adopt-btn')).toBeNull();
  });

  it('отказ adopt называется русским текстом, плашка остаётся, страницу не перечитываем', async () => {
    await seedPage(
      { pageKey: PAGE_KEY, content: PAGE_PLAIN, drift: true },
      SECTION.key,
      PAGE_KEY,
      (url, init) => (init?.method === 'POST'
        ? Promise.reject({ status: 409, body: { code: 'wiki_refused', message: 'x' } })
        : undefined),
    );
    const c = renderPage();
    await settle();
    click(findNodeByAttr(c, 'data-testid', 'wiki-adopt-btn'), 'Принять как есть');
    await settle();
    expect(has(c, 'Правку нельзя применить к этому состоянию страницы')).toBe(true);
    expect(has(c, 'wiki_refused')).toBe(false);
    // отказ — дрейф не решён: плашка на месте, перечитывания не было
    expect(has(c, 'Расходится с журналом Хранителя')).toBe(true);
    expect(pageGets()).toHaveLength(1);
  });

  it('сирота: плашка «проект удалён»', async () => {
    await seedPage({ pageKey: ORPHAN_PAGE_KEY, content: PAGE_PLAIN }, ORPHAN_SECTION.key, ORPHAN_PAGE_KEY);
    const c = renderPage(ORPHAN_SECTION, ORPHAN_PAGE_KEY);
    await settle();
    expect(has(c, 'Проект удалён')).toBe(true);
    expect(has(c, 'Раздел скрыт от всех чатов, агентов и персон')).toBe(true);
  });

  it('«Назад» возвращает к каталогу', async () => {
    let back = 0;
    await seedPage({ pageKey: PAGE_KEY, content: PAGE_PLAIN });
    const c = renderPage(SECTION, PAGE_KEY, () => { back += 1; });
    await settle();
    click(findNodeByAttr(c, 'data-testid', 'wiki-page-back'), 'Назад');
    expect(back).toBe(1);
  });

  it('на странице нет ни одного контрола правки текста (c3)', async () => {
    await seedPage({ pageKey: PAGE_KEY, content: PAGE_PLAIN });
    const c = renderPage();
    await settle();
    const editors = allNodes(c).filter(n =>
      n.nodeName === 'TEXTAREA' || n.nodeName === 'INPUT' || n.getAttribute('contenteditable') !== null);
    expect(editors).toEqual([]);
  });
});


// ─── F3a: авторство страницы, сноска ✎ и поповер источника ───
// Мутации этого шага (вывод прогонов — в отчёте задачи):
//  (1) снять проверку ключа в WikiFootnotePopover (ответ применяется без сверки) → «гонка» краснеет;
//  (2) отдавать авторство без флага → «без флага» краснеет;
//  (3) править дословный текст humanBody → «тексты совпадают с ADR» краснеет.

const ADR_PATH = resolve(dirname(fileURLToPath(import.meta.url)),
  '../../../../../../docs/adr/ADR-038-notes-wiki-human-ai-coauthoring.md');

const PAGE_AUTHORED = PAGE_PLAIN;
const FOOTNOTES = [
  { label: 's1', card: 'Ревью фронта', cardPage: 'projects/p1/Источники/Чат Ревью фронта', origin: 'session', captured: '2026-10-28T09:00:00Z' },
  { label: 's2', card: 'Правка человека', cardPage: 'projects/p1/Источники/Правка человека ab12cd34', origin: 'human', captured: '2026-11-02T10:15:00Z', author: 'Андрей' },
  { label: 's3', card: 'Задача Ревью UI', cardPage: 'projects/p1/Источники/Задача Ревью UI', origin: 'note', captured: '2026-10-30T09:00:00Z' },
];
const authored = (over: Partial<WikiPageAuthoredDto> = {}): WikiPageAuthoredDto =>
  ({ pageKey: PAGE_KEY, content: PAGE_AUTHORED, authorship: 'together', footnotes: FOOTNOTES, ...over });

const cardText = (name: string, ref: string) => `---\ntype: source\nref: ${ref}\n---\n\n# ${name}\n`;

function unifiedOn() { flags.on.add('llm-wiki'); }
const plates = (c: Cont) => allNodes(c).filter(n => n.getAttribute('data-testid') === 'wiki-footnote');
const popover = (c: Cont) => findNodeByAttr(c, 'data-testid', 'wiki-footnote-popover');
function press(node: AnyNode | null, what: string) {
  if (!node) throw new Error(`нет узла: ${what}`);
  const p = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!p?.onClick) throw new Error(`у «${what}» нет onClick`);
  act(() => { p.onClick!({ currentTarget: node }); });
}

describe('Страница с авторством (F3a)', () => {
  it('без флага вид прежний: ни плашек, ни бейджа авторства', async () => {
    await seedPage(authored());
    const c = renderPage();
    await settle();
    expect(plates(c)).toHaveLength(0);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-authorship')).toBeNull();
  });

  it('с флагами, но без footnotes в ответе: старые надстрочники, бейдж по умолчанию «Ведёт ИИ»', async () => {
    unifiedOn();
    await seedPage({ pageKey: PAGE_KEY, content: PAGE_AUTHORED });
    const c = renderPage();
    await settle();
    expect(plates(c)).toHaveLength(0);
    expect(has(c, 'Ведёт ИИ')).toBe(true);
  });

  it.each([
    ['ai', 'Ведёт ИИ'], ['together', 'Ведёте вместе'], ['human', 'Написали вы'],
  ] as const)('бейдж авторства %s → «%s»', async (authorship, text) => {
    unifiedOn();
    await seedPage(authored({ authorship }));
    const c = renderPage();
    await settle();
    const b = findNodeByAttr(c, 'data-testid', 'wiki-authorship');
    expect(b).not.toBeNull();
    expect(texts(b!).join(' ')).toBe(text);
  });

  it('сноска ИИ — прежний номер, сноска человека — «✎ имя, дд.мм»', async () => {
    unifiedOn();
    await seedPage(authored());
    const c = renderPage();
    await settle();
    const label = (n: AnyNode) => textNodes(n).join('');
    const all = plates(c).map(label);
    expect(all).toContain('s1');
    expect(all).toContain('✎ Андрей, 02.11');
    expect(fmtDayMonth('2026-11-02T10:15:00Z')).toBe('02.11');
  });

  it('клик по сноске ИИ: цитата, источник из карточки и «Открыть в чате»', async () => {
    unifiedOn();
    await seedPage(authored(), SECTION.key, PAGE_KEY, (url) => {
      if (url.includes(encodeURIComponent(FOOTNOTES[0].cardPage))) {
        return Promise.resolve({ pageKey: FOOTNOTES[0].cardPage, content: cardText('Ревью фронта', 'session:abc123#m1-9') });
      }
      return undefined;
    });
    const c = renderPage();
    await settle();
    press(plates(c).find(n => textNodes(n).join('') === 's1') ?? null, 'сноска s1');
    await settle();
    const pop = popover(c)!;
    expect(pop).not.toBeNull();
    expect(texts(pop).join(' ')).toContain('Сноска 1 · записал ИИ, 28.10');
    expect(texts(pop).join(' ')).toContain('Глеб ведёт код-ревью фронта с августа');
    expect(texts(pop).join(' ')).toContain(FOOTNOTE_TEXT.sourceChat('Ревью фронта'));
    click(findNodeByAttr(c, 'data-testid', 'wiki-footnote-open-chat'), 'Открыть в чате');
    expect(openChat).toHaveBeenCalledWith('abc123');
    expect(popover(c)).toBeNull();
  });

  it('сноска ИИ без cardPage: «Источник сейчас не прочитать.» без запроса', async () => {
    unifiedOn();
    await seedPage(authored({ footnotes: [{ ...FOOTNOTES[0], cardPage: null }] }));
    const c = renderPage();
    await settle();
    const before = pageGets().length;
    press(plates(c).find(n => textNodes(n).join('') === 's1') ?? null, 'сноска s1');
    await settle();
    expect(texts(findNodeByAttr(c, 'data-testid', 'wiki-footnote-source')!).join(' ')).toBe(FOOTNOTE_TEXT.sourceFailed);
    expect(pageGets().length).toBe(before);
  });

  it('клик по сноске человека: пояснение про замену, без чтения источника', async () => {
    unifiedOn();
    await seedPage(authored());
    const c = renderPage();
    await settle();
    const before = pageGets().length;
    press(plates(c).find(n => textNodes(n).join('').startsWith('✎')) ?? null, 'сноска человека');
    await settle();
    const t = texts(popover(c)!).join(' ');
    expect(t).toContain('Написал Андрей, 02.11');
    expect(t).toContain(FOOTNOTE_TEXT.humanBody);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-footnote-open-chat')).toBeNull();
    expect(pageGets().length).toBe(before);
  });

  it('Esc закрывает поповер, фокус возвращается на сноску', async () => {
    unifiedOn();
    await seedPage(authored());
    const c = renderPage();
    await settle();
    const plate = plates(c).find(n => textNodes(n).join('').startsWith('✎'))!;
    plate.focus();
    press(plate, 'сноска человека');
    await settle();
    const doc = (globalThis as unknown as { document: { activeElement: unknown; listeners: Record<string, Set<(e: unknown) => void>> } }).document;
    expect(doc.activeElement).toBe(popover(c));
    act(() => { doc.listeners.keydown.forEach(f => f({ key: 'Escape', defaultPrevented: false, preventDefault() {} })); });
    expect(popover(c)).toBeNull();
    expect(doc.activeElement).toBe(plate);
  });

  it('гонка: поздний ответ по прежней сноске не подменяет источник новой', async () => {
    unifiedOn();
    let resolveA!: (v: unknown) => void;
    const slowA = new Promise(r => { resolveA = r; });
    await seedPage(authored(), SECTION.key, PAGE_KEY, (url) => {
      if (url.includes(encodeURIComponent(FOOTNOTES[0].cardPage))) return slowA;
      if (url.includes(encodeURIComponent(FOOTNOTES[2].cardPage))) {
        return Promise.resolve({ pageKey: FOOTNOTES[2].cardPage, content: cardText('Задача Ревью UI', 'note:n1') });
      }
      return undefined;
    });
    const c = renderPage();
    await settle();
    press(plates(c).find(n => textNodes(n).join('') === 's1') ?? null, 'сноска s1');
    await settle();
    // не закрывая, открываем другую сноску: подложка перехватила бы клик, но поповер
    // держит открытым кнопка плашки — меняем сноску тем же путём, что и пользователь с клавиатуры
    press(plates(c).find(n => textNodes(n).join('') === 's3') ?? null, 'сноска s3');
    await settle();
    resolveA({ pageKey: FOOTNOTES[0].cardPage, content: cardText('Ревью фронта', 'session:abc123') });
    await settle();
    const src = texts(findNodeByAttr(c, 'data-testid', 'wiki-footnote-source')!).join(' ');
    expect(src).toBe(FOOTNOTE_TEXT.source('Задача Ревью UI'));
    expect(src).not.toContain('Ревью фронта');
  });

  it('тексты поповера совпадают с ADR-038', () => {
    const adr = readFileSync(ADR_PATH, 'utf8');
    expect(adr).toContain(FOOTNOTE_TEXT.humanBody.replace('Ваша правка. ', ''));
    expect(adr).toContain(FOOTNOTE_TEXT.openChat);
  });
});

// ─── Метки «ИИ заменил / оспаривает» и «Вернуть мою» (F3b) ───

const PAGE_OVR = `---
type: entity
status: active
updated: 2026-11-02
---

# Очередь GPU

## Сводка
- Для черновиков снимаем размер 864×480. [^s1]
- Резину меняем в конце октября. [^s2]

## История
- Для черновиков снимаем половинный размер. [^s2]

[^s1]: [[Источники/Чат]] — «размер 864×480»
[^s2]: [[Источники/Правка]] — «половинный размер»
`;
// Страница после «Вернуть мою»: формулировка человека на месте, утверждение ИИ ушло в «Историю»
const PAGE_RESTORED = PAGE_OVR
  .replace('- Для черновиков снимаем размер 864×480. [^s1]', '- Для черновиков снимаем половинный размер. [^s2]')
  .replace('- Для черновиков снимаем половинный размер. [^s2]\n\n[^s1]', '- Для черновиков снимаем размер 864×480. [^s1]\n\n[^s1]');
const OVR_REPLACED: WikiOverrideDto = {
  op: 'supersede', statement: 'Для черновиков снимаем размер 864×480.', previous: 'Для черновиков снимаем половинный размер.',
  at: '2026-11-02T05:48:00Z', packageId: 'pk1',
};
const OVR_DISPUTED: WikiOverrideDto = {
  op: 'conflict', statement: 'Резину меняем в начале ноября.', previous: null, target: 'Резину меняем в конце октября.',
  at: '2026-11-02T05:31:00Z', packageId: 'pk2',
};
const withOvr = (overrides: WikiOverrideDto[], over: Partial<WikiPageOverriddenDto> = {}): WikiPageOverriddenDto =>
  ({ pageKey: PAGE_KEY, content: PAGE_OVR, authorship: 'together', footnotes: FOOTNOTES, hash: 'h1', overrides, ...over });
const tag = (c: Cont) => allNodes(c).filter(n => n.getAttribute('data-testid') === 'wiki-override-tag');
const restoreBtn = (c: Cont) => findNodeByAttr(c, 'data-testid', 'wiki-restore-btn');
const restorePosts = () => req.mock.calls.filter(([u]) => String(u).startsWith('/wiki/pages/restore'));

describe('Метки замен и «Вернуть мою» (F3b)', () => {
  it('без флагов меток и плашек нет, даже если сервер отдал overrides', async () => {
    await seedPage(withOvr([OVR_REPLACED]));
    const c = renderPage();
    await settle();
    expect(tag(c)).toHaveLength(0);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-override-replaced')).toBeNull();
  });

  it('замена: плашка, метка с датой у нового утверждения, подпись у прежней строки «Истории», кнопка', async () => {
    unifiedOn();
    await seedPage(withOvr([OVR_REPLACED]));
    const c = renderPage();
    await settle();
    expect(has(c, 'ИИ заменил вашу формулировку')).toBe(true);
    expect(has(c, 'Прежняя — в «Истории».')).toBe(true);
    const t = tag(c);
    expect(t).toHaveLength(1);
    expect(t[0].getAttribute('data-kind')).toBe('replaced');
    expect(texts(t[0]).join('')).toBe('ИИ заменил вашу формулировку · 02.11');
    expect(styleOf(t[0]).color).toBe('var(--c-info)');
    const cap = findNodeByAttr(c, 'data-testid', 'wiki-history-caption');
    expect(cap).not.toBeNull();
    expect(texts(cap!).join('')).toContain('Ваша формулировка, заменена ИИ');
    expect(restoreBtn(c)).not.toBeNull();
    expect(texts(restoreBtn(c)!).join('')).toContain('Вернуть мою');
  });

  it('previous = null: плашка есть, «Вернуть мою» не предлагается', async () => {
    unifiedOn();
    await seedPage(withOvr([{ ...OVR_REPLACED, previous: null }]));
    const c = renderPage();
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-override-replaced')).not.toBeNull();
    expect(restoreBtn(c)).toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-history-caption')).toBeNull();
  });

  it('спор: плашка-предупреждение и метка «ИИ оспаривает» у утверждения человека, без «Вернуть мою»', async () => {
    unifiedOn();
    await seedPage(withOvr([OVR_DISPUTED]));
    const c = renderPage();
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-override-disputed')).not.toBeNull();
    expect(has(c, 'ИИ оспаривает ваше утверждение')).toBe(true);
    const t = tag(c);
    expect(t).toHaveLength(1);
    expect(t[0].getAttribute('data-kind')).toBe('disputed');
    expect(texts(t[0]).join('')).toBe('⚠ ИИ оспаривает');
    expect(restoreBtn(c)).toBeNull();
  });

  it('окно 14 дней считает сервер: нет в ответе — нет метки (15-й день); есть в ответе — рисуем без своей проверки даты', async () => {
    unifiedOn();
    vi.useFakeTimers({ toFake: ['Date'] });
    try {
      vi.setSystemTime(new Date('2026-11-17T09:00:00Z')); // 15-й день после замены 02.11
      await seedPage(withOvr([]));
      const gone = renderPage();
      await settle();
      expect(tag(gone)).toHaveLength(0);
      expect(findNodeByAttr(gone, 'data-testid', 'wiki-override-replaced')).toBeNull();
      // Те же часы, но сервер замену отдал (его окно шире нашего представления) — фронт её не прячет
      for (const r of roots) act(() => r.unmount());
      roots.length = 0;
      uninstallFakeDom();
      wikiStore.reset();
      await seedPage(withOvr([OVR_REPLACED]));
      const shown = renderPage();
      await settle();
      expect(tag(shown)).toHaveLength(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('«Вернуть мою»: POST с парой statement → current, previous → restore; страница перечитана, плашка снята, сообщение', async () => {
    unifiedOn();
    let restored = false;
    await seedPage(withOvr([OVR_REPLACED]), SECTION.key, PAGE_KEY, (url, init) => {
      if (init?.method === 'POST') { restored = true; return Promise.resolve({ status: 'applied', hash: 'h2', content: PAGE_RESTORED }); }
      // Реальный бэк после возврата override не убирает: пакет ИИ остаётся, previous становится null
      if (restored && url.startsWith('/wiki/page')) {
        return Promise.resolve(withOvr([{ ...OVR_REPLACED, previous: null }], { hash: 'h2', content: PAGE_RESTORED }));
      }
      return undefined;
    });
    const c = renderPage();
    await settle();
    click(restoreBtn(c), 'Вернуть мою');
    await settle(16);
    expect(restorePosts()).toHaveLength(1);
    const body = JSON.parse((restorePosts()[0][1] as { body: string }).body);
    expect(body).toEqual({
      section: SECTION.key, page: PAGE_KEY, baseHash: 'h1',
      current: OVR_REPLACED.statement, restore: OVR_REPLACED.previous,
    });
    expect(findNodeByAttr(c, 'data-testid', 'wiki-override-replaced')).toBeNull();
    expect(tag(c)).toHaveLength(0);
    expect(has(c, 'Вернули вашу формулировку. 7 дней ИИ не меняет ваше на этой странице сам.')).toBe(true);
  });

  it('двойной клик «Вернуть мою» — один запрос', async () => {
    unifiedOn();
    let resolve!: (v: unknown) => void;
    await seedPage(withOvr([OVR_REPLACED]), SECTION.key, PAGE_KEY, (_u, init) =>
      (init?.method === 'POST' ? new Promise(r => { resolve = r; }) : undefined));
    const c = renderPage();
    await settle();
    const p = reactProps(restoreBtn(c)!) as { onClick: (e: unknown) => void };
    act(() => { p.onClick(undefined); p.onClick(undefined); });
    expect(restorePosts()).toHaveLength(1);
    await act(async () => { resolve({ status: 'applied', hash: 'h2', content: PAGE_OVR }); });
    await settle();
    expect(restorePosts()).toHaveLength(1);
  });

  it('409 wiki_page_changed: страница перечитана, ничего не перезаписано, плашка и кнопка остаются, текст сказан', async () => {
    unifiedOn();
    await seedPage(withOvr([OVR_REPLACED]), SECTION.key, PAGE_KEY, (_u, init) =>
      (init?.method === 'POST'
        ? Promise.reject({ status: 409, body: { code: 'wiki_page_changed', message: 'изменилась', page: { pageKey: PAGE_KEY, content: PAGE_OVR, hash: 'h9' } } })
        : undefined));
    const c = renderPage();
    await settle();
    const gets = () => pageGets().filter(([u]) => String(u).startsWith('/wiki/page?')).length;
    const before = gets();
    click(restoreBtn(c), 'Вернуть мою');
    await settle(16);
    expect(restorePosts()).toHaveLength(1);
    expect(gets()).toBe(before + 1);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-restore-error')).not.toBeNull();
    expect(has(c, 'Страница изменилась, пока вы возвращали')).toBe(true);
    expect(restoreBtn(c)).not.toBeNull();
  });

  it('400 wiki_edit_invalid: причина бэка в Notice, rejection наружу не выходит', async () => {
    unifiedOn();
    await seedPage(withOvr([OVR_REPLACED]), SECTION.key, PAGE_KEY, (_u, init) =>
      (init?.method === 'POST'
        ? Promise.reject({ status: 400, body: { code: 'wiki_edit_invalid', message: 'формулировка не человека', violations: [] } })
        : undefined));
    const c = renderPage();
    await settle();
    click(restoreBtn(c), 'Вернуть мою');
    await settle(16);
    expect(has(c, 'формулировка не человека')).toBe(true);
  });

  it('точка C.info на «Журнале»: есть при замене в открытой странице, гаснет после «Вернуть мою» (override остаётся, утверждение ИИ ушло в «Историю»)', async () => {
    unifiedOn();
    await seedPage(withOvr([OVR_REPLACED]));
    installFakeDom();
    const { root, container } = mount(createElement(ReplacementDot));
    roots.push(root);
    await settle();
    const dot = findNodeByAttr(container, 'data-testid', 'wiki-replacement-dot');
    expect(dot).not.toBeNull();
    expect(styleOf(dot!).background).toBe('var(--c-info)');
    req.mockImplementation(() => Promise.resolve(withOvr([{ ...OVR_REPLACED, previous: null }], { hash: 'h2', content: PAGE_RESTORED })));
    await act(async () => { await loadWikiPage(SECTION.key, PAGE_KEY); });
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-replacement-dot')).toBeNull();
  });

  // Как пишет RenderApply: строки утверждений без маркера списка
  const plain = (t: string) => t.replace(/^- /gm, '');

  it('строки без маркера (как пишет бэк): плашка и точка есть, после «Вернуть мою» гаснут', async () => {
    unifiedOn();
    await seedPage(withOvr([OVR_REPLACED], { content: plain(PAGE_OVR) }));
    const c = renderPage();
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-override-replaced')).not.toBeNull();
    expect(tag(c)).toHaveLength(1);
    installFakeDom();
    const { root, container } = mount(createElement(ReplacementDot));
    roots.push(root);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-replacement-dot')).not.toBeNull();
    req.mockImplementation(() => Promise.resolve(withOvr([{ ...OVR_REPLACED, previous: null }], { hash: 'h2', content: plain(PAGE_RESTORED) })));
    await act(async () => { await loadWikiPage(SECTION.key, PAGE_KEY); });
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-replacement-dot')).toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-override-replaced')).toBeNull();
    expect(tag(c)).toHaveLength(0);
  });

  it('паритет: действующие строки фильтра = строки не-«История» секций, что рисует парсер витрины', () => {
    for (const text of [PAGE_OVR, plain(PAGE_OVR), PAGE_RESTORED, plain(PAGE_RESTORED)]) {
      const rendered = parseWikiPage(text).sections.filter(x => x.name !== 'История').flatMap(x => x.lines);
      expect(wikiActiveLines(text)).toEqual(rendered);
      expect(rendered.length).toBeGreaterThan(0);
    }
  });

  const PAGE_DISPUTE = `---
type: entity
status: active
updated: 2026-11-02
---

# Шины

## Сводка
Резину меняем в конце октября. [^s1]

## Противоречия
Резину меняем в начале ноября. [^s2]

## История

[^s1]: [[Источники/Правка]] — «конец октября»
[^s2]: [[Источники/Чат]] — «начало ноября»
`;
  const tagsIn = (c: Cont, name: string) => {
    const sec = allNodes(c).find(n => n.getAttribute('data-testid') === 'wiki-section' && n.getAttribute('data-section-name') === name);
    return sec ? tag(sec as unknown as Cont) : [];
  };

  it('спор на формате бэка (строки без маркера): ⚠ на строке человека, не на доводе ИИ в «Противоречиях»', async () => {
    unifiedOn();
    await seedPage(withOvr([OVR_DISPUTED], { content: PAGE_DISPUTE }));
    const c = renderPage();
    await settle();
    expect(tag(c)).toHaveLength(1);
    expect(tagsIn(c, 'Сводка')).toHaveLength(1);
    expect(tagsIn(c, 'Противоречия')).toHaveLength(0);
    expect(tagsIn(c, 'Сводка')[0].getAttribute('data-kind')).toBe('disputed');
  });

  it('спор с target = null: меток на строках нет, плашка сверху есть', async () => {
    unifiedOn();
    await seedPage(withOvr([{ ...OVR_DISPUTED, target: null }], { content: PAGE_DISPUTE }));
    const c = renderPage();
    await settle();
    expect(tag(c)).toHaveLength(0);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-override-disputed')).not.toBeNull();
    expect(has(c, 'ИИ оспаривает ваше утверждение')).toBe(true);
  });

  // Страница после переписи оспоренного утверждения (формат бэка): строка человека ушла в «Историю»
  // со сноской, довод ИИ остался в «Противоречиях», на живых строках её нет
  const PAGE_DISPUTE_REWRITTEN = PAGE_DISPUTE
    .replace('Резину меняем в конце октября. [^s1]', 'Резину меняем в ноябре.')
    .replace('## История\n', '## История\n\nРезину меняем в конце октября. [^s1]\n');
  const OVR_DISPUTED_REWRITTEN: WikiOverrideDto = { ...OVR_DISPUTED, target: null, previous: 'Резину меняем в конце октября. [^s1]' };

  it('человек переписал оспоренное утверждение (target: null, previous в «Истории»): карточка есть, метки на строке нет', async () => {
    unifiedOn();
    await seedPage(withOvr([OVR_DISPUTED_REWRITTEN], { content: PAGE_DISPUTE_REWRITTEN }));
    const c = renderPage();
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-override-disputed')).not.toBeNull();
    expect(tag(c)).toHaveLength(0);
  });

  it('wikiOverridesOf: переписал — запись остаётся; довод ИИ тоже убран — записи нет; спор с target — по target; замена — по утверждению ИИ', () => {
    const rewritten = { content: PAGE_DISPUTE_REWRITTEN, overrides: [OVR_DISPUTED_REWRITTEN] };
    expect(wikiOverridesOf(rewritten)).toHaveLength(1);
    const noArgument = PAGE_DISPUTE_REWRITTEN.replace('Резину меняем в начале ноября. [^s2]\n', '');
    expect(wikiOverridesOf({ ...rewritten, content: noArgument })).toHaveLength(0);
    // с target: живая строка есть — запись есть; переписана — записи нет, хотя довод ИИ на месте
    expect(wikiOverridesOf({ content: PAGE_DISPUTE, overrides: [OVR_DISPUTED] })).toHaveLength(1);
    expect(wikiOverridesOf({ content: PAGE_DISPUTE_REWRITTEN, overrides: [OVR_DISPUTED] })).toHaveLength(0);
    // supersede: без изменений — по утверждению ИИ
    expect(wikiOverridesOf({ content: PAGE_OVR, overrides: [OVR_REPLACED] })).toHaveLength(1);
    expect(wikiOverridesOf({ content: PAGE_RESTORED, overrides: [OVR_REPLACED] })).toHaveLength(0);
  });

  it('человек переписал строку с target (target исчез со страницы): метка и плашка спора гаснут', async () => {
    unifiedOn();
    await seedPage(withOvr([OVR_DISPUTED], { content: PAGE_DISPUTE_REWRITTEN }));
    const c = renderPage();
    await settle();
    expect(tag(c)).toHaveLength(0);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-override-disputed')).toBeNull();
  });

  it('ИИ заменил, потом человек переписал строку: плашки и точки нет', async () => {
    unifiedOn();
    const rewritten = PAGE_OVR.replace('Для черновиков снимаем размер 864×480.', 'Для черновиков снимаем 720p.');
    await seedPage(withOvr([OVR_REPLACED], { content: rewritten }));
    const c = renderPage();
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-override-replaced')).toBeNull();
    expect(tag(c)).toHaveLength(0);
    installFakeDom();
    const { root, container } = mount(createElement(ReplacementDot));
    roots.push(root);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-replacement-dot')).toBeNull();
  });

  it('спор точку не зажигает: она про замены', async () => {
    await seedPage(withOvr([OVR_DISPUTED]));
    installFakeDom();
    const { root, container } = mount(createElement(ReplacementDot));
    roots.push(root);
    await settle();
    expect(findNodeByAttr(container, 'data-testid', 'wiki-replacement-dot')).toBeNull();
  });
});

// ─── Плашка «только чтение» и правка (F3a-qa, дефект QA a16c6167; TL-6 — без флагов) ───
// Плашка противоречит кнопке «Править»: при доступной правке её нет; она остаётся без
// флагов (прежние тесты выше) и там, где править нельзя: хроника — свой текст, локальный
// раздел и сфера — «нельзя править здесь» с причиной отказа (тексты — в wikiLabels),
// сирота — прежний текст (он верен).
// TL-6: причина — устройство раздела, а не флаг: в локальном разделе / разделе сферы
// плашка не приписывает ведение Хранителю и без флагов (та же формулировка словаря).
// Мутации этого прогона: «показывать плашку всегда» (снять условие !canEdit в
// WikiPageView) → «с обоими флагами и доступной правкой» краснеет; «вернуть привязку
// к флагам» (unified && перед section.local || section.sphereReserved) →
// «локальный раздел без флага» краснеет.

const LOCAL_SECTION = { ...SECTION, local: true };
const SPHERE_SECTION = { ...SECTION, sphereReserved: true };

describe('Плашка «только чтение» и правка (F3a-qa)', () => {
  it('с обоими флагами и доступной правкой: плашки нет, кнопка «Править» есть', async () => {
    unifiedOn();
    await seedPage(withOvr([]));
    const c = renderPage();
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-readonly')).toBeNull();
    expect(has(c, 'Только чтение')).toBe(false);
    expect(buttonByText(c, 'Править')).not.toBeNull();
  });

  it('без флагов плашка прежняя, даже когда сервер отдал hash', async () => {
    await seedPage(withOvr([]));
    const c = renderPage();
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-readonly')).not.toBeNull();
    expect(has(c, 'Только чтение')).toBe(true);
    expect(buttonByText(c, 'Править')).toBeNull();
  });

  it('хроника под флагами: плашка остаётся со своим объяснением, кнопки правки нет', async () => {
    unifiedOn();
    await seedPage(withOvr([], { content: PAGE_CHRONICLE }));
    const c = renderPage();
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-readonly')).not.toBeNull();
    expect(has(c, 'Хроника пишется сама')).toBe(true);
    expect(buttonByText(c, 'Править')).toBeNull();
  });

  it('локальный раздел под флагами: «нельзя править здесь» с причиной отказа', async () => {
    unifiedOn();
    await seedPage(withOvr([]));
    const c = renderPage(LOCAL_SECTION);
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-readonly')).not.toBeNull();
    expect(has(c, 'Эту страницу нельзя править здесь')).toBe(true);
    expect(has(c, 'Вики локальных проектов — отдельной фичей')).toBe(true);
    expect(has(c, 'Только чтение')).toBe(false);
    expect(buttonByText(c, 'Править')).toBeNull();
  });

  it('раздел сферы под флагами: «нельзя править здесь» с причиной отказа', async () => {
    unifiedOn();
    await seedPage(withOvr([]));
    const c = renderPage(SPHERE_SECTION);
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-readonly')).not.toBeNull();
    expect(has(c, 'Эту страницу нельзя править здесь')).toBe(true);
    expect(has(c, 'Раздел сферы — следующая фича')).toBe(true);
    expect(has(c, 'Только чтение')).toBe(false);
    expect(buttonByText(c, 'Править')).toBeNull();
  });

  // TL-6: без флагов плашка локального раздела / сферы не приписывает ведение Хранителю:
  // причина — устройство раздела, а не флаг; формулировка — та же, что под флагами
  it('локальный раздел без флага: плашка без «Хранителя», та же причина, что под флагами', async () => {
    await seedPage(withOvr([]));
    const c = renderPage(LOCAL_SECTION);
    await settle();
    const plate = findNodeByAttr(c, 'data-testid', 'wiki-readonly');
    expect(plate).not.toBeNull();
    const t = texts(plate!).join(' ');
    expect(t).toContain('Эту страницу нельзя править здесь');
    expect(t).toContain('Вики локальных проектов — отдельной фичей');
    expect(t).not.toContain('Хранитель');
    expect(buttonByText(c, 'Править')).toBeNull();
  });

  it('раздел сферы без флага: плашка без «Хранителя», та же причина, что под флагами', async () => {
    await seedPage(withOvr([]));
    const c = renderPage(SPHERE_SECTION);
    await settle();
    const plate = findNodeByAttr(c, 'data-testid', 'wiki-readonly');
    expect(plate).not.toBeNull();
    const t = texts(plate!).join(' ');
    expect(t).toContain('Эту страницу нельзя править здесь');
    expect(t).toContain('Раздел сферы — следующая фича');
    expect(t).not.toContain('Хранитель');
    expect(buttonByText(c, 'Править')).toBeNull();
  });

  it('сирота под флагами: плашка остаётся с прежним текстом (он верен)', async () => {
    unifiedOn();
    await seedPage(withOvr([]), ORPHAN_SECTION.key, ORPHAN_PAGE_KEY);
    const c = renderPage(ORPHAN_SECTION, ORPHAN_PAGE_KEY);
    await settle();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-readonly')).not.toBeNull();
    expect(has(c, 'Только чтение')).toBe(true);
    expect(buttonByText(c, 'Править')).toBeNull();
  });
});

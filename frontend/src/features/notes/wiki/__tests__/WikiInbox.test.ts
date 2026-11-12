// Рендер-тест «Входящих» (S7a): список показывает только спорные пакеты и у строки —
// причину; экран пакета: операции auto не показаны вовсе (0 строк, c2), у каждой спорной —
// «Почему здесь», отклонение без причины невозможно (кнопка активна только с текстом),
// решение замыкается применением (S4g): применённое/отказ применения показаны по ответу
// бэка, кнопки «повторить» нет; пустое состояние; нечитаемые файлы — числом; отказ
// контракта — русским текстом, не сырым кодом.
//
// Мокан только транспорт (offline.request) и signalr — тот же приём, что в WikiPageView.test.
//
// Мутации этого прогона (вывод — в отчёте задачи):
//  (1) показать в экране пакета ВСЕ операции (filter decision→без фильтра) →
//      «auto не показаны» краснеет;
//  (2) снять условие причины у «Подтвердить отказ» (disabled={busy} ) →
//      «отклонение без причины невозможно» краснеет.
//  (3) снять блокировку «Принять» на время запроса (disabled={busy}→{false}) →
//      «пока решение в пути, кнопки заблокированы» краснеет;
//  (4) убрать !o.humanDecision из pendingContested → «0 на решение» краснеет;
//  (5) убрать проверку loadSeq в загрузке списка → «гонка поколений» краснеет;
//  (6) игнорировать sectionScope (scope = null при любом пропe) → «область раздела» и
//      «смена пропa без перезапроса» краснеют (W9a);
//  (7) игнорировать тумблер (judgeMode = const) → «тумблер включён» или «тумблер выключен»
//      краснеет (J-F1);
//  (8) снять !o.judgeDecision из pendingContested → «1 на решение» и «нет кнопок по решённой
//      судьёй» краснеют (хвост R-J-J2a);
//  (9) убрать проверку humanDecision из judgeVerdict → «решённая человеком в виде судьи»
//      краснеет (доработка J-F1, ревью 4a5a06f8).

import { act } from 'react';
import { createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiInbox } from '../WikiInbox';

// Мок с сохранением остальных экспортов: цепочка мокованного доходит до кита хоста
// (WikiInbox → kit) — full-replacement мок рвётся на первом недостающем
// имени. Паттерн — как в NotesPage.test.ts.
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

import { request } from '../../../../lib/offline';
// Стор дерева — он же, что читает компонент через кит: тесту нужен снимок tree.sections
// для имени раздела (тот же шов, что в WikiNightlyBlock.test)
import { __test as wikiStore, consumeOpenWikiPage, loadWikiTree } from '../../../../lib/wiki';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

const op = (over: Record<string, unknown>) => ({
  index: 0, op: 'supersede', page: 'projects/p1/gleb', target: 'Глеб ревьюит только бэкенд.',
  text: 'Закрывает ревью фронта и звука до пятницы.', class: 'session×supersede',
  decision: 'inbox', reason: 'Замещает утверждение, на которое уже опираются агенты',
  baseHashes: {}, inputTokens: null, outputTokens: null, humanDecision: null, humanReason: null,
  ...over,
});

// Пакет как его отдаёт GET /wiki/packages: спорная + неспорная (auto) операции
const PKG = {
  id: '11111111-2222-3333-4444-555555555501',
  section: 'projects/p1', status: 'proposed', statusReason: null,
  createdAt: '2026-10-30T03:10:00Z', updatedAt: '2026-10-30T03:10:00Z',
  sources: [{ origin: 'session', ref: '7f3a2c11-2222-3333-4444-555555555555' }],
  operations: [
    op({}),
    op({
      index: 1, op: 'append', page: 'projects/p1/vera', target: null,
      text: 'Вера ведёт QA редактора видео.', class: 'session×append',
      decision: 'auto', reason: null,
    }),
  ],
};

const LIST = { packages: [PKG], invalidCount: 0 };

// Пакет ПОСЛЕ решения: решение человека вписано, статус — итоговый с диска (S4g)
const decidedPkg = (status: string, human: Record<string, unknown>) => ({
  ...PKG,
  status,
  operations: [op({ humanDecision: 'accepted', ...human }), PKG.operations[1]],
});

const roots: Root[] = [];
let lastRoot: Root | null = null;

function seedList(data: { packages: unknown[]; invalidCount: number }) {
  req.mockImplementation((url: string) => {
    if (url.startsWith('/wiki/packages')) return Promise.resolve(data);
    return Promise.resolve(null);
  });
}

// scope === undefined — без пропa (путь старой раскладки WikiTab); иначе — проп панели
function render(scope?: string | null | 'all'): Cont {
  installFakeDom();
  const { root, container } = mount(createElement(WikiInbox, { sectionScope: scope }));
  roots.push(root);
  lastRoot = root;
  return container;
}

// Смена пропa на живом экране (путь панели W3): тот же компонент, тот же список в состоянии
function rerender(scope?: string | null | 'all') {
  act(() => lastRoot!.render(createElement(WikiInbox, { sectionScope: scope })));
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

function opRows(container: Cont): AnyNode[] {
  return allNodes(container).filter(n => n.getAttribute('data-testid') === 'wiki-op-row');
}

function judgeOpRows(container: Cont): AnyNode[] {
  return allNodes(container).filter(n => n.getAttribute('data-testid') === 'wiki-judge-op');
}

function buttonByTestId(container: Cont, testId: string): AnyNode | null {
  return findNodeByAttr(container, 'data-testid', testId);
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

// Открыть экран пакета из первой строки списка
function openPackage(c: Cont) {
  click(buttonByTestId(c, 'wiki-pkg-row'), 'строка пакета');
}

// POST decide/accept-all — чем ответил бэк на последнее действие теста
function mockDecide(result: () => Promise<unknown>) {
  req.mockImplementation((url: string, init?: { method?: string }) => {
    if (init?.method === 'POST') return result();
    if (url.startsWith('/wiki/packages')) return Promise.resolve(LIST);
    return Promise.resolve(null);
  });
}

const postCalls = () => req.mock.calls.filter(([, init]) => (init as { method?: string } | undefined)?.method === 'POST');

// GET списка пакетов (без decide/accept-all): «смена пропa не перезапрашивает» мерится им
const listCalls = () => req.mock.calls.filter(
  ([url, init]) => String(url).startsWith('/wiki/packages') && !(init as { method?: string } | undefined)?.method,
);

beforeEach(() => {
  req.mockReset();
  req.mockImplementation(() => Promise.resolve(null));
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  lastRoot = null;
  uninstallFakeDom();
  // снимок дерева, засеянный тестом области, не должен течь в следующие тесты файла
  wikiStore.reset();
});

describe('«Входящие» (S7a)', () => {
  it('список: только спорные, у строки — причина; auto-операций в списке нет', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    expect(has(c, 'Почему здесь: Замещает утверждение, на которое уже опираются агенты')).toBe(true);
    expect(has(c, '1 на решение')).toBe(true);
    // auto-операция не показана ни строкой, ни текстом (c2)
    expect(has(c, 'Вера ведёт QA редактора видео.')).toBe(false);
  });

  it('экран пакета: auto-операций нет вовсе (0 строк), у каждой спорной — «Почему здесь»', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    openPackage(c);
    await settle();
    const rows = opRows(c);
    expect(rows).toHaveLength(1);
    expect(rows[0].getAttribute('data-decision')).toBe('inbox');
    expect(has(c, 'Почему здесь: Замещает утверждение')).toBe(true);
    expect(has(c, 'Вера ведёт QA редактора видео.')).toBe(false);
    // «предполагаемое изменение» через общий DiffView, без обещания результата
    expect(has(c, 'Предполагаемое изменение')).toBe(true);
    expect(has(c, 'Закрывает ревью фронта и звука до пятницы.')).toBe(true);
  });

  it('отклонение без причины невозможно: подтверждение активно только с написанной причиной', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    openPackage(c);
    await settle();
    click(buttonByTestId(c, 'wiki-op-reject-0'), 'Отклонить');
    await settle();
    const confirm = buttonByTestId(c, 'wiki-op-reject-confirm-0');
    expect(confirm).not.toBeNull();
    expect((reactProps(confirm!) as { disabled?: boolean }).disabled).toBe(true);
    const area = allNodes(c).find(n => n.nodeName === 'TEXTAREA');
    expect(area).toBeTruthy();
    // props textarea-узла — DOM-обработчик Field.tsx: ждёт событие, не строку
    const areaProps = reactProps(area!) as { onChange?: (e: { target: { value: string } }) => void };
    act(() => { areaProps.onChange!({ target: { value: 'это уже не верно' } }); });
    await settle();
    expect((reactProps(buttonByTestId(c, 'wiki-op-reject-confirm-0')!) as { disabled?: boolean }).disabled).toBe(false);
    mockDecide(() => Promise.resolve({ package: decidedPkg('rejected', { humanDecision: 'rejected', humanReason: 'это уже не верно' }), apply: null }));
    click(buttonByTestId(c, 'wiki-op-reject-confirm-0'), 'Подтвердить отказ');
    await settle();
    const posts = postCalls();
    expect(posts).toHaveLength(1);
    expect(String(posts[0][0])).toContain('/decide');
    expect(JSON.parse((posts[0][1] as { body: string }).body)).toMatchObject({ index: 0, accept: false, reason: 'это уже не верно' });
    // отказ без причины не доходит до бэка; экран показывает записанное решение
    expect(has(c, 'отклонена')).toBe(true);
    expect(has(c, 'Причина отказа: это уже не верно')).toBe(true);
    expect(has(c, 'пакет закрыт')).toBe(true);
  });

  it('последнее принятое решение применяет пакет: показан итог применения, без «повторить»', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    openPackage(c);
    await settle();
    mockDecide(() => Promise.resolve({
      package: { ...decidedPkg('applied', {}), status: 'applied' },
      apply: { applied: true, code: null, reason: null, violations: [] },
    }));
    click(buttonByTestId(c, 'wiki-op-accept-0'), 'Принять');
    await settle();
    expect(postCalls()).toHaveLength(1);
    expect(has(c, 'Изменения применены')).toBe(true);
    expect(has(c, 'применён')).toBe(true);
    // кнопок решения больше нет: пакет вне proposed; «повторить» нет: повторного применения здесь нет
    expect(buttonByTestId(c, 'wiki-op-accept-0')).toBeNull();
    expect(buttonByText(c, 'Повторить')).toBeNull();
  });

  it('отказ применения — причина бэка, без кнопки «повторить»', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    openPackage(c);
    await settle();
    mockDecide(() => Promise.resolve({
      package: { ...PKG, status: 'apply_failed' },
      apply: { applied: false, code: 'wiki_apply_source_unavailable', reason: 'нет карточки источника', violations: ['нет карточки источника'] },
    }));
    click(buttonByTestId(c, 'wiki-op-accept-0'), 'Принять');
    await settle();
    expect(has(c, 'нет карточки источника')).toBe(true);
    expect(has(c, 'wiki_apply_source_unavailable')).toBe(false);
    expect(buttonByText(c, 'Повторить')).toBeNull();
    expect(buttonByText(c, 'Применить')).toBeNull();
  });

  it('гонка двух решений — 409 русским текстом, не сырым кодом', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    openPackage(c);
    await settle();
    mockDecide(() => Promise.reject({ status: 409, body: { code: 'wiki_pkg_not_decidable', message: 'x' } }));
    click(buttonByTestId(c, 'wiki-op-accept-0'), 'Принять');
    await settle();
    expect(has(c, 'Пакет уже решается в другом месте')).toBe(true);
    expect(has(c, 'wiki_pkg_not_decidable')).toBe(false);
  });

  it('пусто — «Спорного нет», а не пустой экран', async () => {
    seedList({ packages: [], invalidCount: 0 });
    const c = render();
    await settle();
    expect(has(c, 'Спорного нет')).toBe(true);
    expect(opRows(c)).toHaveLength(0);
  });

  it('нечитаемые файлы — названы числом, а не пустотой', async () => {
    seedList({ packages: [PKG], invalidCount: 2 });
    const c = render();
    await settle();
    expect(has(c, 'Нечитаемых файлов пакета: 2')).toBe(true);
  });

  it('частичное решение (apply=null, статус proposed): плашка «Решение записано, осталось N спорных»', async () => {
    // пакет с двумя спорными: решение по первой оставляет вторую нерешённой — бэк отвечает
    // {package: proposed с нерешённой операцией, apply: null} (применять нечего)
    const two = {
      ...PKG,
      operations: [
        op({}),
        op({ index: 1, op: 'append', page: 'projects/p1/vera', target: null, text: 'Вера ведёт QA.', class: 'session×append', decision: 'inbox', reason: 'Дополняет страницу' }),
      ],
    };
    const afterFirst = { ...two, operations: [op({ humanDecision: 'accepted' }), two.operations[1]] };
    seedList({ packages: [two], invalidCount: 0 });
    const c = render();
    await settle();
    openPackage(c);
    await settle();
    mockDecide(() => Promise.resolve({ package: afterFirst, apply: null }));
    click(buttonByTestId(c, 'wiki-op-accept-0'), 'Принять');
    await settle();
    expect(has(c, 'Решение записано')).toBe(true);
    expect(has(c, 'осталось 1 спорных')).toBe(true);
    // решённая операция больше не на решение (бейдж «принята»), нерешённая осталась с кнопками
    expect(has(c, 'принята')).toBe(true);
    expect(buttonByTestId(c, 'wiki-op-accept-1')).not.toBeNull();
  });

  it('пока решение в пути, кнопки операции заблокированы', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    openPackage(c);
    await settle();
    let resolvePost: (v: unknown) => void = () => {};
    mockDecide(() => new Promise<unknown>(res => { resolvePost = res; }));
    click(buttonByTestId(c, 'wiki-op-accept-0'), 'Принять');
    await settle();
    // POST ещё висит: «Принять» и «Отклонить» заблокированы (урок S5b/S5e)
    expect((reactProps(buttonByTestId(c, 'wiki-op-accept-0')!) as { disabled?: boolean }).disabled).toBe(true);
    expect((reactProps(buttonByTestId(c, 'wiki-op-reject-0')!) as { disabled?: boolean }).disabled).toBe(true);
    // завершаем запрос, чтобы не оставлять висящий промис
    resolvePost({ package: decidedPkg('applied', {}), apply: { applied: true, code: null, reason: null, violations: [] } });
    await settle();
  });

  it('решённая человеком операция не считается «на решение»: строка «0 на решение»', async () => {
    // строка «Входящих» для пакета, где единственная спорная уже решена человеком:
    // pendingContested пуст → «0 на решение», не «1»
    const row = { ...PKG, operations: [op({ humanDecision: 'accepted' }), PKG.operations[1]] };
    seedList({ packages: [row], invalidCount: 0 });
    const c = render();
    await settle();
    expect(has(c, '0 на решение')).toBe(true);
    expect(has(c, '1 на решение')).toBe(false);
  });

  it('причина цели @human — короткий текст пункта ADR, сырая строка бэка не показана (F6a)', async () => {
    const pkg = {
      ...PKG,
      operations: [
        op({
          class: 'session×supersede@human',
          reason: 'Источник старше вашей правки (2026-10-29): замена и оспаривание после неё идут через ревью',
        }),
        PKG.operations[1],
      ],
    };
    seedList({ packages: [pkg], invalidCount: 0 });
    const c = render();
    await settle();
    // точный текстовый узел, не подстрока: сырая строка бэка длиннее текста пункта и
    // не равна ему — на прямой выдаче причины эта проверка краснеет (R-F6a, минор :358)
    expect(has(c, 'Почему здесь:')).toBe(true);
    expect(texts(c)).toContain('Источник старше вашей правки');
    expect(has(c, 'замена и оспаривание после неё идут через ревью')).toBe(false);
  });

  it('нераспознанное начало причины цели @human — запасной текст, без сырой строки (F6a)', async () => {
    const pkg = {
      ...PKG,
      operations: [
        op({
          op: 'conflict',
          class: 'session×conflict@human',
          reason: 'целевая страница в статусе «disputed»: там уже нужен человек',
        }),
        PKG.operations[1],
      ],
    };
    seedList({ packages: [pkg], invalidCount: 0 });
    const c = render();
    await settle();
    expect(has(c, 'Почему здесь: ИИ не смог применить это сам')).toBe(true);
    expect(has(c, 'целевая страница в статусе')).toBe(false);
  });

  it('цель не человека — причина как есть, прежнее поведение (F6a)', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    expect(has(c, 'Почему здесь: Замещает утверждение, на которое уже опираются агенты')).toBe(true);
  });

  it('подпись сверху списка — текст ADR-038 (F6a)', async () => {
    seedList(LIST);
    const c = render();
    await settle();
    expect(has(c, 'Остальное, включая замену ваших формулировок новыми источниками, ИИ применил сам — это видно в «Журнале».')).toBe(true);
  });

  it('гонка поколений: поздний ответ прежней загрузки не затирает свежий список', async () => {
    // mount → список A (быстрый). Первый «Назад» → загрузка B (висит). Пока B висит, снова
    // открыть/закрыть → загрузка C (быстрая, свежий список). B отвечает ПОСЛЕ C: с проверкой
    // поколения (loadSeq) B не затрёт C; без неё — затрёт (тест краснеет).
    const A = { ...PKG, sources: [{ origin: 'session', ref: 'СПИСОК-А' }] };
    const B = { ...PKG, sources: [{ origin: 'session', ref: 'ЗАПАЗДАЛЫЙ-B' }] };
    const C = { ...PKG, sources: [{ origin: 'session', ref: 'СВЕЖИЙ-C' }] };
    let resolveB: (v: unknown) => void = () => {};
    let n = 0;
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/packages')) {
        n++;
        if (n === 1) return Promise.resolve({ packages: [A], invalidCount: 0 });
        if (n === 2) return new Promise(res => { resolveB = res; });
        return Promise.resolve({ packages: [C], invalidCount: 0 });
      }
      return Promise.resolve(null);
    });
    const c = render();
    await settle();
    expect(has(c, 'СПИСОК-А')).toBe(true);
    // открыть/закрыть → загрузка B (висит); список остаётся A
    openPackage(c); await settle();
    click(buttonByTestId(c, 'wiki-pkg-back'), 'Назад'); await settle();
    // пока B висит — снова открыть/закрыть → свежая загрузка C (быстрая)
    openPackage(c); await settle();
    click(buttonByTestId(c, 'wiki-pkg-back'), 'Назад'); await settle();
    expect(has(c, 'СВЕЖИЙ-C')).toBe(true);
    // запоздалый ответ B приходит ПОСЛЕ C: не должен затереть свежий список
    resolveB({ packages: [B], invalidCount: 0 });
    await settle();
    expect(has(c, 'СВЕЖИЙ-C')).toBe(true);
    expect(has(c, 'ЗАПАЗДАЛЫЙ-B')).toBe(false);
  });
});

// ─── Область раздела (W9a) ───
// Проп панели с чипом раздела: строка раздела — только её пакеты; без пропa / 'all' / null —
// все (прежнее поведение старой раскладки). Фильтр поверх загруженного списка: смена пропa
// не делает второго GET. Пустая область — «Разобрано» с именем раздела по дереву.
describe('область раздела (W9a)', () => {
  const PKG_A = { ...PKG, id: '11111111-2222-3333-4444-555555555A01', section: 'projects/alpha', sources: [{ origin: 'session', ref: 'ПАКЕТ-А' }] };
  const PKG_B = { ...PKG, id: '11111111-2222-3333-4444-555555555B01', section: 'projects/beta', sources: [{ origin: 'session', ref: 'ПАКЕТ-Б' }] };

  it('строка раздела — только её пакеты; без пропa, "all" и null — все', async () => {
    seedList({ packages: [PKG_A, PKG_B], invalidCount: 0 });
    const c = render('projects/alpha');
    await settle();
    expect(has(c, 'ПАКЕТ-А')).toBe(true);
    expect(has(c, 'ПАКЕТ-Б')).toBe(false);
    // без пропa — путь старой раскладки: прежний вид, все пакеты
    rerender();
    await settle();
    expect(has(c, 'ПАКЕТ-Б')).toBe(true);
    rerender('all');
    await settle();
    expect(has(c, 'ПАКЕТ-Б')).toBe(true);
    rerender(null);
    await settle();
    expect(has(c, 'ПАКЕТ-А')).toBe(true);
    expect(has(c, 'ПАКЕТ-Б')).toBe(true);
  });

  it('смена sectionScope не перезапрашивает сервер: фильтр поверх списка', async () => {
    seedList({ packages: [PKG_A, PKG_B], invalidCount: 0 });
    const c = render();
    await settle();
    expect(listCalls()).toHaveLength(1);
    expect(has(c, 'ПАКЕТ-А')).toBe(true);
    expect(has(c, 'ПАКЕТ-Б')).toBe(true);
    rerender('projects/alpha');
    await settle();
    expect(has(c, 'ПАКЕТ-Б')).toBe(false);
    rerender('projects/beta');
    await settle();
    expect(has(c, 'ПАКЕТ-А')).toBe(false);
    expect(has(c, 'ПАКЕТ-Б')).toBe(true);
    // ни одного лишнего GET за всю смену области
    expect(listCalls()).toHaveLength(1);
  });

  it('пустая область — «Разобрано» с именем раздела по дереву', async () => {
    req.mockImplementation((url: string) => {
      if (url.startsWith('/wiki/packages')) return Promise.resolve({ packages: [PKG_A], invalidCount: 0 });
      if (url.startsWith('/wiki/tree')) {
        return Promise.resolve({
          initialized: true,
          sections: [{ key: 'projects/beta', name: 'Раздел Б', orphan: false, local: false, sphereReserved: false, pageCount: 0 }],
        });
      }
      return Promise.resolve(null);
    });
    await act(async () => { await loadWikiTree(); });
    const c = render('projects/beta');
    await settle();
    expect(has(c, 'Разобрано')).toBe(true);
    expect(has(c, 'Всё, что ИИ предлагал по разделу «Проекты / Раздел Б», применено или отклонено.')).toBe(true);
    // это не общее пустое состояние: пакет другого раздела скрыт, «Спорного нет» нет
    expect(has(c, 'Спорного нет')).toBe(false);
    expect(has(c, 'ПАКЕТ-А')).toBe(false);
  });

  it('раздела нет в дереве — ключ как есть (тот же фолбэк, что у строки списка)', async () => {
    seedList({ packages: [PKG_A], invalidCount: 0 });
    const c = render('projects/unknown');
    await settle();
    expect(has(c, 'Разобрано')).toBe(true);
    expect(has(c, 'Всё, что ИИ предлагал по разделу «projects/unknown», применено или отклонено.')).toBe(true);
  });
});

// ─── «Решения» (J-F1): режим — тумблером «Применять без ревью» (ADR-039, решение 9) ───
// Включён → «Решения судьи»: список только для чтения (вердикт, причина, ссылка на страницу),
// кнопок решения нет; кнопки отката в строке очереди тоже нет (дефект 24c61d60: очередь
// несёт непринятые пакеты, откатывают применённый — кнопка всегда получала 409; откат —
// в «Журнале» → «Решения за ночь»). Выключен → прежние «Входящие» (поведение без изменений).
// Снимка настроек нет → ручная очередь: отказ чтения настроек не отказ списка (этим живут все
// тесты выше: они не сечат /wiki/settings).

// Форма ответа настроек, читаемая loadWikiSettings: без nightly — не снимок (бросается), поэтому
// фикстура несёт пустой блок; режим меряет только autoApplyEnabled.
const SETTINGS_ON = { autoApplyEnabled: true, classes: [], nightly: { enabled: true }, lastNight: null, nightlyRunning: false };
const SETTINGS_OFF = { ...SETTINGS_ON, autoApplyEnabled: false };

function seedBoth(data: { packages: unknown[]; invalidCount: number }, settings: unknown) {
  req.mockImplementation((url: string) => {
    if (url.startsWith('/wiki/packages')) return Promise.resolve(data);
    if (url.startsWith('/wiki/settings')) return Promise.resolve(settings);
    return Promise.resolve(null);
  });
}

// Пакет с вердиктами судьи, ещё в очереди: проход решил op0 (отказ с причиной) и op1 (принятие),
// op2 отложил (вердикта нет) — пакет остаётся proposed (StatusAfterDecisions: есть нерешённые).
const JPKG = {
  ...PKG,
  operations: [
    op({
      judgeDecision: 'rejected', judgeReason: 'числа в утверждении не подтверждены цитатой',
      judgeModel: 'claude-x', judgeAt: '2026-11-12T03:40:00Z', judgeCostUsd: 0.01, judgeAttempts: 1,
    }),
    op({
      index: 1, op: 'append', page: 'projects/p1/vera', target: null, text: 'Вера ведёт QA.',
      class: 'session×append', decision: 'inbox', reason: 'Дополняет страницу',
      judgeDecision: 'accepted', judgeModel: 'claude-x', judgeAt: '2026-11-12T03:41:00Z', judgeCostUsd: 0.01, judgeAttempts: 1,
    }),
    op({
      index: 2, op: 'link', page: 'projects/p1/gleb', target: 'projects/p1/vera', text: null,
      class: 'session×link', decision: 'inbox', reason: 'Новая связь',
    }),
    PKG.operations[1],
  ],
};

// Пакет после частичного решения человека: в ручном режиме op0 принята, op1 отклонена,
// потом включено автоприменение; op2 (index 2) ещё не решена никем — пакет в очереди
// (proposed). В строке «Решений судьи» решённые человеком показывают решение человека,
// а не «вердикта ещё нет» (доработка J-F1, ревью 4a5a06f8).
const JPKG_HUMAN = {
  ...PKG,
  operations: [
    op({ humanDecision: 'accepted' }),
    op({
      index: 1, op: 'append', page: 'projects/p1/vera', target: null, text: 'Вера ведёт QA.',
      class: 'session×append', decision: 'inbox', reason: 'Дополняет страницу',
      humanDecision: 'rejected', humanReason: 'это уже не верно',
    }),
    op({
      index: 2, op: 'link', page: 'projects/p1/gleb', target: 'projects/p1/vera', text: null,
      class: 'session×link', decision: 'inbox', reason: 'Новая связь',
    }),
    PKG.operations[1],
  ],
};

describe('«Решения судьи» (J-F1)', () => {
  it('тумблер включён: «Решения судьи», вердикты и «почему», кнопок «Принять»/«Отклонить» нет', async () => {
    seedBoth({ packages: [JPKG], invalidCount: 0 }, SETTINGS_ON);
    const c = render();
    await settle();
    expect(has(c, 'Решения судьи')).toBe(true);
    // вердикт по каждой спорной операции, с именем агента решения (И1: видно, чьё решение);
    // недоделанное — честно «вердикта ещё нет», а не пустая строка
    expect(has(c, 'отклонена судьёй')).toBe(true);
    expect(has(c, 'принята судьёй')).toBe(true);
    expect(has(c, 'вердикта ещё нет')).toBe(true);
    // «почему» — judgeReason из фикстуры
    expect(has(c, 'числа в утверждении не подтверждены цитатой')).toBe(true);
    // список только для чтения: экрана пакета и кнопок решения нет вовсе
    expect(buttonByText(c, 'Принять')).toBeNull();
    expect(buttonByText(c, 'Отклонить')).toBeNull();
    expect(opRows(c)).toHaveLength(0);
    // auto-операций нет и здесь (тот же контракт S4a)
    expect(has(c, 'Вера ведёт QA редактора видео.')).toBe(false);
  });

  it('ссылка на страницу — существующим каналом requestOpenWikiPage (тот же, что в «Журнале»)', async () => {
    seedBoth({ packages: [JPKG], invalidCount: 0 }, SETTINGS_ON);
    const c = render();
    await settle();
    click(buttonByTestId(c, 'wiki-judge-open-page-0'), 'Открыть страницу');
    expect(consumeOpenWikiPage()).toEqual({ section: 'projects/p1', path: 'projects/p1/gleb' });
  });

  it('в строке очереди вида судьи нет кнопки «Откатить пакет» (дефект 24c61d60)', async () => {
    // очередь несёт непринятые пакеты, откатывают применённый — кнопка строки всегда
    // получала 409; откат живёт в «Журнале» → «Решения за ночь»
    seedBoth({ packages: [JPKG], invalidCount: 0 }, SETTINGS_ON);
    const c = render();
    await settle();
    expect(buttonByTestId(c, 'wiki-pkg-revert')).toBeNull();
    expect(buttonByText(c, 'Откатить пакет')).toBeNull();
  });

  it('решённая человеком операция в виде судьи: бейдж решения человека, не «вердикта ещё нет» (доработка J-F1)', async () => {
    seedBoth({ packages: [JPKG_HUMAN], invalidCount: 0 }, SETTINGS_ON);
    const c = render();
    await settle();
    const rows = judgeOpRows(c);
    expect(rows).toHaveLength(3);
    const rowText = (n: AnyNode) => textNodes(n).map(t => t.trim()).join(' ');
    // op0/op1 решил человек: бейдж — решение человека, тем же текстом, что в экране пакета
    expect(rowText(rows[0])).toContain('принята');
    expect(rowText(rows[0])).not.toContain('вердикта ещё нет');
    expect(rowText(rows[1])).toContain('отклонена');
    expect(rowText(rows[1])).not.toContain('вердикта ещё нет');
    // op2 не решил никто — прежнее честное «вердикта ещё нет»
    expect(rowText(rows[2])).toContain('вердикта ещё нет');
  });

  it('тумблер выключен: прежние «Входящие» с кнопками, поведение без изменений', async () => {
    seedBoth(LIST, SETTINGS_OFF);
    const c = render();
    await settle();
    expect(has(c, 'Решения судьи')).toBe(false);
    expect(has(c, 'Здесь только то, где нужен человек')).toBe(true);
    openPackage(c);
    await settle();
    expect(buttonByTestId(c, 'wiki-op-accept-0')).not.toBeNull();
    expect(buttonByTestId(c, 'wiki-op-reject-0')).not.toBeNull();
  });

  it('решённая судьёй операция в ручном режиме: не «на решение», без кнопок «Принять»/«Отклонить» (хвост R-J-J2a)', async () => {
    seedBoth({ packages: [JPKG], invalidCount: 0 }, SETTINGS_OFF);
    const c = render();
    await settle();
    // в счётчике строки — только то, что не решил ни человек, ни судья (op2)
    expect(has(c, '1 на решение')).toBe(true);
    openPackage(c);
    await settle();
    // по вердикту судья кнопок решения нет: человек его вторым решением не переворачивает (И1)
    expect(buttonByTestId(c, 'wiki-op-accept-0')).toBeNull();
    expect(buttonByTestId(c, 'wiki-op-reject-0')).toBeNull();
    expect(buttonByTestId(c, 'wiki-op-accept-1')).toBeNull();
    expect(buttonByTestId(c, 'wiki-op-reject-1')).toBeNull();
    // решённое видно и называется чьим оно
    expect(has(c, 'отклонена судьёй')).toBe(true);
    expect(has(c, 'принята судьёй')).toBe(true);
    // не решённая никем — прежняя, с кнопками
    expect(buttonByTestId(c, 'wiki-op-accept-2')).not.toBeNull();
    expect(buttonByTestId(c, 'wiki-op-reject-2')).not.toBeNull();
  });
});

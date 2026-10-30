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
//  (5) убрать проверку loadSeq в загрузке списка → «гонка поколений» краснеет.

import { act } from 'react';
import { createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiInbox } from '../WikiInbox';

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

const op = (over: Record<string, unknown>) => ({
  index: 0, op: 'supersede', page: 'projects/p1/gleb', target: 'Глеб ревьюит только бэкенд.',
  text: 'Закрывает ревью фронта и звука до пятницы.', class: 'session×supersede',
  decision: 'inbox', reason: 'Замещает утверждение, на которое уже опираются агенты',
  baseHashes: {}, inputTokens: null, outputTokens: null, humanDecision: null, humanReason: null,
  ...over,
});

// Пакет как его отдаёт GET /api/wiki/packages: спорная + неспорная (auto) операции
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

function seedList(data: { packages: unknown[]; invalidCount: number }) {
  req.mockImplementation((url: string) => {
    if (url.startsWith('/api/wiki/packages')) return Promise.resolve(data);
    return Promise.resolve(null);
  });
}

function render(): Cont {
  installFakeDom();
  const { root, container } = mount(createElement(WikiInbox));
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

function opRows(container: Cont): AnyNode[] {
  return allNodes(container).filter(n => n.getAttribute('data-testid') === 'wiki-op-row');
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
    if (url.startsWith('/api/wiki/packages')) return Promise.resolve(LIST);
    return Promise.resolve(null);
  });
}

const postCalls = () => req.mock.calls.filter(([, init]) => (init as { method?: string } | undefined)?.method === 'POST');

beforeEach(() => {
  req.mockReset();
  req.mockImplementation(() => Promise.resolve(null));
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  uninstallFakeDom();
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
      if (url.startsWith('/api/wiki/packages')) {
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

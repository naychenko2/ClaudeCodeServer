// Карточка спорной операции показывает доказательства (С2 Ш6b): утверждение, цитаты по
// порядку массива, вид страницы подписью PAGE_TYPE (kind в любом регистре — поиск по
// нижнему), для state — «на {asOf}» строкой как есть. У старой операции (все поля null)
// блока нет вовсе. Для синтезированных операций Text === Claim утверждение видно один раз:
// op.text не печатается, когда он равен claim (доработка ревью 6d67a6b1).
//
// Мутации (вывод — в отчёте задачи):
//  (1) quotes.map → quotes.slice(0, 1) → «цитаты — по порядку» краснеет;
//  (2) убрать условие kind === 'state' у asOf → «kind=decision без даты» краснеет;
//  (3) печатать op.text всегда (убрать text !== claim) → «утверждение один раз» краснеет;
//  (4) вернуть поиск PAGE_TYPE без lower-case op.kind → «kind='State'» краснеет;
//  (5) снять метку ожидания в бейдже режима судьи (последняя ветка decidedBadge → null) →
//      «допустительная — «вердикта ещё нет» без кнопок» краснеет (доработка J-F1, финал 9c305f19);
//  (6) убрать строку «A → B» операции merge (блок wiki-op-merge) → «Мелкая → Крупная» краснеет;
//      снять в intentLines признак op.op !== 'merge' у fallback «− target» → «нет предполагаемого
//      изменения» краснеет (C7).

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, afterEach } from 'vitest';
import { mount, installFakeDom, uninstallFakeDom, textNodes, findNodeByAttr, reactProps } from '../../../../components/chat/__tests__/fakeDom';
import { WikiPackageView } from '../WikiPackageView';

vi.mock('../../../../lib/signalr', async (orig) => ({
  ...await orig<typeof import('../../../../lib/signalr')>(),
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));
vi.mock('../../../../lib/offline', async (orig) => ({
  ...await orig<typeof import('../../../../lib/offline')>(),
  request: vi.fn(() => Promise.resolve(null)),
  readStoredUserId: vi.fn(() => 'u1'),
}));

import { request } from '../../../../lib/offline';
const req = request as unknown as ReturnType<typeof vi.fn>;

const op = (over: Record<string, unknown>) => ({
  index: 0, op: 'supersede', page: 'projects/p1/gleb', target: 'Глеб ревьюит только бэкенд.',
  text: 'Закрывает ревью фронта.', class: 'session×supersede', decision: 'inbox',
  reason: 'Спорно', baseHashes: {}, inputTokens: null, outputTokens: null,
  humanDecision: null, humanReason: null,
  claim: null, quotes: null, kind: null, asOf: null,
  ...over,
});

const pkg = (o: Record<string, unknown>) => ({
  id: 'p1', section: 'projects/p1', status: 'proposed', statusReason: null,
  createdAt: '2026-10-30T03:10:00Z', updatedAt: '2026-10-30T03:10:00Z',
  sources: [{ origin: 'session', ref: 's1' }], operations: [o],
});

const roots: Root[] = [];
function render(o: Record<string, unknown>) {
  installFakeDom();
  const { root, container } = mount(createElement(WikiPackageView, { pkg: pkg(o) as never, onBack: () => {} }));
  roots.push(root);
  return container;
}
type Node = ReturnType<typeof mount>['container'];
const texts = (c: Node) => textNodes(c).map(t => t.trim()).filter(Boolean);
const all = (c: Node) => texts(c).join(' ');
// Текст одного узла по data-testid — ассерт вида/утверждения по узлу, а не по экрану
const nodeText = (c: Node, testid: string): string | null => {
  const n = findNodeByAttr(c, 'data-testid', testid);
  // те же trim/фильтр, что у texts: JSX делит «Состояние» и « · на …» на отдельные узлы
  return n ? textNodes(n).map(t => t.trim()).filter(Boolean).join(' ') : null;
};
// Весь текст карточки, кроме блока «Предполагаемое изменение» (заголовок + DiffView
// в общем wrapper): чтобы мерить, сколько раз утверждение видно вне диффа
const textsOutsideDiff = (c: Node) => {
  let block: Node | null = null;
  const find = (x: Node): void => {
    if (block) return;
    if (x.nodeType === 1 && x.childNodes.some(ch => ch.nodeType === 3 && ch.nodeValue.startsWith('Предполагаемое изменение'))) {
      block = x.parentNode;
      return;
    }
    for (const ch of x.childNodes) find(ch);
  };
  find(c);
  const out: string[] = [];
  const walk = (x: Node): void => {
    if (x === block) return;
    if (x.nodeType === 3) out.push(x.nodeValue);
    for (const ch of x.childNodes) walk(ch);
  };
  walk(c);
  return out.map(t => t.trim()).filter(Boolean);
};

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  uninstallFakeDom();
});

describe('карточка операции: доказательства (Ш6b)', () => {
  it('утверждение, цитаты по порядку, вид и «на asOf» для state', () => {
    const c = render(op({
      claim: 'Глеб теперь ревьюит и фронт', quotes: ['первая цитата', 'вторая', 'третья'],
      kind: 'state', asOf: 'unknown-дата?',
    }));
    const t = texts(c);
    expect(all(c)).toContain('Глеб теперь ревьюит и фронт');
    // порядок цитат — по массиву
    const i1 = t.indexOf('первая цитата'), i2 = t.indexOf('вторая'), i3 = t.indexOf('третья');
    expect(i1).toBeGreaterThanOrEqual(0);
    expect(i2).toBeGreaterThan(i1);
    expect(i3).toBeGreaterThan(i2);
    expect(all(c)).toContain('Состояние · на unknown-дата?');
  });

  it('claim и ровно 2 цитаты; state с asOf=2026-08-21', () => {
    const c = render(op({ claim: 'Утверждение X', quotes: ['цитата А', 'цитата Б'], kind: 'state', asOf: '2026-08-21' }));
    expect(all(c)).toContain('Утверждение X');
    const quotes: unknown[] = [];
    const walk = (n: ReturnType<typeof mount>['container']) => {
      if (n.getAttribute?.('data-testid') === 'wiki-op-quote') quotes.push(n);
      for (const ch of n.childNodes) walk(ch);
    };
    walk(c);
    expect(quotes).toHaveLength(2);
    expect(all(c)).toContain('Состояние · на 2026-08-21');
    expect(findNodeByAttr(c, 'data-testid', 'wiki-op-evidence')).not.toBeNull();
  });

  it('kind=decision без даты: подпись вида по узлу, «на» нет', () => {
    const c = render(op({ kind: 'decision', asOf: '2026-01-01' }));
    const kind = nodeText(c, 'wiki-op-kind');
    expect(kind).not.toBeNull();
    expect(kind).toContain('Решение');
    expect(kind).not.toContain('на 2026-01-01');
  });

  it("kind в верхнем регистре ('State'): подпись маппится та же, «на asOf» при нём", () => {
    const c = render(op({ kind: 'State', asOf: '2026-08-21' }));
    const kind = nodeText(c, 'wiki-op-kind');
    expect(kind).not.toBeNull();
    expect(kind).toContain('Состояние · на 2026-08-21');
  });

  it('Text === Claim: утверждение видно в карточке ровно один раз (вне диффа)', () => {
    const text = 'Тот же текст, что и утверждение.';
    const c = render(op({ claim: text, text }));
    // ровно один текстовый узел с этим текстом вне блока «Предполагаемое изменение»;
    // в намерении для DiffView текст остаётся как есть
    expect(textsOutsideDiff(c).filter(t => t === text)).toHaveLength(1);
    expect(all(c)).toContain(text);
  });

  it('старая операция (все null): блока доказательств нет', () => {
    const c = render(op({}));
    expect(findNodeByAttr(c, 'data-testid', 'wiki-op-evidence')).toBeNull();
    expect(all(c)).not.toContain('Утверждение:');
    expect(all(c)).not.toContain('Цитаты:');
  });
});

// ─── Режим судьи (доработка J-F1, финал 9c305f19) ───
// Экран открывается и в режиме судьи — когда в пакете есть операции вне его допуска
// (autoapply_off, без кода): кнопки получают только они; допустительные нерешённые ждут
// прохода («вердикта ещё нет»), вердикт — без кнопок (И1). «Принять всё» решает все
// нерешённые разом — в режиме судьи только когда всё нерешённое вне допуска.

const opRowNodes = (c: Node) => {
  const out: Node[] = [];
  const walk = (x: Node) => {
    if (x.getAttribute?.('data-testid') === 'wiki-op-row') out.push(x);
    for (const ch of x.childNodes) walk(ch);
  };
  walk(c);
  return out;
};
const rowText = (n: Node) => textNodes(n).map(t => t.trim()).filter(Boolean).join(' ');

function renderOps(ops: Record<string, unknown>[], judgeMode?: boolean) {
  installFakeDom();
  const p = {
    id: 'p1', section: 'projects/p1', status: 'proposed', statusReason: null,
    createdAt: '2026-10-30T03:10:00Z', updatedAt: '2026-10-30T03:10:00Z',
    sources: [{ origin: 'session', ref: 's1' }], operations: ops,
  };
  const { root, container } = mount(createElement(WikiPackageView, { pkg: p as never, judgeMode, onBack: () => {} }));
  roots.push(root);
  return container;
}

describe('режим судьи: кнопки только у операций вне допуска (доработка J-F1)', () => {
  // op0 — autoapply_off (вне допуска), op1 — допущена и ещё не решена, op2 — вердикт судьи
  const MIXED = [
    op({ reasonCode: 'autoapply_off' }),
    op({ index: 1, reasonCode: 'overrides_human' }),
    op({ index: 2, reasonCode: 'overrides_human', judgeDecision: 'rejected', judgeModel: 'claude-x', judgeReason: 'числа не подтверждены цитатой' }),
  ];

  it('режим судьи: вне допуска — с кнопками; допустительная — «вердикта ещё нет» без кнопок; вердикт — без кнопок', () => {
    const c = renderOps(MIXED, true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-op-accept-0')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-op-reject-0')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-op-accept-1')).toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-op-reject-1')).toBeNull();
    const rows = opRowNodes(c);
    expect(rows).toHaveLength(3);
    expect(rowText(rows[1])).toContain('вердикта ещё нет');
    expect(rowText(rows[2])).toContain('отклонена судьёй');
    expect(findNodeByAttr(c, 'data-testid', 'wiki-op-accept-2')).toBeNull();
  });

  it('без режима судьи (регресс): тот же пакет — кнопки у всех нерешённых, метки ожидания нет', () => {
    const c = renderOps(MIXED);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-op-accept-0')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-op-accept-1')).not.toBeNull();
    expect(findNodeByAttr(c, 'data-testid', 'wiki-op-accept-2')).toBeNull();
    expect(all(c)).not.toContain('вердикта ещё нет');
  });

  it('режим судьи: «Принять всё» — нет для смешанного пакета, да когда всё нерешённое вне допуска', () => {
    const out1 = op({ reasonCode: 'autoapply_off' });
    const out2 = op({ index: 1, op: 'append', page: 'projects/p1/vera', target: null, text: 'Вера ведёт QA.', class: 'session×append', reasonCode: 'autoapply_off' });
    const admitted = op({ index: 2, reasonCode: 'overrides_human' });
    // смешанный: две человеческие + одна судьи — оптом забрать судью нельзя
    const c1 = renderOps([out1, out2, admitted], true);
    expect(findNodeByAttr(c1, 'data-testid', 'wiki-pkg-accept-all')).toBeNull();
    // тот же пакет без допустительной — «Принять всё» на прежнем месте
    const c2 = renderOps([out1, out2], true);
    expect(findNodeByAttr(c2, 'data-testid', 'wiki-pkg-accept-all')).not.toBeNull();
  });
});

// ─── Плашка смешанного пакета (T2): часть решена — остальные ещё ждут человека ───
// apply=null ровно когда применять нечего: статус остался proposed. Плашка подтверждает,
// что решение записано, и называет оставшуюся долю: заголовок «Решение записано. Ещё N
// спорных ждут вас», текст «Они ниже — решите их, и пакет применится целиком.»
// (формулировки — из карточки T2; условие показа плашки не менялось).

async function settle(times = 10) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

describe('плашка смешанного пакета: решение записано, остальные ждут (T2)', () => {
  it('решение одной из трёх (apply=null): «Решение записано. Ещё 2 спорных ждут вас»', async () => {
    const three = [op({}), op({ index: 1 }), op({ index: 2 })];
    // ответ бэка на «Принять» по op0: op0 решена человеком, op1–op2 остались,
    // apply=null (применять нечего — статус остался proposed)
    const afterFirst = { ...pkg({}), operations: [op({ humanDecision: 'accepted' }), op({ index: 1 }), op({ index: 2 })] };
    req.mockImplementation((_url: string, init?: { method?: string }) =>
      init?.method === 'POST' ? Promise.resolve({ package: afterFirst, apply: null }) : Promise.resolve(null));
    const c = renderOps(three);
    await settle();
    const accept = findNodeByAttr(c, 'data-testid', 'wiki-op-accept-0');
    expect(accept).not.toBeNull();
    act(() => { (reactProps(accept!) as { onClick: (e: unknown) => void }).onClick(undefined); });
    await settle();
    expect(all(c)).toContain('Решение записано. Ещё 2 спорных ждут вас');
    expect(all(c)).toContain('Они ниже — решите их, и пакет применится целиком.');
    // транспорт возвращаем к прежнему поведению файла — следующий тест не наследует мок
    req.mockImplementation(() => Promise.resolve(null));
  });
});

// ─── Операция merge (C7): строка «A → B» ───
// merge: page — путь A (сливаемая страница), target — путь B (куда вливают), text — null
// (контракт C2, WikiPackage.cs:32-35). Бейдж «слить страницы» — из OP_META (тот же словарь
// читает список «Решения судьи» в WikiInbox:275, поэтому «Решения» покрыты тем же ключом).
// Имена сторон — без префикса раздела, как имя страницы в шапке карточки. Неизвестный op
// по-прежнему не роняет рендер: карточка есть, бейджа операции нет.

describe('операция merge: «A → B» и неизвестный op (C7)', () => {
  it('merge: бейдж «слить страницы» и строка «Мелкая → Крупная»', () => {
    const c = render(op({ op: 'merge', page: 'projects/p1/Мелкая', target: 'projects/p1/Крупная', text: null }));
    expect(all(c)).toContain('слить страницы');
    const row = nodeText(c, 'wiki-op-merge');
    expect(row).not.toBeNull();
    // порядок: A слева, B справа
    expect(row).toContain('Мелкая → Крупная');
    // текста нет → блока «Предполагаемое изменение» тоже нет: fallback «− target»
    // для merge закрыт (B не удаляется, в него вливают)
    expect(all(c)).not.toContain('Предполагаемое изменение');
  });

  it("неизвестный op 'zzz': карточка рендерится, исключения нет", () => {
    const c = render(op({ op: 'zzz', target: 'Неизвестная операция' }));
    expect(opRowNodes(c)).toHaveLength(1);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-op-merge')).toBeNull();
  });
});

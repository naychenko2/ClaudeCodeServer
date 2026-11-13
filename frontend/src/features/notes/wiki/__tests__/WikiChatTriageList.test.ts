// Рендер-тест блока «Отбор чатов» (S7d-2): каждая известная зона — со своей подписью, неизвестная
// — нейтральной; rejected_skipped (C2R-Sh2) — свой бейдж «пропущено после 3 отказов», причина
// отказа и действия строки на месте; awaiting_human (C2R-Sh2b) — свой бейдж «ждёт решения во
// «Входящих»», бэковая причина общим путём; причина — бэковая строка; «Почему:» без кнопок «Разобрать»
// (их в срезе нет, «Разобрать этот чат» — меню чата);
// клик «Не разбирать» — ровно один PUT {mark:'skip'}, повторный снимает (null); «Важный» и
// «Не разбирать» взаимоисключающие (один PUT, пометка перекрывает бейдж зоны); чат исполнения
// задач — кнопки disabled с подсказкой и без действия; 200 чатов рендерятся; пустой список —
// пустое состояние; отказ чтения — плашка с «Повторить»; отказ пометки — текст по коду, строка
// не меняется;
// действие важнее пометки (возврат QA-2, дефект 82567033): в action-зоне (awaiting_human,
// rejected_skipped) бейдж действия стоит ПЕРВЫМ, пометка — вторым бейджем строки и не теряется;
// в обычной зоне (triage/skipped) пометка по-прежнему одна и перекрывает зону.
//
// M1b (этот шаг) меняет два текста этого файла: шапка рамки — «Показано N из M» (ассертция
// «200 чатов» стала «Показано 200 из 200»), балл — справа от строки названия, без балла —
// «без балла» (вместо «—»). Рамка, шапка и карточка как целое проверяются в
// wikiChatTriageFilter.test.ts (describe «M1b»).
//
// Мокан только транспорт (offline.request, signalr) — тот же приём, что WikiJournal.test/WikiApplySettings.test:
// «один вызов setWikiChatMark» проверяется телом PUT, которое реально ушло на прово; сервер PUT
// отвечает {mark} как бэк (WikiChatsController.Mark) и меняет состояние, которое читает GET.
//
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) doMark — ставить пометку локально, не вызывая setWikiChatMark → «ровно один PUT» краснеет
//     (проверяемое «пометка через стор»);
// (2) не включать кнопки у чата исполнения задач (task не трогать) → «disabled и 0 PUT» краснеет;
// (3) в rowMetaOf не перекрывать зону пометкой (всегда ZONE_META[zone]) → «бейдж «важный»/
//     «не разбирать»» краснеют;
// (4) убрать проверку task в onClick (дать действие чату исполнения) → «0 PUT по клику» краснеет;
// (5) в подписях зон слить skipped и nothing_new в одну → «нечего добавить» краснеет;
// (6) в ZONE_META удалить запись awaiting_human → «строка awaiting_human» краснеет (Sh6d);
// (7) в rowBadgesOf вернуть «пометка важнее зоны» для всех зон (убрать ветку ACTION_ZONES) →
//     «действие поверх пометки» краснеет (Sh6d, дефект 82567033).

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiChatTriageList } from '../WikiChatTriageList';

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

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

type Item = {
  sessionId: string; name: string | null; zone: string; score: number | null;
  signals: unknown; reason: string | null; nothingNewStreak: number; triagedAt: string;
  running: boolean; cursor: number; chroniclePath: string | null; mark: string | null;
};

// Строка как её отдаёт GET /api/wiki/chats (WikiChatTriageDto): зона — показанная (ShownZone),
// причина — бэковая строка ступени; triagedAt — полдень UTC: дата строки не должна зависеть
// от часового пояса прогона
const item = (over: Partial<Item>): Item => ({
  sessionId: 'c1', name: 'План шагов LLM-wiki', zone: 'triage', score: 31,
  signals: null, reason: 'балл 31: формулировки решений (+8), реплики человека (+6)',
  nothingNewStreak: 0, triagedAt: '2026-11-01T12:00:00Z', running: false, cursor: 0,
  chroniclePath: null, mark: null, ...over,
});

// Литерал бэка (WikiChatScorer.cs:167) — тот, по которому фронт узнаёт чат исполнения задач
const TASK_REASON = 'ступень 0: чат исполнения задачи';

let serverItems: Item[] = [];
function serveChats(): void {
  req.mockImplementation(async (url: string, init?: RequestInit) => {
    const method = (init?.method ?? 'GET').toUpperCase();
    if (method === 'GET' && url === '/wiki/chats') return { items: serverItems, counts: {} };
    const m = /^\/wiki\/chats\/([^/]+)\/mark$/.exec(url);
    if (method === 'PUT' && m) {
      const id = decodeURIComponent(m[1]);
      const body = JSON.parse((init?.body ?? '{}') as string);
      // бэк: пометка живёт на чате, ответ — принятое значение (Mark, :196-218)
      serverItems = serverItems.map(it => (it.sessionId === id ? { ...it, mark: body.mark ?? null } : it));
      return { mark: body.mark ?? null };
    }
    return null;
  });
}

const roots: Root[] = [];

function render(): Cont {
  installFakeDom();
  const { root, container } = mount(createElement(WikiChatTriageList));
  roots.push(root);
  return container;
}

async function settle(times = 20) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

function texts(container: AnyNode): string[] {
  return textNodes(container).map(t => t.trim()).filter(Boolean);
}

function has(container: AnyNode, needle: string): boolean {
  return texts(container).join(' ').includes(needle);
}

function countByTestId(container: AnyNode, testId: string): number {
  let n = 0;
  const walk = (x: AnyNode) => {
    if (x.nodeType === 1 && x.getAttribute('data-testid') === testId) n += 1;
    for (const c of x.childNodes) walk(c);
  };
  walk(container);
  return n;
}

function click(node: AnyNode, what: string): void {
  const props = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!props?.onClick) throw new Error(`у «${what}» нет onClick`);
  act(() => { props.onClick(undefined); });
}

const putCalls = (): unknown[][] => req.mock.calls.filter((c) => ((c[1] as RequestInit | undefined)?.method ?? 'GET') === 'PUT');
const sentBody = (call: unknown[]): Record<string, unknown> => JSON.parse(((call[1] as RequestInit).body ?? '') as string);
const markBtn = (c: AnyNode, kind: 'important' | 'skip', id: string): AnyNode | null =>
  findNodeByAttr(c, 'data-testid', `wiki-triage-mark-${kind}-${id}`);
const pressed = (node: AnyNode | null): string | null => node?.getAttribute('aria-pressed') ?? null;

beforeEach(() => {
  req.mockReset();
  serverItems = [];
  serveChats();
});

afterEach(() => {
  for (const root of roots.splice(0)) void root.unmount();
  uninstallFakeDom();
});

describe('блок «Отбор чатов» (S7d-2)', () => {
  it('каждая зона — со своей подписью; неизвестная — нейтральной; причина — бэковая строка', async () => {
    serverItems = [
      item({ sessionId: 'z1', name: 'Разбирается', zone: 'triage', score: 31 }),
      item({ sessionId: 'z2', name: 'Серая', zone: 'gray', score: 11, reason: 'балл 11: реплики человека (+6)' }),
      item({ sessionId: 'z3', name: 'Пропущен', zone: 'skipped', score: 2, reason: 'балл 2: реплики человека (+2)' }),
      item({ sessionId: 'z4', name: 'Пустой', zone: 'nothing_new', score: 4, reason: 'нет новых утверждений' }),
      item({ sessionId: 'z5', name: 'Короткий', zone: 'excluded', score: 0, reason: 'ступень 0: диалог короче 1500 символов' }),
      item({ sessionId: 'z6', name: 'Будущее', zone: 'quantum', score: null, reason: null }),
    ];
    const c = render();
    await settle();
    expect(has(c, 'разбирается')).toBe(true);
    expect(has(c, 'серая зона')).toBe(true);
    expect(has(c, 'пропущено')).toBe(true);
    expect(has(c, 'нечего добавить')).toBe(true);
    expect(has(c, 'не разбирается')).toBe(true);
    // неизвестная зона — нейтральная подпись, строка на месте (не отказ и не выдумка)
    expect(has(c, 'неизвестное состояние')).toBe(true);
    // балл — справа от строки названия (M1b, П3: колонки балла нет); без балла — «без балла»;
    // «Почему:» — метка причины (макет)
    expect(has(c, 'без балла')).toBe(true);
    expect(has(c, 'Почему: балл 31: формулировки решений')).toBe(true);
    // кнопок «Разобрать» в списке нет — в срезе их нет вовсе («Не разбирать» — не «Разобрать»)
    expect(texts(c).some(t => t === 'Разобрать')).toBe(false);
    expect(texts(c).some(t => t === 'Разобрать этот чат')).toBe(false);
  });

  it('строка rejected_skipped: свой бейдж и причина отказа, действия строки на месте, «неизвестного состояния» нет', async () => {
    // одна строка этой зоны — без «кванта» из общего кейса: «неизвестное состояние» в рендере
    // не должно быть вовсе (для этой зоны словарь даёт свою подпись, не UNKNOWN_ZONE)
    serverItems = [item({ sessionId: 'r1', name: 'Три отказа', zone: 'rejected_skipped', score: 9, reason: 'отказ: модель не ответила' })];
    const c = render();
    await settle();
    expect(has(c, 'пропущено после 3 отказов')).toBe(true);
    expect(has(c, 'Почему: отказ: модель не ответила')).toBe(true);
    expect(has(c, 'неизвестное состояние')).toBe(false);
    // действия строки не тронуты: пометки на месте; «Разобрать этот чат» — пункт меню чата
    // (manifest.tsx), в списке его нет по решению S7d-2
    expect(markBtn(c, 'important', 'r1')).toBeTruthy();
    expect(markBtn(c, 'skip', 'r1')).toBeTruthy();
    expect(texts(c).some(t => t === 'Разобрать')).toBe(false);
  });

  it('строка awaiting_human: свой бейдж и бэковая причина, «неизвестного состояния» нет', async () => {
    // зона C2R-Sh2b (ZoneAwaitingHuman): чат, чей разобранный исход — пакет во «Входящих»,
    // ждущий решения человека. Причина — бэковый литерал (WikiChatIngestService.cs:527),
    // показывается общим путём, как у остальных зон; подпись — своя, не UNKNOWN_ZONE
    serverItems = [item({ sessionId: 'a1', name: 'Суперсид', zone: 'awaiting_human', score: 12, reason: '2 операции на решении' })];
    const c = render();
    await settle();
    // подпись зоны — точный текстовый узел бейджа: переформулированную подпись тест ловит
    // именно точным узлом, а не подстрокой «Почему: …»
    expect(texts(c).some(t => t === 'ждёт решения во «Входящих»')).toBe(true);
    expect(has(c, 'Почему: 2 операции на решении')).toBe(true);
    expect(has(c, 'неизвестное состояние')).toBe(false);
    // действия строки не тронуты: пометки на месте
    expect(markBtn(c, 'important', 'a1')).toBeTruthy();
    expect(markBtn(c, 'skip', 'a1')).toBeTruthy();
  });

  it('действие поверх пометки: «важный» + awaiting_human — бейдж действия первым, пометка вторым бейджем', async () => {
    // возврат QA-2 (дефект 82567033): пометка не должна прятать действие, требующее человека.
    // Порядок проверяется точными узлами по документу: бейдж действия обязан идти раньше
    // бейджа пометки (кнопка «Важный» — с заглавной, её текст в сравнение не попадает)
    serverItems = [item({ sessionId: 'am1', name: 'Важный ожидающий', zone: 'awaiting_human', score: 12, reason: '2 операции на решении', mark: 'important' })];
    const c = render();
    await settle();
    const ts = texts(c);
    const iAction = ts.indexOf('ждёт решения во «Входящих»');
    const iMark = ts.indexOf('важный');
    expect(iAction).toBeGreaterThanOrEqual(0);
    expect(iMark).toBeGreaterThan(iAction);
    expect(has(c, 'Почему: 2 операции на решении')).toBe(true);
    // пометка не потеряна: её кнопка остаётся нажатой
    expect(pressed(markBtn(c, 'important', 'am1'))).toBe('true');
  });

  it('действие поверх пометки: «не разбирать» + rejected_skipped — бейдж действия первым, пометка вторым бейджем', async () => {
    // та же проверка для второй action-зоны и другой пометки: «не разбирать» (строчная) —
    // узел бейджа, кнопка строки — «Не разбирать» (заглавная), узлы не пересекаются
    serverItems = [item({ sessionId: 'rs1', name: 'Отвергнутый важный', zone: 'rejected_skipped', score: 9, reason: 'отказ: модель не ответила', mark: 'skip' })];
    const c = render();
    await settle();
    const ts = texts(c);
    const iAction = ts.indexOf('пропущено после 3 отказов');
    const iMark = ts.indexOf('не разбирать');
    expect(iAction).toBeGreaterThanOrEqual(0);
    expect(iMark).toBeGreaterThan(iAction);
    expect(has(c, 'Почему: отказ: модель не ответила')).toBe(true);
    expect(pressed(markBtn(c, 'skip', 'rs1'))).toBe('true');
  });

  it('клик «Не разбирать» — ровно один PUT {mark:"skip"}; повторный снимает (null)', async () => {
    serverItems = [item({ sessionId: 'm1', zone: 'triage', mark: null })];
    const c = render();
    await settle();
    const btn = markBtn(c, 'skip', 'm1')!;
    click(btn, 'Не разбирать');
    await settle();
    const puts = putCalls();
    expect(puts.length).toBe(1);
    expect(sentBody(puts[0])).toEqual({ mark: 'skip' });
    // пометка принята: кнопка нажата, бейдж строки — решение человека, а не прежняя зона
    expect(pressed(markBtn(c, 'skip', 'm1'))).toBe('true');
    expect(has(c, 'не разбирать')).toBe(true);
    expect(has(c, 'разбирается')).toBe(false);
    click(markBtn(c, 'skip', 'm1')!, 'Не разбирать');
    await settle();
    const puts2 = putCalls();
    expect(puts2.length).toBe(2);
    expect(sentBody(puts2[1])).toEqual({ mark: null });
    expect(pressed(markBtn(c, 'skip', 'm1'))).toBe('false');
    // без пометки строка снова показывает автоматическую зону
    expect(has(c, 'разбирается')).toBe(true);
  });

  it('«Важный» и «Не разбирать» взаимоисключающие: один PUT, бейдж — «важный»', async () => {
    serverItems = [item({ sessionId: 'm2', zone: 'skipped', score: 2, mark: 'skip' })];
    const c = render();
    await settle();
    // прежняя пометка видна: skip нажат, «важный» — бейдж строки
    expect(pressed(markBtn(c, 'skip', 'm2'))).toBe('true');
    expect(has(c, 'не разбирать')).toBe(true);
    click(markBtn(c, 'important', 'm2')!, 'Важный');
    await settle();
    const puts = putCalls();
    // ровно один запрос: снятие skip отдельным вызовом не делается — значение одно, его ставит бэк
    expect(puts.length).toBe(1);
    expect(sentBody(puts[0])).toEqual({ mark: 'important' });
    expect(pressed(markBtn(c, 'important', 'm2'))).toBe('true');
    expect(pressed(markBtn(c, 'skip', 'm2'))).toBe('false');
    expect(has(c, 'важный')).toBe(true);
    expect(has(c, 'пропущено')).toBe(false);
    // и на сервере пометка одна
    expect(serverItems[0].mark).toBe('important');
  });

  it('чат исполнения задач: кнопки disabled с подсказкой, клик — ни одного PUT', async () => {
    serverItems = [item({ sessionId: 't1', name: 'LLM-wiki · M2: макет', zone: 'excluded', score: 0, reason: TASK_REASON })];
    const c = render();
    await settle();
    const bi = markBtn(c, 'important', 't1')!;
    const bs = markBtn(c, 'skip', 't1')!;
    expect((reactProps(bs) as { disabled?: boolean }).disabled).toBe(true);
    expect((reactProps(bi) as { disabled?: boolean }).disabled).toBe(true);
    expect((reactProps(bs) as { title?: string }).title).toContain('Чат исполнителя задачи');
    // действия у строки нет вовсе: клик по attrs не пороет запрос
    expect((reactProps(bs) as { onClick?: unknown }).onClick).toBeUndefined();
    expect(putCalls().length).toBe(0);
    expect(has(c, 'не разбирается')).toBe(true);
  });

  it('200 чатов рендерятся без ошибки', async () => {
    serverItems = Array.from({ length: 200 }, (_, i) =>
      item({ sessionId: `bulk-${i}`, name: `Чат ${i}`, zone: i % 2 ? 'gray' : 'triage', score: i }));
    const c = render();
    await settle();
    expect(countByTestId(c, 'wiki-triage-row')).toBe(200);
    // M1b: шапка рамки — «Показано N из M»; здесь показано всё: 200 из 200
    expect(has(c, 'Показано 200 из 200')).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-triage-error')).toBeNull();
  });

  it('пустой список — пустое состояние; отказ чтения — плашка с «Повторить»', async () => {
    serverItems = [];
    const c = render();
    await settle();
    expect(has(c, 'Чатов пока нет')).toBe(true);

    req.mockImplementation(async () => {
      throw Object.assign(new Error('HTTP 409'), { status: 409, body: { code: 'wiki_triage_invalid', message: 'файл разбора чатов не читается' } });
    });
    const c2 = render();
    await settle();
    expect(findNodeByAttr(c2, 'data-testid', 'wiki-triage-error')).toBeTruthy();
    expect(has(c2, 'Файл разбора чатов не читается')).toBe(true);
    expect(has(c2, 'Повторить')).toBe(true);
  });

  it('отказ пометки — текст по коду, состояние строки не меняется', async () => {
    serverItems = [item({ sessionId: 'e1', zone: 'triage', mark: null })];
    const c = render();
    await settle();
    req.mockImplementation(async () => {
      throw Object.assign(new Error('HTTP 409'), { status: 409, body: { code: 'wiki_llm_wiki_off', message: 'пометка не сохранена: функция выключена' } });
    });
    click(markBtn(c, 'skip', 'e1')!, 'Не разбирать');
    await settle();
    expect(has(c, 'Вики выключена в настройках')).toBe(true);
    // оптимизма нет: строка осталась без пометки
    expect(pressed(markBtn(c, 'skip', 'e1'))).toBe('false');
    expect(has(c, 'не разбирать')).toBe(false);
  });
});

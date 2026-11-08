// Рендер-тест блока «Ночной разбор» (F2, срез «Ф2 02.11»; F4 — формулировки итога ночи,
// хвосты R-F2 (1)–(4)):
// — нет итога: карточка «ещё не было» и текст по состоянию тумблера (окно — из настроек;
//   без обещания «сегодня» — серверного часа у фронта нет);
// — четыре итога рендерят ожидаемые строки с числами lastNight (selected/analyzed/applied/
//   toInbox) и оценочным расходом «≈ N из M» — против ТЕКУЩЕГО бюджета: после его смены
//   строка и полоса считают новое значение, не то, что было при той ночи;
// — window_closed / disabled_midway / error — info-тексты без кнопки «Входящие»;
//   disabled_midway при analyzed=0 — «до первого чата», не «на 0-м чате»;
// — «Чем разбирать» — только текст: ноль select/input/button внутри блока;
// — клик по тумблеру: оптимистично до ответа и ровно один PUT {nightly:{enabled:true}};
// — 400 сохранения: текст записки, и стор откатил оптимистичное (тумблер снова выключен);
// — ошибки ввода (равные часы, выше потолка) — текст у поля, PUT'а нет;
// — успешный путь: валидное число (пакеты за ночь/на раздел, бюджет) и смена часа окна —
//   ровно один PUT, в теле только изменённые поля (окно — обе границы, как делает tryWindow).
// Стор настоящий (тот же приём, что WikiApplySettings.test): блок только вызывает его,
// тесты смотрят тело, реально ушедшее на прово.
//
// Мутации этого прогона (вывод — в отчёт задачи):
// (1) в onChange тумблера не вызывать putWikiNightly → «ровно один PUT» краснеет;
// (2) в LastNightBlock не показывать предупреждение open_limit → «Остановились:…» и
//     «Больше 20 спорных…» краснеют;
// (3) в completed не показывать строку «Разобрано всё отобранное.» → «completed» краснеет;
// (4) в commitNum не вызывать putWikiNightly → тесты валидных чисел краснеют (0 PUT'ов);
// (5) вернуть старую строку причины disabled_midway («остановились на N-м чате») →
//     тест window_closed/disabled_midway/error краснеет (проверено, вывод — в отчёте F4);
// — отдельной мутации для «против текущего бюджета» нет: тест смены доказывает это сам —
//   после PUT проверяется новое значение и отсутствие старого в строке расхода.
//
// Доработка LI-3 (дефект Веры a1f88b4a): вики заводится тем же PUT'ом, что включает
// тумблер (LI-2) — после подтверждённого включения блок перечитывает дерево, и подпись
// «вики ещё не создана» уходит без перезагрузки; текст подписи называет это же.
// (6) убрать перечитку дерева после PUT в onChange тумблера → «включение тумблера…»
//     краснеет (одна запись /wiki/tree + живая подпись);
// (7) снять .catch у перечитки → «отказ перечитки…» краснеет (плашка «Не удалось
//     сохранить настройки» на сохранённые настройки);
// (8) вернуть старый текст подписи («создастся при первом разборе») → тесты подписи
//     краснеют.
//
// WN-6b (план 0ab580a7): «Запустить сейчас» рядом с заголовком итога — диалог подтверждения
// с лимитом пакетов из снимка; «Запустить» → ровно один POST /wiki/settings/nightly/run,
// «Отмена» → 0; после успеха — оптимистичное состояние стора («Идёт разбор…», WN-6a);
// nightlyRunning / выключенный тумблер → кнопка disabled («Идёт разбор…» / «Ночной разбор
// выключен»); rejected>0 → «отказано N» в строке чисел, 0 или отсутствие поля — слова нет;
// 409 wiki_nightly_disabled → текст отказа у кнопки, кнопка снова активна, опрос не стартует.
// (9) не ставить disabled при nightlyRunning → «nightlyRunning:…» и «клик…:…» краснеют
//     (в «клике» — строка «кнопка выключена после успеха»);
// (10) не добавлять ячейку rejected в итог → «rejected=2:…» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiNightlyBlock } from '../WikiNightlyBlock';

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
import { __test as wikiStore, getWikiSettingsSnapshot, getWikiTreeSnapshot, loadWikiTree } from '../../../../lib/wiki';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

// Настройки как их отдаёт GET /api/wiki/settings (контракт NA-2/N7c): ночной блок целым
// всегда, с умолчаниями; lastNight — только из GET, файла нет — null
const NIGHTLY = {
  enabled: false, windowStartHour: 3, windowEndHour: 6,
  maxPackagesPerNight: 20, maxPackagesPerSection: 6, maxChatsPerSection: 10,
  inputTokenBudget: 700000,
};

function settings(over: { nightly?: Partial<typeof NIGHTLY>; lastNight?: unknown; nightlyRunning?: boolean } = {}): Record<string, unknown> {
  const s: Record<string, unknown> = {
    autoApplyEnabled: false,
    classes: [],
    nightly: { ...NIGHTLY, ...(over.nightly ?? {}) },
  };
  if ('lastNight' in over) s.lastNight = over.lastNight;
  // WN-5: GET несёт состояние прохода всегда; здесь — только когда тест его задаёт
  if (over.nightlyRunning !== undefined) s.nightlyRunning = over.nightlyRunning;
  return s;
}

// lastNight — форма файла keeper/nightly-last.json (план N7b): date 'yyyy-MM-dd' без
// времени; числа примеров макета (7/7/11/2, 512k) — чтобы тексты теста сверялись с макетом
const LAST = (over: Record<string, unknown>): Record<string, unknown> => ({
  date: '2026-10-27', selected: 7, analyzed: 7, applied: 11, toInbox: 2, estTokens: 512000,
  outcome: 'completed', ...over,
});

const httpError = (status: number, body?: unknown): Error =>
  Object.assign(new Error(`HTTP ${status}`), { status, body });

let server: Record<string, unknown> = settings();
function serveSettings(): void {
  req.mockImplementation(async (url: string, init?: RequestInit) => {
    const method = (init?.method ?? 'GET').toUpperCase();
    // run (WN-5): 202 {started:true} — проход начат, его читает опрос GET
    if (url === '/wiki/settings/nightly/run') return { started: true };
    if (url !== '/wiki/settings') return null;
    if (method === 'PUT') {
      const body = JSON.parse((init?.body ?? '{}') as string);
      if (body.nightly !== undefined) {
        // названное поле — менять (частичный мердж, WikiSettingsController)
        const patch = Object.fromEntries(
          Object.entries(body.nightly as Record<string, unknown>).filter(([, v]) => v !== null));
        server = { ...server, nightly: { ...(server.nightly as Record<string, unknown>), ...patch } };
      }
      // ответ PUT — формы реального бэка: lastNight в него не входит (N7c — только GET)
      return { ...server, lastNight: null };
    }
    return server;
  });
}

const roots: Root[] = [];

function render(props?: { onOpenInbox?: () => void; onOpenJournal?: () => void }): Cont {
  installFakeDom();
  const { root, container } = mount(createElement(WikiNightlyBlock, props));
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

function click(node: AnyNode, what: string): void {
  const props = reactProps(node) as { onClick?: (e: unknown) => void } | null;
  if (!props?.onClick) throw new Error(`у «${what}» нет onClick`);
  act(() => { props.onClick(undefined); });
}

function callProp(node: AnyNode, name: string, arg?: unknown): void {
  const props = reactProps(node) as Record<string, ((e: unknown) => void) | undefined> | null;
  const fn = props?.[name];
  if (!fn) throw new Error(`у контрола нет ${name}`);
  act(() => { fn(arg); });
}

const putCalls = (): unknown[][] => req.mock.calls.filter((c) => ((c[1] as RequestInit | undefined)?.method ?? 'GET') === 'PUT');
const sentBody = (call: unknown[]): Record<string, unknown> => JSON.parse(((call[1] as RequestInit).body ?? '') as string);

beforeEach(() => {
  wikiStore.reset();
  req.mockReset();
  server = settings();
  serveSettings();
});

afterEach(() => {
  for (const root of roots.splice(0)) void root.unmount();
  uninstallFakeDom();
  wikiStore.reset();
});

describe('блок «Ночной разбор» (F2)', () => {
  it('нет итога: выкл. — «ещё не было» и подсказка включить; вкл. — первая ночь в этом окне', async () => {
    const c = render();
    await settle();
    expect(has(c, 'Ночного разбора ещё не было')).toBe(true);
    expect(has(c, 'Включите разбор — первая ночь пройдёт в ближайшее окно, утром придёт уведомление с итогом.')).toBe(true);
    expect(has(c, 'Ночью ничего не разбирается. Отбор чатов всё равно считается — его видно в «Отборе чатов».')).toBe(true);
    // поля видны и при выключенном (затемнены, но правятся — затемнение это стиль, не отсутствие)
    expect(has(c, 'Пакетов за ночь')).toBe(true);
    expect(has(c, 'Бюджет за ночь')).toBe(true);

    server = settings({ nightly: { enabled: true } });
    const c2 = render();
    await settle();
    expect(has(c2, 'Первый пройдёт ближайшей ночью с 03:00 до 06:00. Утром придёт уведомление: что применилось само и что ждёт вас.')).toBe(true);
    expect(has(c2, 'Первый пройдёт сегодня')).toBe(false);
    expect(has(c2, 'Ночью ничего не разбирается')).toBe(false);
    expect(has(c2, 'Включите разбор')).toBe(false);
  });

  it('completed: карточка с числами и оценочным расходом, без предупреждений', async () => {
    server = settings({ lastNight: LAST({}) });
    const c = render();
    await settle();
    expect(has(c, '· 26 → 27.10')).toBe(true);
    expect(has(c, '7 чатов отобрано')).toBe(true);
    expect(has(c, '11 пакетов применено само')).toBe(true);
    expect(has(c, '2 ждут вас во «Входящих»')).toBe(true);
    expect(has(c, '≈ 512 000 из 700 000')).toBe(true);
    expect(has(c, 'Разобрано всё отобранное.')).toBe(true);
    expect(has(c, 'Не хватило бюджета')).toBe(false);
    expect(has(c, 'Остановились')).toBe(false);
  });

  it('смена бюджета: «≈ N из M» и полоса против нового значения, не прежнего', async () => {
    server = settings({ lastNight: LAST({}) });
    const c = render();
    await settle();
    expect(has(c, '≈ 512 000 из 700 000')).toBe(true);
    const budgetInput = findNodeByAttr(c, 'data-testid', 'wiki-nightly-input-inputTokenBudget')!
      .querySelectorAll('input')[0];
    callProp(budgetInput, 'onFocus');
    callProp(budgetInput, 'onChange', { target: { value: '1000000' } });
    await settle();
    callProp(budgetInput, 'onBlur');
    await settle();
    const puts = putCalls();
    expect(puts.length).toBe(1);
    expect(sentBody(puts[0])).toEqual({ nightly: { inputTokenBudget: 1000000 } });
    expect(getWikiSettingsSnapshot()!.nightly.inputTokenBudget).toBe(1000000);
    // карточка итога остаётся в снимке: PUT не несёт lastNight, он сохраняется от GET-чтения
    // (wiki.ts:1084) — и расход после смены считается НОВЫМ значением (решение F4)
    expect(has(c, '≈ 512 000 из 1 000 000')).toBe(true);
    expect(has(c, '≈ 512 000 из 700 000')).toBe(false);
  });

  it('budget: предупреждение с числами чатов и полным расходом', async () => {
    server = settings({ lastNight: LAST({ selected: 14, analyzed: 9, applied: 13, toInbox: 3, estTokens: 700000, outcome: 'budget' }) });
    const c = render();
    await settle();
    expect(has(c, 'Ночь остановилась на своём лимите')).toBe(true);
    expect(has(c, 'Разобрали 9 чатов из 14 отобранных. Остальные 5 разберём следующей ночью — сначала они. Лимиты заданы в «Пакетах за ночь» и «Бюджете за ночь» — если так каждую ночь, проверьте их.')).toBe(true);
    // старой формулировки-совета про бюджет в тексте нет: она не применима к упору в лимит пакетов
    expect(has(c, 'поднимите бюджет')).toBe(false);
    expect(has(c, '≈ 700 000 из 700 000')).toBe(true);
    expect(has(c, '13 пакетов применено само')).toBe(true);
  });

  it('open_limit: предупреждение с потолком и кнопка «Открыть «Входящие»» зовёт контейнер', async () => {
    server = settings({ lastNight: LAST({ selected: 12, analyzed: 4, applied: 5, toInbox: 6, estTokens: 218000, outcome: 'open_limit' }) });
    let inbox = 0;
    const c = render({ onOpenInbox: () => { inbox += 1; }, onOpenJournal: () => {} });
    await settle();
    expect(has(c, 'Остановились: во «Входящих» 20 пакетов ждут вашего решения')).toBe(true);
    expect(has(c, 'Больше 20 спорных пакетов сразу Хранитель не копит — иначе их не разобрать. Решите часть, и следующая ночь продолжит с того же места.')).toBe(true);
    expect(has(c, '≈ 218 000 из 700 000')).toBe(true);
    click(findNodeByAttr(c, 'data-testid', 'wiki-nightly-open-inbox')!, 'Открыть «Входящие»');
    expect(inbox).toBe(1);
    click(findNodeByAttr(c, 'data-testid', 'wiki-nightly-open-journal')!, 'Открыть «Журнал»');
  });

  it('window_closed / disabled_midway / error: info-тексты без кнопки «Входящие»', async () => {
    server = settings({ lastNight: LAST({ outcome: 'window_closed' }) });
    const c = render();
    await settle();
    expect(has(c, 'Окно закончилось раньше, чем разбор: остальное следующей ночью.')).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-nightly-open-inbox')).toBeNull();

    server = settings({ lastNight: LAST({ analyzed: 3, outcome: 'disabled_midway' }) });
    const c2 = render();
    await settle();
    expect(has(c2, 'Вы выключили разбор посреди ночи: 3 чата разобрано, чего не успели — следующей ночью.')).toBe(true);
    expect(has(c2, 'остановились на 3-м чате')).toBe(false);

    // отключение до первого чата (CheckStop перед каждым чатом): «на 0-м чате» не про что
    server = settings({ lastNight: LAST({ selected: 0, analyzed: 0, applied: 0, toInbox: 0, outcome: 'disabled_midway' }) });
    const c4 = render();
    await settle();
    expect(has(c4, 'Вы выключили разбор до первого чата — ничего не разобрано.')).toBe(true);
    expect(has(c4, '0-м чате')).toBe(false);

    server = settings({ lastNight: LAST({ outcome: 'error' }) });
    const c3 = render();
    await settle();
    expect(has(c3, 'Ночь прервалась из-за сбоя. Повторим следующей ночью.')).toBe(true);
  });

  it('«Чем разбирать» — текст без контролов: ноль select/input/button внутри блока', async () => {
    const c = render();
    await settle();
    const eng = findNodeByAttr(c, 'data-testid', 'wiki-nightly-engine')!;
    expect(eng.querySelectorAll('select,button,input').length).toBe(0);
    expect(texts(eng).includes('Отбор спорных чатов')).toBe(true);
    expect(has(c, 'Назначает администратор в «Поставщиках моделей».')).toBe(true);
  });

  it('клик по тумблеру: оптимистично до ответа и ровно один PUT {nightly:{enabled:true}}', async () => {
    const c = render();
    await settle();
    const sw = findNodeByAttr(c, 'data-testid', 'wiki-nightly-switch')!;
    const toggle = findNodeByAttr(sw, 'role', 'switch')!;
    // по умолчанию — выключен (умолчание NA-2)
    expect(toggle.getAttribute('aria-checked')).toBe('false');
    click(toggle, 'Разбирать по ночам');
    // до ответа: новое значение уже в сторе — UI тумблера не ждёт сети (мутация 1)
    expect(getWikiSettingsSnapshot()!.nightly.enabled).toBe(true);
    await settle();
    const puts = putCalls();
    expect(puts.length).toBe(1);
    expect(sentBody(puts[0])).toEqual({ nightly: { enabled: true } });
    expect(getWikiSettingsSnapshot()!.nightly.enabled).toBe(true);
    expect(toggle.getAttribute('aria-checked')).toBe('true');
  });

  it('400 сохранения: текст записки, оптимистичное откатано, тумблер снова выключен', async () => {
    const c = render();
    await settle();
    req.mockImplementation(async (_url: string, init?: RequestInit) => {
      if ((init?.method ?? 'GET').toUpperCase() === 'PUT') throw httpError(400, { code: 'wiki_nightly', message: 'merged-блок ночного раздела кривой' });
      return server;
    });
    const sw = findNodeByAttr(c, 'data-testid', 'wiki-nightly-switch')!;
    const toggle = findNodeByAttr(sw, 'role', 'switch')!;
    click(toggle, 'Разбирать по ночам');
    expect(getWikiSettingsSnapshot()!.nightly.enabled).toBe(true); // оптимистично
    await settle();
    expect(has(c, 'Не сохранилось — попробуйте ещё раз.')).toBe(true);
    // откат стора (putWikiNightly, F1): снимок и тумблер — прежние
    expect(getWikiSettingsSnapshot()!.nightly.enabled).toBe(false);
    expect(toggle.getAttribute('aria-checked')).toBe('false');
  });

  it("ошибки ввода: равные часы и 25 > 20 — текст у поля, PUT нет", async () => {
    const c = render();
    await settle();
    // Окно: конец = началу (3) — ошибка у поля, запроса нет
    const win = findNodeByAttr(c, 'data-testid', 'wiki-nightly-window')!;
    const selects = win.querySelectorAll('select');
    expect(selects.length).toBe(2);
    callProp(selects[1], 'onChange', { target: { value: '3' } });
    await settle();
    expect(has(c, 'Начало и конец окна совпадают')).toBe(true);
    expect(putCalls().length).toBe(0);

    // «Пакетов за ночь» 25 > 20: ошибка у поля при наборе; blur не отправляет невалидное
    const wrap = findNodeByAttr(c, 'data-testid', 'wiki-nightly-input-maxPackagesPerNight')!;
    const input = wrap.querySelectorAll('input')[0];
    callProp(input, 'onFocus');
    callProp(input, 'onChange', { target: { value: '25' } });
    await settle();
    expect(has(c, 'Не больше 20 — иначе ночь упрётся в лимит «Входящих»')).toBe(true);
    callProp(input, 'onBlur');
    await settle();
    expect(putCalls().length).toBe(0);
    expect(getWikiSettingsSnapshot()!.nightly.maxPackagesPerNight).toBe(20);
  });

  it('валидное число «Пакетов за ночь» 12: ровно один PUT {nightly:{maxPackagesPerNight:12}}', async () => {
    const c = render();
    await settle();
    const wrap = findNodeByAttr(c, 'data-testid', 'wiki-nightly-input-maxPackagesPerNight')!;
    const input = wrap.querySelectorAll('input')[0];
    callProp(input, 'onFocus');
    callProp(input, 'onChange', { target: { value: '12' } });
    await settle();
    expect(putCalls().length).toBe(0); // посреди набора PUT нет: коммит на blur
    callProp(input, 'onBlur');
    await settle();
    // мутация (4): без putWikiNightly в commitNum здесь 0 PUT'ов — тест красный
    const puts = putCalls();
    expect(puts.length).toBe(1);
    expect(sentBody(puts[0])).toEqual({ nightly: { maxPackagesPerNight: 12 } });
    // ответ подтверждён — новое значение в снимке (мок мержит названные поля, как бэк)
    expect(getWikiSettingsSnapshot()!.nightly.maxPackagesPerNight).toBe(12);
  });

  it('валидное окно 03–06 → 03–08: ровно один PUT, в теле обе границы', async () => {
    const c = render();
    await settle();
    const win = findNodeByAttr(c, 'data-testid', 'wiki-nightly-window')!;
    const selects = win.querySelectorAll('select');
    callProp(selects[1], 'onChange', { target: { value: '8' } });
    await settle();
    const puts = putCalls();
    expect(puts.length).toBe(1);
    // tryWindow всегда зовёт обе границы (WikiNightlyBlock.tsx:267) — зафиксировано как есть
    expect(sentBody(puts[0])).toEqual({ nightly: { windowStartHour: 3, windowEndHour: 8 } });
    expect(getWikiSettingsSnapshot()!.nightly.windowEndHour).toBe(8);
  });

  it('валидные бюджет и «Пакетов на раздел»: ровно один PUT на поле, только своё поле', async () => {
    const c = render();
    await settle();

    const budgetInput = findNodeByAttr(c, 'data-testid', 'wiki-nightly-input-inputTokenBudget')!
      .querySelectorAll('input')[0];
    callProp(budgetInput, 'onFocus');
    callProp(budgetInput, 'onChange', { target: { value: '500000' } });
    await settle();
    callProp(budgetInput, 'onBlur');
    await settle();
    let puts = putCalls();
    expect(puts.length).toBe(1);
    expect(sentBody(puts[0])).toEqual({ nightly: { inputTokenBudget: 500000 } });
    expect(getWikiSettingsSnapshot()!.nightly.inputTokenBudget).toBe(500000);

    const secInput = findNodeByAttr(c, 'data-testid', 'wiki-nightly-input-maxPackagesPerSection')!
      .querySelectorAll('input')[0];
    callProp(secInput, 'onFocus');
    callProp(secInput, 'onChange', { target: { value: '8' } });
    await settle();
    callProp(secInput, 'onBlur');
    await settle();
    puts = putCalls();
    expect(puts.length).toBe(2); // второе поле — второй PUT, первый не повторился
    expect(sentBody(puts[1])).toEqual({ nightly: { maxPackagesPerSection: 8 } });
    expect(getWikiSettingsSnapshot()!.nightly.maxPackagesPerSection).toBe(8);
  });
});

// ─── LI-3: подпись о незаведённой вики под тумблером ───
// Состояние — из стора дерева (поля «initialized» в DTO настроек нет). Вики заводится
// тем же PUT'ом, что включает тумблер (LI-2): после подтверждённого включения блок
// перечитывает дерево, и подпись уходит без перезагрузки экрана. Дерево ещё не читали —
// состояния не знаем, подписи нет (выдумывать нечем).
describe('подпись «вики ещё не создана» (LI-3)', () => {
  function serveTree(initialized: boolean): void {
    req.mockImplementation(async (url: string) => {
      if (url === '/wiki/settings') return server;
      if (url.startsWith('/wiki/tree')) return { initialized, sections: [] };
      return null;
    });
  }

  // Мок как бэк LI-2: PUT с nightly.enabled=true заводит вики — с этого момента
  // GET /wiki/tree отвечает initialized:true. failTreeAfter — с какого по счёту чтения
  // дерево падает (для «исключение наружу не уходит»)
  function serveTreeCreatedOnEnable(failTreeAfter = Infinity): void {
    const base = req.getMockImplementation()!; // serveSettings() уже стоит в beforeEach
    let treeReads = 0;
    req.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url.startsWith('/wiki/tree')) {
        treeReads += 1;
        if (treeReads >= failTreeAfter) throw httpError(500);
        return { initialized: (server.nightly as Record<string, unknown>).enabled === true, sections: [] };
      }
      return base(url, init);
    });
  }

  const treeGets = (): unknown[][] => req.mock.calls.filter((c) => String(c[0]).startsWith('/wiki/tree'));

  it('дерево не заведено — подпись под тумблером; после заведения — нет', async () => {
    serveTree(false);
    await act(async () => { await loadWikiTree(); });
    const c = render();
    await settle();
    expect(has(c, 'Вики ещё не создана — она создастся, как только вы включите ночной разбор.')).toBe(true);
    // вики появилась (так же, как её заведёт сервер при включении разбора): подпись уходит
    serveTree(true);
    await act(async () => { await loadWikiTree(); });
    await settle();
    expect(has(c, 'Вики ещё не создана')).toBe(false);
  });

  it('включение тумблера: PUT подтверждён — дерево перечитано, подпись исчезает без перезагрузки', async () => {
    serveTreeCreatedOnEnable();
    await act(async () => { await loadWikiTree(); });
    const c = render();
    await settle();
    expect(has(c, 'Вики ещё не создана — она создастся, как только вы включите ночной разбор.')).toBe(true);
    const sw = findNodeByAttr(c, 'data-testid', 'wiki-nightly-switch')!;
    const toggle = findNodeByAttr(sw, 'role', 'switch')!;
    click(toggle, 'Разбирать по ночам');
    await settle();
    expect(putCalls().length).toBe(1);
    expect(sentBody(putCalls()[0])).toEqual({ nightly: { enabled: true } });
    // мутация (6): без перечитки дерева после PUT здесь одна запись /wiki/tree и
    // живая подпись — обе строки краснеют
    expect(treeGets().length).toBe(2);
    expect(getWikiTreeSnapshot()!.initialized).toBe(true);
    expect(has(c, 'Вики ещё не создана')).toBe(false);
  });

  it('отказ перечитки дерева: настройки сохранены, исключение наружу не идёт', async () => {
    serveTreeCreatedOnEnable(2); // первая перечитка (до рендера) удачна, вторая — после PUT — падает
    await act(async () => { await loadWikiTree(); });
    const c = render();
    await settle();
    expect(has(c, 'Вики ещё не создана')).toBe(true);
    const sw = findNodeByAttr(c, 'data-testid', 'wiki-nightly-switch')!;
    const toggle = findNodeByAttr(sw, 'role', 'switch')!;
    click(toggle, 'Разбирать по ночам');
    await settle();
    // мутация (7): без .catch отказ перечитки вошёл бы в runAction и дал бы плашку
    // «Не удалось сохранить настройки» на сохранённые настройки
    expect(getWikiSettingsSnapshot()!.nightly.enabled).toBe(true);
    expect(has(c, 'Не удалось сохранить настройки')).toBe(false);
    expect(has(c, 'Не сохранилось')).toBe(false);
    // дерево осталось неперечитанным: подпись живёт до следующей удачной перечитки
    expect(getWikiTreeSnapshot()!.initialized).toBe(false);
    expect(has(c, 'Вики ещё не создана')).toBe(true);
  });

  it('дерево ещё не читали — подписи нет', async () => {
    const c = render();
    await settle();
    expect(has(c, 'Вики ещё не создана')).toBe(false);
  });
});

// ─── WN-6b: «Запустить сейчас», состояния кнопки и «отказано» в итоге ───
// Диалог — портал в document.body (тот же приём, что «Журнал»): его узлы ищутся от body.
describe('«Запустить сейчас» (WN-6b)', () => {
  function docBody(): AnyNode {
    return (globalThis as { document: { body: AnyNode } }).document.body;
  }
  function allNodes(root: AnyNode, out: AnyNode[] = []): AnyNode[] {
    out.push(root);
    for (const c of root.childNodes) allNodes(c, out);
    return out;
  }
  // Точное совпадение: «Запустить» в диалоге не должно схватить «Запустить сейчас» кнопки
  function buttonByExactText(root: AnyNode, text: string): AnyNode | null {
    return allNodes(root).find(n => n.nodeName === 'BUTTON' && textNodes(n).join('').trim() === text) ?? null;
  }
  const runBtn = (c: Cont) => findNodeByAttr(c, 'data-testid', 'wiki-nightly-run')!;
  const isDisabled = (n: AnyNode): boolean => (reactProps(n) as { disabled?: boolean }).disabled === true;
  const postCalls = (): unknown[][] => req.mock.calls.filter((c) => ((c[1] as RequestInit | undefined)?.method ?? 'GET') === 'POST');

  it('клик «Запустить сейчас»: диалог с лимитом пакетов из снимка; «Запустить» → ровно один POST; «Отмена» → 0', async () => {
    // 7, а не потолок 20: текст диалога берёт значение из снимка, не константу
    server = settings({ nightly: { enabled: true, maxPackagesPerNight: 7 } });
    const c = render();
    await settle();
    expect(isDisabled(runBtn(c))).toBe(false);

    click(runBtn(c), 'Запустить сейчас');
    expect(has(docBody(), 'Запустить ночной разбор сейчас?')).toBe(true);
    expect(has(docBody(), 'Хранитель разберёт новые чаты — не больше 7 пакетов за проход — и при включённом автоприменении сразу запишет найденное в вики. Откатить можно из „Журнала“.')).toBe(true);
    click(buttonByExactText(docBody(), 'Отмена')!, 'отмена');
    expect(has(docBody(), 'Запустить ночной разбор сейчас?')).toBe(false);
    expect(postCalls().length).toBe(0);

    click(runBtn(c), 'Запустить сейчас');
    click(buttonByExactText(docBody(), 'Запустить')!, 'запустить');
    await settle();
    const posts = postCalls();
    expect(posts.length).toBe(1);
    expect(String(posts[0][0])).toBe('/wiki/settings/nightly/run');
    // успех: стор оптимистично ставит проход (WN-6a) — кнопка выключена и говорит «идёт»
    // (здесь же краснеет мутация (9): без nightlyRunning в disabled кнопка осталась бы активной)
    expect(getWikiSettingsSnapshot()!.nightlyRunning).toBe(true);
    expect(isDisabled(runBtn(c))).toBe(true);
    expect(has(c, 'Идёт разбор…')).toBe(true);
  });

  it('nightlyRunning: кнопка disabled с «Идёт разбор…», без «Запустить сейчас»', async () => {
    server = settings({ nightly: { enabled: true }, nightlyRunning: true });
    const c = render();
    await settle();
    // мутация (9): без snapshot.nightlyRunning в disabled здесь краснеют обе строки
    expect(isDisabled(runBtn(c))).toBe(true);
    expect(has(c, 'Идёт разбор…')).toBe(true);
    expect(has(c, 'Запустить сейчас')).toBe(false);
    expect(has(c, 'Ночной разбор выключен')).toBe(false);
  });

  it('тумблер выключен: кнопка disabled с подсказкой «Ночной разбор выключен»', async () => {
    const c = render(); // умолчание NA-2: enabled=false
    await settle();
    expect(isDisabled(runBtn(c))).toBe(true);
    expect(has(c, 'Ночной разбор выключен')).toBe(true);
  });

  it('rejected=2: «отказано 2» в строке чисел; rejected=0 или поля нет — слова «отказано» нет', async () => {
    server = settings({ lastNight: LAST({ rejected: 2 }) });
    const c = render();
    await settle();
    // мутация (10): без ячейки rejected в итоге этой строки нет — тест красный
    expect(has(c, 'отказано 2')).toBe(true);

    server = settings({ lastNight: LAST({ rejected: 0 }) });
    const c0 = render();
    await settle();
    expect(has(c0, 'отказано')).toBe(false);

    server = settings({ lastNight: LAST({}) }); // старый итог без поля (аддитивность WN-2)
    const cN = render();
    await settle();
    expect(has(cN, 'отказано')).toBe(false);
  });

  it('409 wiki_nightly_disabled: текст отказа у кнопки, кнопка снова активна, прохода нет', async () => {
    server = settings({ nightly: { enabled: true } });
    const c = render();
    await settle();
    req.mockImplementation(async (url: string, init?: RequestInit) => {
      if ((init?.method ?? 'GET').toUpperCase() === 'POST') {
        throw httpError(409, { code: 'wiki_nightly_disabled', message: 'ночной разбор этого владельца выключен' });
      }
      return server;
    });
    click(runBtn(c), 'Запустить сейчас');
    click(buttonByExactText(docBody(), 'Запустить')!, 'запустить');
    await settle();
    expect(postCalls().length).toBe(1);
    expect(has(c, 'Ночной разбор выключен — запустить его нельзя')).toBe(true);
    // отказ — не старт: стор не ставит проход, кнопка возвращается в активное состояние
    expect(getWikiSettingsSnapshot()!.nightlyRunning).toBe(false);
    expect(isDisabled(runBtn(c))).toBe(false);
  });
});

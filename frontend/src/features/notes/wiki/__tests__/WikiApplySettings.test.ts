// Рендер-тест блока «Применение» (S7d-1): выключенный класс показан с причиной бэка и кнопкой
// «Снова применять само», у включённого класса этой кнопки нет; нажатие — ровно один PUT с ключом
// класса и enable:true, после ответа класс включён; общий выключатель оптимистичен и шлёт
// {autoApplyEnabled}; отказ PUT откатывает оптимистичное и называется текстом по коду; пустой
// список — пустое состояние; отказ чтения — плашка с «Повторить».
//
// Мокан только транспорт (offline.request, signalr) — стор настоящий (тот же приём, что
// WikiJournal.test): «один вызов метода lib/wiki.ts с ключом класса» проверяется телом PUT,
// которое реально ушло на прово.
//
// Класс-часть ответа — контракт бэка, а не полный список: PUT отвечает только классами с записью
// в settings.json (её имеют когда-либо выключавшиеся) и с нулевым замером (BuildDto(next, [],
// null), WikiSettingsController.cs:185). Снимок стора обязан сохранять все классы: и активный
// без записи, и его замер — иначе смена окна ночи оставляла бы блок пустым до перечитки.
//
// Мутации этого прогона (вывод — в отчёте задачи):
// (1) в ClassRow не показывать reason выключенного → «выключенный класс с причиной» краснеет;
// (2) давать кнопку «Снова применять само» и включённому классу → «у включённого нет кнопки»
//     краснеет;
// (3) в onResume не передавать ключ класса (resumeWikiApplyClass('')) → «ровно один PUT с
//     ключом класса» краснеет (тело без ключа);
// (4) убрать оптимистичную запись тумблера (стор) → «тумблер оптимистичен» краснеет;
// (5) не откатывать при отказе PUT (стор) → «отказ откатывает» краснеет;
// (6) не звать loadWikiSettings после успеха (стор) → «после ответа доли перечитаны» краснеет;
// (7) в mergeClassStats брать множество классов из ответа (return next.map) → «смена окна ночи»
//     и «между ответом и перечиткой» краснеют (активный класс выпадает из снимка).
//
// Класс «Замена ваших формулировок» (F6c, ADR-038 «Класс @human в „Настройках“»): записи
// …@human — агрегат под именем класса, «Снова применять само» — PUT по каждой выключенной
// записи. Мутации этих тестов:
// (8) снять проверку ключа (isHumanTargetClass в разделении блока) → «строка-агрегат под
//     именем» краснеет: сырой ключ выходит на экран, имени и агрегата нет;
// (9) убрать if (busy) return в runAction → «двойной клик под первым запросом» краснеет:
//     второй клик добавляет PUT по оставшейся выключенной записи;
// (10) снять фильтр записей human×… → «шум human×… не показан» краснеет.
// Отбрасывание устаревшего ответа — поколение слота настроек стора (проверено на сторе,
// wiki.test.ts «ДваБыстрыхPUT»), механизм не различает классы.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { C, LIST_MAX_H } from 'aihome_shell/kit';
import { WikiApplySettings, classesHeaderText, disabledFirst } from '../WikiApplySettings';

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
import { __test as wikiStore, getWikiSettingsSnapshot, putWikiNightly } from '../../../../lib/wiki';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

type Cont = ReturnType<typeof mount>['container'];
type AnyNode = Cont;

// Настройки как их отдаёт GET /api/wiki/settings (контракт S4c, WikiSettingsDto): классы —
// с замером оценки; доля — число уже в процентах; reason — человеческий текст оценки.
const NIGHTLY = {
  enabled: false, windowStartHour: 3, windowEndHour: 6,
  maxPackagesPerNight: 20, maxPackagesPerSection: 6, maxChatsPerSection: 10,
  inputTokenBudget: 700000,
};
const CLS_ON = {
  class: 'session×append', disabled: false, reason: null, disabledAt: null, resumedAt: null,
  applied7: 5, retracted7: 0, share7: 0, applied30: 41, retracted30: 0, share30: 0,
};
const CLS_OFF = {
  class: 'session×create', disabled: true,
  reason: 'в окне последних 50 операций откатили 4 из 38 (10,5 %) при пороге 10 %',
  // полдень UTC: дата в любом часовом пояшении прогона остаётся 25.10 — проверка даты
  // не должна зависеть от TZ машины
  disabledAt: '2026-10-25T12:00:00Z', resumedAt: null,
  applied7: 0, retracted7: 3, share7: 0, applied30: 38, retracted30: 4, share30: 10.5,
};
// второй выключенный класс — для проверки порядка внутри группы выключенных (M4)
const OFF_NOTE = { ...CLS_OFF, class: 'note×create' };
const SETTINGS = { autoApplyEnabled: true, classes: [CLS_ON, CLS_OFF], nightly: NIGHTLY };
// Нулевой замер — как бэк сериализует класс без строки оценки (WikiClassDto по умолчанию):
// именно таким приходит каждый класс ответа PUT («Замер не возвращается», :94; BuildDto(next,
// [], null), :185). Активного никогда не выключавшегося класса в ответе PUT нет вовсе: его
// строки в settings.json не было (BuildDto строит класс-часть из settings.Classes, :206–214)
const NO_STATS = { applied7: 0, retracted7: 0, share7: 0, applied30: 0, retracted30: 0, share30: 0 };

// Класс замены формулировок человека — бэковая форма ключа с хвостом @human (по одному на
// origin × supersede/conflict, ADR-038:440). Числа согласованы: откатов не больше применённых
const HUMAN_OFF_CONFLICT = {
  class: 'session×conflict@human', disabled: true,
  reason: '3 откатов за 7 дней при пороге 3',
  disabledAt: '2026-10-25T12:00:00Z', resumedAt: null,
  applied7: 3, retracted7: 3, share7: 100, applied30: 10, retracted30: 6, share30: 60,
};
const HUMAN_OFF_SUPERSEDE = {
  class: 'session×supersede@human', disabled: true,
  reason: 'в окне последних 50 операций откатили 6 из 18 (33,3 %) при пороге 10 %',
  disabledAt: '2026-10-26T12:00:00Z', resumedAt: null,
  applied7: 2, retracted7: 2, share7: 100, applied30: 18, retracted30: 6, share30: 33.3,
};
const HUMAN_ON_NOTE = {
  class: 'note×supersede@human', disabled: false, reason: null, disabledAt: null, resumedAt: null,
  applied7: 2, retracted7: 1, share7: 50, applied30: 12, retracted30: 2, share30: 16.7,
};
const HUMAN_ON_SESSION = {
  class: 'session×supersede@human', disabled: false, reason: null, disabledAt: null, resumedAt: null,
  applied7: 3, retracted7: 0, share7: 0, applied30: 20, retracted30: 1, share30: 5,
};
// Запись human×… (без «@») — шум до SV1-Б: оценка её больше не мерит (пакеты AuthorHuman
// пропускаются, WikiClassHealth.cs:140), но старая строка settings.json BuildDto отдаёт
// как есть; фронт её не показывает (ADR-038:440)
const NOISE_HUMAN_EDIT = {
  class: 'human×edit', disabled: false, reason: null, disabledAt: null, resumedAt: null,
  applied7: 1, retracted7: 0, share7: 0, applied30: 1, retracted30: 0, share30: 0,
};

const httpError = (status: number, body?: unknown): Error =>
  Object.assign(new Error(`HTTP ${status}`), { status, body });

// Состояние «сервера» теста: PUT меняет его и отвечает БЕЗ замера и только классами с записью
// в settings.json — запись появляется у класса, который когда-либо выключали (оценкой —
// WikiClassHealth.cs:295 — или человеком: Mutate, WikiSettingsController.cs:130–144), и больше
// не снимается; активный никогда не выключавшийся класс в ответе не назван вовсе (:185).
// Следующий GET — оценка: изменённое состояние с НОВЫМ замером
// (applied30=42 — отличается и от прежнего снимка, и от нулей PUT: «доли перечитаны GET'ом»
// проверяется именно этим числом)
let server: Record<string, unknown> = SETTINGS;
let withEntry = new Set<string>([CLS_OFF.class]);
let putDone = false;
// выключение/возврат — та же строка состояния, что ставит бэк (:135–143): возврат
// начинает окно класса заново (ResumedAt), выключение сохраняет его
function applyPut(body: Record<string, unknown>): void {
  if (body.autoApplyEnabled !== undefined) server = { ...server, autoApplyEnabled: body.autoApplyEnabled };
  if (body.class !== undefined) {
    withEntry.add(body.class as string);
    const at = '2026-11-02T10:00:00Z';
    server = {
      ...server,
      classes: (server.classes as Record<string, unknown>[]).map(c => (c.class === body.class
        ? (body.enable === true
          ? { ...c, disabled: false, reason: null, disabledAt: null, resumedAt: at, ...NO_STATS }
          : { ...c, disabled: true, reason: 'класс выключен человеком', disabledAt: at, ...NO_STATS })
        : c)),
    };
  }
  if (body.nightly !== undefined) {
    // ночной блок: названное поле — менять, null — не менять (:146–168)
    const patch = Object.fromEntries(
      Object.entries(body.nightly as Record<string, unknown>).filter(([, v]) => v !== null));
    server = { ...server, nightly: { ...(server.nightly as Record<string, unknown>), ...patch } };
  }
}
// ответ PUT — формы реального бэка: BuildDto(next, [], null): только классы с записью,
// замер нулевой, активного класса без записи нет вовсе
function putResponse(): Record<string, unknown> {
  return {
    ...server,
    classes: (server.classes as Record<string, unknown>[])
      .filter(c => withEntry.has(c.class as string))
      .map(c => ({ ...c, ...NO_STATS })),
  };
}
function getResponse(): Record<string, unknown> {
  if (putDone) {
    return {
      ...server,
      classes: (server.classes as Record<string, unknown>[]).map(c =>
        (c.class === 'session×append' ? { ...c, applied30: 42 } : c)),
    };
  }
  return server;
}
function serveSettings(): void {
  req.mockImplementation(async (url: string, init?: RequestInit) => {
    const method = (init?.method ?? 'GET').toUpperCase();
    if (url !== '/wiki/settings') return null;
    if (method === 'PUT') {
      putDone = true;
      applyPut(JSON.parse((init?.body ?? '{}') as string));
      return putResponse();
    }
    return getResponse();
  });
}
// Тот же «сервер», но первый PUT держится открытым: под ним блок перечитывается на
// оптимистичном состоянии — этим проверяется конкурентность кнопки (двойной клик, busy-слот)
function serveHoldingFirstPut(): { release: () => void; puts: () => number } {
  let releaseFn: (v: unknown) => void = () => {};
  const gate = new Promise<unknown>((r) => { releaseFn = r; });
  let n = 0;
  req.mockImplementation(async (url: string, init?: RequestInit) => {
    const method = (init?.method ?? 'GET').toUpperCase();
    if (url !== '/wiki/settings') return null;
    if (method === 'PUT') {
      putDone = true;
      applyPut(JSON.parse((init?.body ?? '{}') as string));
      n += 1;
      if (n === 1) return gate;
      return putResponse();
    }
    return getResponse();
  });
  return { release: () => releaseFn(putResponse()), puts: () => n };
}

const roots: Root[] = [];

function render(): Cont {
  installFakeDom();
  const { root, container } = mount(createElement(WikiApplySettings));
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

const putCalls = (): unknown[][] => req.mock.calls.filter((c) => ((c[1] as RequestInit | undefined)?.method ?? 'GET') === 'PUT');
const sentBody = (call: unknown[]): Record<string, unknown> => JSON.parse(((call[1] as RequestInit).body ?? '') as string);

function allNodes(root: AnyNode, out: AnyNode[] = []): AnyNode[] {
  out.push(root);
  for (const c of root.childNodes) allNodes(c, out);
  return out;
}

// Инлайн-стиль узла — из props React (в fakeDom он не дублируется в атрибут): тест читает
// ровно то, что задал компонент (тот же приём, что в WikiPageView.test)
function styleOf(n: AnyNode): Record<string, string> {
  return ((reactProps(n) ?? {}).style ?? {}) as Record<string, string>;
}

beforeEach(() => {
  wikiStore.reset();
  req.mockReset();
  server = SETTINGS;
  // «сервер» каждого теста начинается с одного и того же состояния записи классов
  withEntry = new Set<string>([CLS_OFF.class]);
  putDone = false;
  serveSettings();
});

afterEach(() => {
  for (const root of roots.splice(0)) void root.unmount();
  uninstallFakeDom();
  wikiStore.reset();
});

describe('блок «Применение» (S7d-1)', () => {
  it('выключенный класс показан с причиной и датой; у включённого — без кнопки', async () => {
    const c = render();
    await settle();
    // выключенный: бейдж, причина оценки дословно, дата из DisabledAt, кнопка возврата
    expect(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-session×create')).toBeTruthy();
    expect(has(c, 'откатили 4 из 38 (10,5 %) при пороге 10 %')).toBe(true);
    expect(has(c, '25.10.2026')).toBe(true);
    expect(has(c, 'выключен')).toBe(true);
    // включённый: состояния «само» и NO кнопки возврата — «Снова применять само» только для выключенных
    expect(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-session×append')).toBeNull();
    expect(has(c, 'само')).toBe(true);
    // класс — бэковым ключом с пробелами для чтения
    expect(has(c, 'session × create')).toBe(true);
  });

  it('кнопка «Снова применять само» — цель касания не ниже 40 px', async () => {
    const c = render();
    await settle();
    const btn = findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-session×create')!;
    const st = (reactProps(btn).style ?? {}) as { minHeight?: number; height?: number };
    expect(st.minHeight).toBeGreaterThanOrEqual(40);
    expect(st.height === undefined || st.height >= 40).toBe(true);
  });

  it('нажатие «Снова применять само» — ровно один PUT с ключом класса; после ответа кнопки нет', async () => {
    const c = render();
    await settle();
    const btn = findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-session×create')!;
    click(btn, 'Снова применять само');
    await settle();
    const puts = putCalls();
    expect(puts.length).toBe(1);
    // ключ уходит как есть ('origin×op' без пробелов — форма бэкового ключа)
    expect(sentBody(puts[0])).toEqual({ class: 'session×create', enable: true });
    // после подтверждённого и перечитки GET класс включён: своей кнопки больше нет
    expect(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-session×create')).toBeNull();
    expect(getWikiSettingsSnapshot()?.classes.find(cl => cl.class === 'session×create')?.disabled).toBe(false);
  });

  it("после подтверждённого действия доли перечитаны GET'ом, а не нулями ответа PUT", async () => {
    const c = render();
    await settle();
    click(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-session×create')!, 'Снова применять само');
    await settle();
    // GET после PUT (оценка) — был: без него стор остался бы на merged-замере прежнего чтения
    const gets = req.mock.calls.filter((c2) => ((c2[1] as RequestInit | undefined)?.method ?? 'GET') === 'GET');
    expect(gets.length).toBeGreaterThanOrEqual(2);
    const cls = getWikiSettingsSnapshot()!.classes.find(cl => cl.class === 'session×append')!;
    // 42 — только из перечитки; без неё было бы 41 (прежний снимок) или 0 (нули PUT)
    expect(cls.applied30).toBe(42);
  });

  it('PUT, не касающийся классов (смена окна ночи), не меняет класс-часть снимка', async () => {
    const c = render();
    await settle();
    expect(getWikiSettingsSnapshot()!.classes.length).toBe(2);
    // Смена окна ночи — PUT без class: бэк отвечает только классами с записью и нулевым
    // замером, активного класса в ответе нет вовсе (BuildDto(next, [], null))
    let res: Awaited<ReturnType<typeof putWikiNightly>> | undefined;
    await act(async () => { res = await putWikiNightly({ windowStartHour: 4 }); });
    await settle();
    expect(res!.ok).toBe(true);
    const snap = getWikiSettingsSnapshot()!;
    expect(snap.nightly.windowStartHour).toBe(4);
    // оба класса живы в снимке: активный — со своим замером, выключенный — со своей причиной
    // и долей: их вернул бы и следующий GET, но блок не имеет права пустеть между ними
    const on = snap.classes.find(cl => cl.class === 'session×append')!;
    expect(on.disabled).toBe(false);
    expect(on.applied30).toBe(41);
    const off = snap.classes.find(cl => cl.class === 'session×create')!;
    expect(off.disabled).toBe(true);
    expect(off.share30).toBe(10.5);
    expect(has(c, 'Классов правок ещё нет')).toBe(false);
    expect(has(c, 'session × append')).toBe(true);
    expect(has(c, 'session × create')).toBe(true);
  });

  it('между ответом PUT и перечиткой GET снимок не пустеет: классы prev сохранены', async () => {
    const c = render();
    await settle();
    // Перечитку GET после действия держим открытой: видим ровно тот снимок, что стор пишет из
    // ответа PUT. Ответ — только возвращённый класс с нулевым замером (форма бэка); без union
    // мерджа активный класс выпал бы из снимка до перечитки, и блок показал бы пустой список
    req.mockImplementation((url: string, init?: RequestInit) => {
      const method = (init?.method ?? 'GET').toUpperCase();
      if (url !== '/wiki/settings') return Promise.resolve(null);
      if (method === 'PUT') {
        return Promise.resolve({
          ...SETTINGS,
          classes: [{ ...CLS_OFF, disabled: false, reason: null, disabledAt: null, resumedAt: '2026-11-02T10:00:00Z', ...NO_STATS }],
        });
      }
      return new Promise<unknown>(() => { /* перечитки в этом тесте нет: она и есть проверяемое */ });
    });
    click(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-session×create')!, 'Снова применять само');
    await settle();
    const snap = getWikiSettingsSnapshot()!;
    expect(snap.classes.length).toBe(2);
    // ответ подтверждён по своему классу: он включён, а его замер — прежний (PUT его не несёт)
    const off = snap.classes.find(cl => cl.class === 'session×create')!;
    expect(off.disabled).toBe(false);
    expect(off.applied30).toBe(38);
    // и чужой, не тронутый PUT класс — на месте со своим замером
    expect(snap.classes.find(cl => cl.class === 'session×append')!.applied30).toBe(41);
    expect(has(c, 'Классов правок ещё нет')).toBe(false);
  });

  it('общий выключатель: оптимистичен до ответа и шлёт {autoApplyEnabled}', async () => {
    const c = render();
    await settle();
    const sw = findNodeByAttr(c, 'data-testid', 'wiki-apply-switch')!;
    const toggle = findNodeByAttr(sw, 'role', 'switch')!;
    // J-F1 (QA b0065470): имя тумблера для скринридера равно видимой подписи — экран и
    // доступное имя не расходятся
    expect(toggle.getAttribute('aria-label'), 'aria-label = видимая подпись').toBe('Применять неспорное само');
    click(toggle, 'тумблер «Применять неспорное само»');
    // до ответа: новое значение уже в сторе — UI тумблера не ждёт сети (мутация 4)
    expect(getWikiSettingsSnapshot()!.autoApplyEnabled).toBe(false);
    await settle();
    const puts = putCalls();
    expect(puts.length).toBe(1);
    expect(sentBody(puts[0])).toEqual({ autoApplyEnabled: false });
    expect(getWikiSettingsSnapshot()!.autoApplyEnabled).toBe(false);
  });

  it('отказ PUT откатывает оптимистичное и называется текстом по коду', async () => {
    const c = render();
    await settle();
    req.mockImplementation(async () => { throw httpError(400, { code: 'wiki_class_key', message: 'класс не пары «источник × операция»' }); });
    click(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-session×create')!, 'Снова применять само');
    await settle();
    // оптимистичное откатано: класс остался выключенным
    expect(getWikiSettingsSnapshot()!.classes.find(cl => cl.class === 'session×create')!.disabled).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-session×create')).toBeTruthy();
    expect(has(c, 'Неизвестный класс')).toBe(true);
  });

  it('пустой список классов — пустое состояние; отказ чтения — плашка с «Повторить»', async () => {
    server = { ...SETTINGS, classes: [] };
    const c = render();
    await settle();
    expect(has(c, 'Классов правок ещё нет')).toBe(true);
    // отказ чтения настроек
    req.mockImplementation(async () => { throw httpError(409, { code: 'wiki_llm_wiki_off', message: 'функция выключена' }); });
    const c2 = render();
    await settle();
    expect(findNodeByAttr(c2, 'data-testid', 'wiki-apply-error')).toBeTruthy();
    expect(has(c2, 'Вики выключена в настройках')).toBe(true);
  });
});

// Класс «Замена ваших формулировок» (F6c, ADR-038 «Класс @human в „Настройках“»): бэк отдаёт
// класс замены формулировок человека сырыми ключами …@human; блок показывает их одной
// строкой-агрегатом под именем класса: счёт — сумма записей, доля — пересчёт из сумм,
// состояние/причина — первой выключенной записи; «Снова применять само» — PUT по каждой
// выключенной записи. Сырой ключ и шум human×… на экран не выходят.
describe('класс «Замена ваших формулировок» (F6c)', () => {
  it('строка-агрегат под именем класса: доли по суммам записей, сырой ключ не показан', async () => {
    server = { ...SETTINGS, classes: [CLS_ON, HUMAN_ON_NOTE, HUMAN_ON_SESSION] };
    const c = render();
    await settle();
    expect(has(c, 'Замена ваших формулировок')).toBe(true);
    // описание класса — дословно из макета (notes-wiki-unified-v1-proposal, «Применение»)
    expect(has(c, 'ИИ меняет ваше сам, если источник новее вашей правки')).toBe(true);
    // агрегат: 2+3 применено / 1+0 откатов за 7 дн → 20 %; 12+20 / 2+1 за 30 дн → 9,4 %
    expect(has(c, 'за 7 дн: 5 операций · откатов 1 (20 %)')).toBe(true);
    expect(has(c, '32 операции · откатов 3 (9,4 %)')).toBe(true);
    // сырой ключ на экран не выходит ни в какой форме
    expect(has(c, 'supersede')).toBe(false);
    expect(has(c, '@human')).toBe(false);
    // агрегат — первой строкой списка (именованная настройка, по макету), остальные — как были
    const ts = texts(c);
    expect(ts.indexOf('Замена ваших формулировок')).toBeGreaterThanOrEqual(0);
    expect(ts.indexOf('Замена ваших формулировок')).toBeLessThan(ts.indexOf('session × append'));
    expect(has(c, 'само')).toBe(true);
  });

  it('выключенный агрегат: причина и дата первой выключенной записи; «Снова применять само» — PUT по каждой выключенной', async () => {
    server = { ...SETTINGS, classes: [HUMAN_OFF_CONFLICT, HUMAN_OFF_SUPERSEDE] };
    const c = render();
    await settle();
    expect(has(c, 'Замена ваших формулировок')).toBe(true);
    expect(has(c, 'выключен')).toBe(true);
    // причина и дата — первой выключенной записи порядка бэка (ADR-038: «текст — её reason»);
    // причина второй наружу не выходит
    expect(has(c, '3 откатов за 7 дней при пороге 3')).toBe(true);
    expect(has(c, '25.10.2026')).toBe(true);
    expect(has(c, '6 из 18')).toBe(false);
    // по макету у выключенного описание класса заменяется причиной
    expect(has(c, 'ИИ меняет ваше сам')).toBe(false);
    // агрегатный замер: 3+2 / 3+2 за 7 дн → 100 %; 10+18 / 6+6 за 30 дн → 42,9 %
    expect(has(c, '5 операций · откатов 5 (100 %)')).toBe(true);
    expect(has(c, '28 операций · откатов 12 (42,9 %)')).toBe(true);
    const btn = findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-human')!;
    click(btn, 'Снова применять само');
    await settle();
    // одна последовательность: ровно один PUT по каждой выключенной записи, чужие не тронуты
    const puts = putCalls();
    expect(puts.length).toBe(2);
    expect(sentBody(puts[0])).toEqual({ class: 'session×conflict@human', enable: true });
    expect(sentBody(puts[1])).toEqual({ class: 'session×supersede@human', enable: true });
    // после подтверждённого и перечитки выключенных записей нет — кнопки тоже
    expect(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-human')).toBeNull();
  });

  it('двойной клик по одиночному классу — один запрос: кнопка снята оптимистичным состоянием до ответа', async () => {
    server = { ...SETTINGS, classes: [HUMAN_OFF_CONFLICT] };
    const c = render();
    await settle();
    const hold = serveHoldingFirstPut();
    const btn = findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-human')!;
    click(btn, 'Снова применять само');
    await settle();
    // до ответа сервера: класс оптимистично включён, кнопки нет — физическому второму клику
    // некуда попасть; запрос ровно один, он ещё в полёте
    expect(getWikiSettingsSnapshot()!.classes.find(cl => cl.class === 'session×conflict@human')!.disabled).toBe(false);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-human')).toBeNull();
    expect(hold.puts()).toBe(1);
    hold.release();
    await settle();
    expect(hold.puts()).toBe(1);
    expect(sentBody(putCalls()[0])).toEqual({ class: 'session×conflict@human', enable: true });
  });

  it('двойной клик под первым запросом (две выключенные записи) — второй клик не добавляет PUT', async () => {
    server = { ...SETTINGS, classes: [HUMAN_OFF_CONFLICT, HUMAN_OFF_SUPERSEDE] };
    const c = render();
    await settle();
    const hold = serveHoldingFirstPut();
    const btn = findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-human')!;
    click(btn, 'Снова применять само');
    await settle();
    // первый PUT в полёте: первая запись оптимистично включена, вторая ещё выключена —
    // кнопка на месте, но под busy-слотом; повторный клик не должен создать запрос
    const btn2 = findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-human')!;
    click(btn2, 'Снова применять само повторно');
    expect(hold.puts()).toBe(1);
    hold.release();
    await settle();
    // серия первой же последовательности дошла до конца: ровно по одному PUT на запись
    expect(hold.puts()).toBe(2);
    expect(sentBody(putCalls()[0])).toEqual({ class: 'session×conflict@human', enable: true });
    expect(sentBody(putCalls()[1])).toEqual({ class: 'session×supersede@human', enable: true });
    expect(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-human')).toBeNull();
  });

  it('отказ PUT по классу @human: оптимистичное откатано, плашка с текстом по коду, действие остаётся', async () => {
    server = { ...SETTINGS, classes: [HUMAN_OFF_CONFLICT] };
    const c = render();
    await settle();
    req.mockImplementation(async () => { throw httpError(400, { code: 'wiki_class_key', message: 'класс не пары «источник × операция»' }); });
    click(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-human')!, 'Снова применять само');
    await settle();
    expect(getWikiSettingsSnapshot()!.classes.find(cl => cl.class === 'session×conflict@human')!.disabled).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-human')).toBeTruthy();
    expect(has(c, 'Неизвестный класс')).toBe(true);
  });

  it('между ответом PUT и перечиткой агрегат не пустеет: обе записи и их замер сохранены', async () => {
    server = { ...SETTINGS, classes: [HUMAN_OFF_CONFLICT, HUMAN_ON_NOTE] };
    const c = render();
    await settle();
    // ответ — только возвращённая запись с нулевым замером (форма BuildDto(next, [], null));
    // перечитку GET держим открытой: видим ровно тот снимок, что стор пишет из ответа
    req.mockImplementation((url: string, init?: RequestInit) => {
      const method = (init?.method ?? 'GET').toUpperCase();
      if (url !== '/wiki/settings') return Promise.resolve(null);
      if (method === 'PUT') {
        return Promise.resolve({
          ...server,
          classes: [{ ...HUMAN_OFF_CONFLICT, disabled: false, reason: null, disabledAt: null, resumedAt: '2026-11-02T10:00:00Z', ...NO_STATS }],
        });
      }
      return new Promise<unknown>(() => { /* перечитки в этом тесте нет: она и есть проверяемое */ });
    });
    click(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-human')!, 'Снова применять само');
    await settle();
    const snap = getWikiSettingsSnapshot()!;
    expect(snap.classes.length).toBe(2);
    // подтверждённая запись включена, её замер — прежний (PUT его не несёт); чужая — как была
    const off = snap.classes.find(cl => cl.class === 'session×conflict@human')!;
    expect(off.disabled).toBe(false);
    expect(off.applied30).toBe(10);
    expect(snap.classes.find(cl => cl.class === 'note×supersede@human')!.applied30).toBe(12);
    expect(has(c, 'Замена ваших формулировок')).toBe(true);
    expect(findNodeByAttr(c, 'data-testid', 'wiki-apply-resume-human')).toBeNull();
  });

  it('шум human×… (без «@») не показан: список только из таких записей — пустое состояние (и рамки нет)', async () => {
    server = { ...SETTINGS, classes: [NOISE_HUMAN_EDIT] };
    const c = render();
    await settle();
    expect(has(c, 'human × edit')).toBe(false);
    expect(has(c, 'Классов правок ещё нет')).toBe(true);
    // M4: пустое состояние — без рамки и её шапки: считать и ограничивать нечего
    expect(findNodeByAttr(c, 'data-testid', 'wiki-apply-classes')).toBeNull();
  });

  it('строка агрегата на 320 px не переполняется горизонтально: перенос строки заголовка и переносимый текст (M4: и шапка рамки над ней)', async () => {
    server = { ...SETTINGS, classes: [HUMAN_OFF_CONFLICT] };
    const c = render();
    await settle();
    const row = findNodeByAttr(c, 'data-testid', 'wiki-apply-human-row')!;
    // шапка строки — переносной flex-ряд (как в ClassRow): бейдж не вылезает за 320 px
    const header = row.childNodes[0];
    expect(styleOf(header).flexWrap).toBe('wrap');
    // M4: закреплённая шапка рамки над агрегатом — фон непрозрачный: строки списка под
    // ней при прокрутке не просвечивают
    const hdr = findNodeByAttr(c, 'data-testid', 'wiki-apply-classes-header')!;
    expect(styleOf(hdr).background).toBe(C.bgInset);
    // ищем элемент с прямой текстовой дочерней строкой (textNodes рекурсивен: по нему
    // внешние контейнеры тоже «содержат» строку, а стиль нужен у того, кто её рендерит)
    const hasOwnText = (n: AnyNode, needle: string) =>
      Array.from(n.childNodes as unknown[]).some(ch => (ch as { nodeType?: number; nodeValue?: string }).nodeType === 3 && (ch as { nodeValue?: string }).nodeValue?.includes(needle));
    const title = allNodes(c).find(n => n.nodeName === 'SPAN' && hasOwnText(n, 'Замена ваших формулировок'));
    expect(title).not.toBeUndefined();
    expect(styleOf(title!).overflowWrap).toBe('anywhere');
    const reason = allNodes(c).find(n => n.nodeName === 'DIV' && hasOwnText(n, 'при пороге 3'));
    expect(reason).not.toBeUndefined();
    expect(styleOf(reason!).overflowWrap).toBe('break-word');
    // описание включённого класса — тоже переносимый текст (длинная строка на мобиле)
    server = { ...SETTINGS, classes: [HUMAN_ON_NOTE] };
    const c2 = render();
    await settle();
    const desc = allNodes(c2).find(n => n.nodeName === 'DIV' && hasOwnText(n, 'ИИ меняет ваше сам'));
    expect(desc).not.toBeUndefined();
    expect(styleOf(desc!).overflowWrap).toBe('break-word');
  });
});

// M4 (wiki-panels-v1-proposal, «Применение» (А4) и П2): выключенные классы — наверху списка
// (внутри групп порядок — как пришёл с бэка), список — в рамке с пределом высоты LIST_MAX_H
// и своей прокруткой, в рамке — закреплённая шапка со счётом и бейджем выключенных.
// Мутации этих тестов (каждая прогнана в копии дерева, вывод — в отчёте задачи):
// (11) disabledFirst → вернуть исходный порядок (return classes) → краснеют «disabledFirst:
//      выключенные наверх» ([A,B,C,D] вместо [B,D,A,C]) и «выключенные классы поднимаются
//      выше включённых» (append остаётся первым);
// (12) не поднимать выключенные в месте вызова (disabledFirst(snapshot.classes) →
//      snapshot.classes) → «выключенные классы поднимаются выше включённых» краснеет,
//      чистый тест зелёный: порядок проверяется именно вызовом;
// (13) снять maxHeight рамки → «рамка списка» краснеет (expected undefined to be 420);
// (14) в classesHeaderText всегда добавлять «· выключено: K» → «без выключенных — только
//      счёт» и «бейджа нет» краснеют: при нуле шапка сообщает «выключено: 0».
describe('рамка и порядок списка классов (M4)', () => {
  it('disabledFirst: выключенные наверх, внутри групп — порядок как пришёл', () => {
    const cls = (name: string, off: boolean) => ({ ...(off ? CLS_OFF : CLS_ON), class: name, disabled: off });
    const got = disabledFirst([cls('A', false), cls('B', true), cls('C', false), cls('D', true)]).map(c => c.class);
    expect(got).toEqual(['B', 'D', 'A', 'C']);
  });

  it('classesHeaderText: «Классов: N · выключено: K»; без выключенных — только счёт', () => {
    expect(classesHeaderText(20, 3)).toBe('Классов: 20 · выключено: 3');
    expect(classesHeaderText(20, 0)).toBe('Классов: 20');
  });

  it('рамка списка: предел высоты LIST_MAX_H, своя прокрутка; шапка закреплена, со счётом и бейджем', async () => {
    const c = render();
    await settle();
    const frame = findNodeByAttr(c, 'data-testid', 'wiki-apply-classes')!;
    const st = styleOf(frame);
    expect(st.maxHeight).toBe(LIST_MAX_H);
    expect(st.overflowY).toBe('auto');
    expect(st.overscrollBehavior).toBe('contain');
    const hdr = findNodeByAttr(frame, 'data-testid', 'wiki-apply-classes-header')!;
    const hs = styleOf(hdr);
    expect(hs.position).toBe('sticky');
    expect(hs.top).toBe(0);
    // 1 выключенный из 2 классов: счёт и бейдж — как их видит человек в списке
    expect(has(hdr, 'Классов: 2 · выключено: 1')).toBe(true);
    expect(has(hdr, 'выключено 1')).toBe(true);
  });

  it('нет выключенных классов: в шапке только счёт, бейджа нет', async () => {
    server = { ...SETTINGS, classes: [CLS_ON] };
    const c = render();
    await settle();
    const hdr = findNodeByAttr(c, 'data-testid', 'wiki-apply-classes-header')!;
    expect(has(hdr, 'Классов: 1')).toBe(true);
    expect(has(hdr, 'выключено')).toBe(false);
  });

  it('выключенные классы поднимаются выше включённых; внутри группы — порядок как пришёл с бэка', async () => {
    // бэковый порядок: сначала включённый append, потом два выключенных — список обязан
    // поднять выключенных наверх, сохранив их взаимный порядок (без disabledFirst append
    // остался бы первым: мутации 11–12)
    server = { ...SETTINGS, classes: [CLS_ON, OFF_NOTE, CLS_OFF] };
    const c = render();
    await settle();
    const ts = texts(c);
    const iNote = ts.indexOf('note × create');
    const iCreate = ts.indexOf('session × create');
    const iAppend = ts.indexOf('session × append');
    expect(iNote).toBeGreaterThanOrEqual(0);
    expect(iNote).toBeLessThan(iCreate);
    expect(iCreate).toBeLessThan(iAppend);
    // счёт шапки — по строкам: 3 класса, 2 выключено
    const hdr = findNodeByAttr(c, 'data-testid', 'wiki-apply-classes-header')!;
    expect(has(hdr, 'Классов: 3 · выключено: 2')).toBe(true);
  });

  it('агрегат считается одним классом: его состояние «выключен» — когда выключена любая запись', async () => {
    server = { ...SETTINGS, classes: [HUMAN_OFF_CONFLICT, HUMAN_ON_NOTE] };
    const c = render();
    await settle();
    const hdr = findNodeByAttr(c, 'data-testid', 'wiki-apply-classes-header')!;
    expect(has(hdr, 'Классов: 1 · выключено: 1')).toBe(true);
    expect(has(hdr, 'выключено 1')).toBe(true);
  });
});

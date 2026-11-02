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

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import {
  mount, installFakeDom, uninstallFakeDom, findNodeByAttr, textNodes, reactProps,
} from '../../../../components/chat/__tests__/fakeDom';
import { WikiApplySettings } from '../WikiApplySettings';

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
const SETTINGS = { autoApplyEnabled: true, classes: [CLS_ON, CLS_OFF], nightly: NIGHTLY };
// Нулевой замер — как бэк сериализует класс без строки оценки (WikiClassDto по умолчанию):
// именно таким приходит каждый класс ответа PUT («Замер не возвращается», :94; BuildDto(next,
// [], null), :185). Активного никогда не выключавшегося класса в ответе PUT нет вовсе: его
// строки в settings.json не было (BuildDto строит класс-часть из settings.Classes, :206–214)
const NO_STATS = { applied7: 0, retracted7: 0, share7: 0, applied30: 0, retracted30: 0, share30: 0 };

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
function serveSettings(): void {
  req.mockImplementation(async (url: string, init?: RequestInit) => {
    const method = (init?.method ?? 'GET').toUpperCase();
    if (url !== '/wiki/settings') return null;
    if (method === 'PUT') {
      const body = JSON.parse((init?.body ?? '{}') as string);
      putDone = true;
      if (body.autoApplyEnabled !== undefined) server = { ...server, autoApplyEnabled: body.autoApplyEnabled };
      if (body.class !== undefined) {
        // выключение/возврат — та же строка состояния, что ставит бэк (:135–143): возврат
        // начинает окно класса заново (ResumedAt), выключение сохраняет его
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
      // ответ PUT — формы реального бэка: BuildDto(next, [], null): только классы с записью,
      // замер нулевой, активного класса без записи нет вовсе
      return {
        ...server,
        classes: (server.classes as Record<string, unknown>[])
          .filter(c => withEntry.has(c.class as string))
          .map(c => ({ ...c, ...NO_STATS })),
      };
    }
    if (putDone) {
      return {
        ...server,
        classes: (server.classes as Record<string, unknown>[]).map(c =>
          (c.class === 'session×append' ? { ...c, applied30: 42 } : c)),
      };
    }
    return server;
  });
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

beforeEach(() => {
  wikiStore.reset();
  req.mockReset();
  server = SETTINGS;
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
    click(toggle, 'тумблер «Применять без ревью»');
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

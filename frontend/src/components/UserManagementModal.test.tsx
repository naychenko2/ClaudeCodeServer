// Ф2f: в «Управлении пользователями» — галка «Детский режим» (PUT child-mode) и вход в окно
// настроек (кнопка «Настроить занятия» → ChildSettingsWindow с тремя разделами в слотах Ф2b).
// Прежний встроенный редактор профиля из модалки убран (файл удалит Ф1g).
// Проверяется: галка только у песочницы (local — нет, ср. not_container на бэке); до снимка
// галка гасится (её состояние — не догадка: список пользователей режима не отдаёт); клик —
// ровно один PUT child-mode, ответ которого и есть новое состояние; отказ 400 not_container —
// серверный текст под галкой; созданный здесь пользователь песочницы получает живую галку
// сразу (его состояние подтверждено фактом создания: профиля у нового нет); кнопка — только при включённом режиме, и открывает окно с четырьмя
// разделами, куда дошёл живой профиль (предмет, окно времени, опекун по имени — не id).
// Окружение — node + linkedom, как в соседних .test.tsx волны. Modal — портал в document.body:
// createPortal подменён pass-through (паттерн Modal.test.ts). childApi — mock: GET child-profile
// по id (fixed — немедленный ответ, prepared — отложенный: тест сам решает, когда придёт),
// PUT child-mode по индексу вызова (modeAnswers — отказ или ответ следующей попытки),
// getRoutes — один список маршрутов для пикера раздела «Безопасность».

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { parseHTML } from 'linkedom';
import type { ChildSettingsProfile, ChildSettingsResponse } from '../features/childMode/types';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Мобильная ветка: useIsMobile и Modal читают window.matchMedia при mount.
let mobile = false;
Object.assign(win, {
  matchMedia: () => ({
    matches: mobile, addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
});
// React решает, навешивать ли слушателя «input», по isEventSupported: 'oninput' in document.
// В linkedom его нет — без этого шима onChange не срабатывает ни на одном поле
// (тот же шим, что в Composer.childFeatures.test.tsx:24)
(doc as unknown as { oninput?: unknown }).oninput = null;
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// Modal рендерится порталом: в тесте содержимое становится inline (тот же приём, что в Modal.test.ts)
vi.mock('react-dom', async (orig) => ({ ...(await orig<typeof import('react-dom')>()), createPortal: (n: unknown) => n }));

// Фикстура списка: админ на сервере (галки не видит) и двое в песочнице.
const { usersFixture } = vi.hoisted(() => ({
  usersFixture: [
    { id: 'u-papa', username: 'Папа', role: 'admin', createdAt: '2026-01-01T00:00:00Z', executionEnvironment: 'local' },
    { id: 'u-liza', username: 'Лиза', role: 'user', createdAt: '2026-02-01T00:00:00Z', executionEnvironment: 'container' },
    { id: 'u-mira', username: 'Мира', role: 'user', createdAt: '2026-03-01T00:00:00Z', executionEnvironment: 'container' },
    // Г2: ещё один ребёнок (включён подтверждённым снимком) — в кандидаты опекунов не входит
    { id: 'u-tim', username: 'Тим', role: 'user', createdAt: '2026-04-01T00:00:00Z', executionEnvironment: 'container' },
  ],
}));
vi.mock('../lib/api', () => ({
  api: {
    users: {
      list: () => Promise.resolve(usersFixture),
      // ответ — createdUser (тест галки нового пользователя: что модалка получила из POST,
      // то и рендерит); не задан — отказа, прежние тесты создание не зовут
      create: () => (createdUser === null ? Promise.reject(new Error('не используется в этом тесте')) : Promise.resolve(createdUser)),
      delete: () => Promise.resolve(),
      resetPassword: () => Promise.resolve(),
    },
  },
}));

// GET child-profile (модалка снимает состояние при открытии, окно читает черновик при открытии):
// fixed — немедленный ответ id (по умолчанию «профиля нет»), prepared — отложенный: тест сам
// решает, когда он придёт (состояние загрузки, отказ, конкурентность).
type Resp = ChildSettingsResponse;
let getCalls: string[] = [];
const fixed = new Map<string, Resp>();
const prepared = new Map<string, Promise<Resp>>();
function deferred(id: string): { resolve: (r: Resp) => void; reject: (e: unknown) => void } {
  let resolve!: (r: Resp) => void;
  let reject!: (e: unknown) => void;
  prepared.set(id, new Promise<Resp>((res, rej) => { resolve = res; reject = rej; }));
  return { resolve, reject };
}
// PUT child-mode: запрос пишется, ответ — по индексу вызова (modeAnswers: Error — отказ с
// status/body как выносит request(); объект — ответ; пусто — эхо запроса: сервер возвращает
// то, что сохранил).
let modeCalls: { id: string; enabled: boolean }[] = [];
let modeAnswers: (Error | Resp)[] = [];
let putCalls: { id: string; body: unknown }[] = [];
let putAnswers: (Error | Resp)[] = [];
// созданный пользователь, который отдаст api.users.create (форма ToDto бэка: профиля нет)
let createdUser: unknown = null;

vi.mock('../features/childMode/api', () => ({
  childApi: {
    getChildProfile: (id: string) => {
      getCalls.push(id);
      const p = prepared.get(id);
      if (p) {
        prepared.delete(id);
        return p;
      }
      return Promise.resolve(fixed.get(id) ?? { profile: null, tutorPersonaId: '' });
    },
    setChildMode: (id: string, enabled: boolean) => {
      modeCalls.push({ id, enabled });
      const a = modeAnswers.shift();
      if (a instanceof Error) return Promise.reject(a);
      if (a !== undefined) return Promise.resolve(a);
      return Promise.resolve({ profile: { enabled } as ChildSettingsProfile, tutorPersonaId: '' });
    },
    // Пикер раздела «Безопасность»: одна запись пула — на ней и выбирается маршрут.
    getRoutes: () => Promise.resolve([
      {
        providerKey: 'anthropic-api-kids', kind: 'pool', label: 'Anthropic · ключ «kids»',
        priced: true, requiresAck: false, models: [{ id: 'sonnet', label: 'Sonnet' }],
      },
    ]),
    // PUT child-profile (окно «Сохранить»): по индексу вызова, та же форма, что у мока окна.
    putChildProfile: (id: string, body: unknown) => {
      putCalls.push({ id, body });
      const a = putAnswers.shift();
      if (a instanceof Error) return Promise.reject(a);
      if (a !== undefined) return Promise.resolve(a);
      return Promise.resolve({ profile: body as ChildSettingsProfile, tutorPersonaId: '' });
    },
  },
}));

// react, react-dom и модалка импортируются после стабов: canUseDOM и isInputEventSupported
// вычисляются на загрузке react-dom — при статическом импорте они были бы false (window ещё
// не .stubbed), и React не выдавал бы onChange на «input»: поля диалога «Добавить» нельзя
// было бы набрать (тот же порядок, что в ChildTutorCard.test.tsx:52-55)
const { act, createElement } = await import('react');
const { createRoot } = await import('react-dom/client');
const { FLAGS, setAllFlags } = await import('../lib/featureFlags');
const { UserManagementModal } = await import('./UserManagementModal');

// Живой корень последнего mountModal: следующий тест сначала снимает его (паттерн unmount
// из ChildSettingsWindow.test.tsx). Без этого прежний экземпляр остаётся подписанным на
// флаги: setAllFlags чужого теста дёргает его перерендер, эффект с childModeUi в зависимостях
// перечитывается — и прежний компонент съедает prepared-ответ нового теста своим запросом.
let liveRoot: ReturnType<typeof createRoot> | null = null;

async function mountModal() {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  liveRoot = root;
  // Список и снимки приходят в эффектах асинхронно: рендер + выгнать микротаски (как settle в соседних тестах)
  await act(async () => {
    root.render(createElement(UserManagementModal, { currentUserId: 'u-papa', onClose: () => {} }));
    for (let i = 0; i < 8; i++) await Promise.resolve();
  });
}

const row = (id: string) => doc.querySelector(`[data-testid="user-row-${id}"]`) as HTMLElement | null;
const checkboxIn = (r: HTMLElement) => r.querySelector('[role="checkbox"][aria-label="Детский режим"]') as HTMLButtonElement | null;
const openBtnIn = (r: HTMLElement) => r.querySelector('[data-testid="child-settings-open"]') as HTMLElement | null;
const modeErrorIn = (r: HTMLElement) => r.querySelector('[data-testid="child-mode-error"]') as HTMLElement | null;
const tabs = () => Array.from(doc.querySelectorAll('[role="tab"]'));
const windowRoot = () => doc.querySelector('[data-testid="child-settings-window"]') as HTMLElement | null;

// Клик + выгнать цепочку then/catch обработчика (hops микротасков с запасом)
async function clickSettle(el: HTMLElement) {
  await act(async () => {
    el.click();
    for (let i = 0; i < 10; i++) await Promise.resolve();
  });
}
const clickTab = async (label: string) => {
  const b = tabs().find((x) => (x.textContent ?? '').includes(label));
  if (!b) throw new Error(`вкладка «${label}» не найдена`);
  await clickSettle(b as HTMLElement);
};

const httpErr = (status: number, code: string, message: string) =>
  Object.assign(new Error('отказ'), { status, body: { code, message } });

// Ввод в поле диалога: сеттер прототипа, а не el.value — React оборачивает аксессор
// экземпляра своим value-tracker и прямую запись не замечает (тот же приём, что typeInto
// в Composer.childFeatures.test.tsx:130), затем input-событие
async function typeIn(el: HTMLInputElement, value: string) {
  const desc = Object.getOwnPropertyDescriptor((win as unknown as { HTMLInputElement: { prototype: object } }).HTMLInputElement.prototype, 'value');
  if (desc?.set) desc.set.call(el, value); else el.value = value;
  await act(async () => { el.dispatchEvent(new win.Event('input', { bubbles: true })); });
}
const btnByText = (label: string) =>
  Array.from(doc.querySelectorAll('button')).find((b) => (b.textContent ?? '').trim() === label) as HTMLElement;

// Г2: поле пикера опекунов — по подписи Field (тот же путь, что selectByLabel в
// ChildSafetySection.test); React-props onChange нативного select (linkedom не умеет select-события)
const guardianSelect = () => {
  const w = windowRoot()!;
  const lbl = Array.from(w.querySelectorAll('label')).find((l) => (l.textContent ?? '').includes('Добавить опекуна'));
  return (lbl?.parentElement?.querySelector('select') ?? null) as Element | null;
};
// значения опций без placeholder ('' — «Выбери пользователя», не кандидат)
const selectValues = (sel: Element) => Array.from(sel.querySelectorAll('option')).map((o) => o.getAttribute('value')).filter(Boolean);
const changeSelect = async (el: Element, v: string) => {
  const key = Object.keys(el).find((k) => k.startsWith('__reactProps$'));
  const props = key ? (el as unknown as Record<string, unknown>)[key] as { onChange?: (e: { target: { value: string } }) => void } : undefined;
  await act(async () => { props?.onChange?.({ target: { value: v } }); });
};

// Живой профиль Лизы (форма контракта окна, Ф2a): предмет, окно времени, опекун-куратор с
// флагом общих проектов, согласие на риск — теми и проверяем, что в окно дошёл профиль
// сервера, а не пустой черновик.
const lizaProfile: ChildSettingsProfile = {
  enabled: true,
  guardianUserIds: ['u-papa'],
  providerKey: 'anthropic-api-kids',
  model: 'sonnet',
  spaces: { lessons: { windows: [{ from: '09:00', to: '12:00' }], dailyMinutes: 30 } },
  subjects: [{ id: 'math', label: 'Математика', glyph: '×', color: 'accent' }],
  acknowledgeRouteRisk: true,
  routeRiskAcknowledgedAt: '2026-11-18T10:00:00',
  routeRiskAcknowledgedBy: 'Папа',
  guardians: [{ userId: 'u-papa', sharedProjectsFlag: true }],
  sharedProjectsInstanceEnabled: true,
};
const lizaResp: Resp = { profile: lizaProfile, tutorPersonaId: 'p-liza-tutor' };

beforeEach(async () => {
  // между тестами дерево чистят: поиски идут по всему документу; прежний корень снимается
  // до чистки DOM — иначе его подписки на флаги живут в следующем тесте (см. mountModal)
  if (liveRoot) {
    const r = liveRoot;
    liveRoot = null;
    await act(async () => { r.unmount(); });
  }
  doc.getElementById('root')!.innerHTML = '';
  setAllFlags({});
  mobile = false;
  getCalls = [];
  modeCalls = [];
  modeAnswers = [];
  putCalls = [];
  putAnswers = [];
  createdUser = null;
  fixed.clear();
  prepared.clear();
});

describe('UserManagementModal: галка «Детский режим» (Ф2f)', () => {
  it('флаг включён: у local галки нет; у container — галка и кнопка по снимку; клик — один PUT child-mode', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    // Мира — выключена (профиля нет), Лиза — включена: кнопка видна только ей.
    fixed.set('u-liza', lizaResp);
    await mountModal();

    const papa = row('u-papa')!;
    expect(papa, 'строка админа на месте').toBeTruthy();
    expect(checkboxIn(papa), 'local-пользователю галки нет').toBeNull();

    const rLiza = row('u-liza')!;
    const cbLiza = checkboxIn(rLiza)!;
    expect(cbLiza, 'галка у песочницы').toBeTruthy();
    expect(cbLiza.getAttribute('aria-checked'), 'состояние из снимка').toBe('true');
    expect(openBtnIn(rLiza), 'кнопка при включённом режиме').toBeTruthy();
    const rMira = row('u-mira')!;
    expect(checkboxIn(rMira)!.getAttribute('aria-checked'), 'выключенный подтверждён снимком').toBe('false');
    expect(openBtnIn(rMira), 'при выключенном режима кнопки нет').toBeNull();

    // Галка — узкая ручка: Мира включает режим одним PUT child-mode(true), тела профиля нет.
    await clickSettle(checkboxIn(rMira)!);
    expect(modeCalls, 'один запрос, своим id и значением').toEqual([{ id: 'u-mira', enabled: true }]);
    // Ответ — сохранённый профиль сервера: галка Миры стала включённой, её кнопка появилась.
    expect(checkboxIn(rMira)!.getAttribute('aria-checked'), 'галка по ответу сохранения').toBe('true');
    expect(openBtnIn(rMira), 'кнопка появилась').toBeTruthy();
    // Лиза не тронута чужим включением.
    expect(checkboxIn(row('u-liza')!)!.getAttribute('aria-checked')).toBe('true');
    expect(modeCalls.length, 'чужого запроса не было').toBe(1);
  });

  it('до снимка галка гасится: состояния ещё нет; свой ответ — галка живая, кнопка по снимку', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    const d = deferred('u-liza'); // ответ висит
    await mountModal();
    const r = row('u-liza')!;
    const cb = checkboxIn(r)!;
    expect(cb, 'галка есть').toBeTruthy();
    expect(cb.disabled, 'до снимка галка гасится').toBe(true);
    expect(openBtnIn(r), 'кнопки до снимка нет').toBeNull();
    // Свой ответ пришёл — галка живая и по снимку; кнопка появилась.
    await act(async () => { d.resolve(lizaResp); });
    expect(checkboxIn(r)!.disabled, 'после снимка галка живая').toBe(false);
    expect(checkboxIn(r)!.getAttribute('aria-checked'), 'состояние из снимка').toBe('true');
    expect(openBtnIn(r), 'кнопка по включённому снимку').toBeTruthy();
  });

  it('отказ GET — текст под галкой; галка остаётся гаситься, PUT не уходит', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    const d = deferred('u-liza');
    await mountModal();
    const r = row('u-liza')!;
    await act(async () => { d.reject(httpErr(404, 'user_not_found', 'Пользователь не найден.')); });
    const err = modeErrorIn(r);
    expect(err, 'текст отказа под галкой').toBeTruthy();
    expect(err!.textContent).toContain('Пользователь не найден.');
    expect(checkboxIn(r)!.disabled, 'без состояния галка гасится').toBe(true);
    expect(modeCalls.length, 'PUT не уходит').toBe(0);
  });

  it('отказ 400 not_container — серверный текст под галкой; галка при своём подтверждённом состоянии', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    // Снимок: выключена. Среда сменилась с момента чтения списка — сервер отбивает включение.
    modeAnswers = [httpErr(400, 'not_container', 'Детский режим включается только пользователям со средой исполнения container.')];
    await mountModal();
    const r = row('u-mira')!;
    await clickSettle(checkboxIn(r)!);
    const err = modeErrorIn(r);
    expect(err, 'текст под галкой').toBeTruthy();
    expect(err!.textContent).toContain('только пользователям со средой исполнения');
    expect(checkboxIn(r)!.getAttribute('aria-checked'), 'галка осталась выключенной').toBe('false');
    expect(checkboxIn(r)!.disabled, 'галка снова живая').toBe(false);
    expect(openBtnIn(r), 'кнопки нет').toBeNull();
  });

  it('флаг выключен: ни галки, ни запросов состояния; строки пользователей на месте', async () => {
    await mountModal();
    expect(checkboxIn(row('u-liza')!), 'без флага галки нет').toBeNull();
    expect(checkboxIn(row('u-mira')!)).toBeNull();
    expect(getCalls.length, 'без флага запросов состояния нет').toBe(0);
    expect(row('u-liza')!.textContent).toContain('Лиза');
  });

  it('мобила (360): галка и кнопка в карточке пользователя', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    mobile = true;
    fixed.set('u-liza', lizaResp);
    await mountModal();
    expect(checkboxIn(row('u-papa')!), 'local — без галки и на мобиле').toBeNull();
    const r = row('u-liza')!;
    expect(checkboxIn(r), 'галка в мобильной карточке').toBeTruthy();
    expect(openBtnIn(r), 'кнопка в мобильной карточке').toBeTruthy();
  });

  it('ответ одного пользователя не входит в чужое состояние: гонка снимков', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    const dLiza = deferred('u-liza');
    await mountModal();
    const rLiza = row('u-liza')!;
    const rMira = row('u-mira')!;
    // Мира получила свой default-ответ («профиля нет»), Лиза всё ещё ждёт.
    expect(checkboxIn(rMira)!.getAttribute('aria-checked'), 'Мира по своему снимку').toBe('false');
    expect(checkboxIn(rLiza)!.disabled, 'Лиза ждёт — галка гасится').toBe(true);
    await act(async () => { dLiza.resolve(lizaResp); });
    expect(checkboxIn(rLiza)!.getAttribute('aria-checked'), 'свой профиль дошёл до своей галки').toBe('true');
    expect(checkboxIn(rMira)!.getAttribute('aria-checked'), 'чужой профиль не тронул Миру').toBe('false');
  });

  it('создал container-пользователя: галка живая сразу, без повторного открытия модалки', async () => {
    // Доработка по ревью 37e1a4f4: upsertUser добавлял строку, но не заводил состояние галки —
    // loadChildStates (эффект открытия) был единственным его путём, и галка новой строки
    // была мёртва до повторного открытия модалки.
    setAllFlags({ [FLAGS.childMode]: true });
    // Ответ api.users.create — форма ToDto бэка: профиля у созданного нет (POST его не принимает).
    createdUser = { id: 'u-new', username: 'Новый', role: 'user', createdAt: '2026-11-20T00:00:00Z', executionEnvironment: 'container' };
    await mountModal();

    // «Добавить» в заголовке → диалог → имя и пароль → «Создать».
    await clickSettle(btnByText('Добавить'));
    // Поля диалога — единственные input в документе. По autocomplete выбирать нельзя:
    // React 19 пишет атрибут дословно (autoComplete), а имена атрибутов linkedom
    // чувствительны к регистру (в браузере — нет). type — то, как их задал сам диалог.
    await typeIn(doc.querySelector('input[type="text"]') as HTMLInputElement, 'Новый');
    await typeIn(doc.querySelector('input[type="password"]') as HTMLInputElement, 'password12345');
    await clickSettle(btnByText('Создать'));
    const r = row('u-new')!;
    expect(r, 'строка созданного пользователя на месте').toBeTruthy();
    const cb = checkboxIn(r)!;
    expect(cb, 'галка есть').toBeTruthy();
    expect(cb.disabled, 'галка живая без повторного открытия').toBe(false);
    expect(cb.getAttribute('aria-checked'), 'состояние подтверждено самим фактом создания: выключен').toBe('false');
    expect(getCalls, 'лишнего GET для нового нет').not.toContain('u-new');
    // Клик по живой галке — ровно один PUT child-mode, ответ которого и становится состоянием.
    await clickSettle(cb);
    expect(modeCalls, 'ровно один PUT, своим id и значением').toEqual([{ id: 'u-new', enabled: true }]);
    expect(checkboxIn(r)!.getAttribute('aria-checked'), 'галка по ответу сохранения').toBe('true');
  });
});

describe('UserManagementModal: вход в окно настроек (Ф2f, слоты Ф2b наполнены Ф2c–Ф2e)', () => {
  it('«Настроить занятия» — окно с четырьмя разделами; в разделы дошёл живой профиль', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    fixed.set('u-liza', lizaResp);
    await mountModal();
    await clickSettle(openBtnIn(row('u-liza')!)!);

    const labels = tabs().map((t) => (t.textContent ?? '').trim());
    expect(labels, 'четыре раздела по подписям макета').toEqual(['Обзор', 'Предметы', 'Время и лимиты', 'Безопасность и согласие']);

    // «Предметы» — живой предмет профиля (Ф2c в слоте).
    await clickTab('Предметы');
    const w = windowRoot()!;
    expect(w.querySelector('[data-testid="child-subjects-section"]'), 'раздел наполнен').toBeTruthy();
    expect(w.textContent, 'предмет профиля дошёл').toContain('Математика');

    // «Время и лимиты» — окно 09:00–12:00 из space lessons (Ф2d в слоте).
    await clickTab('Время и лимиты');
    expect(windowRoot()!.querySelectorAll('[data-testid="time-window"]').length, 'живое окно дошло').toBe(1);

    // «Безопасность и согласие» — опекун по имени, не по id (имена даёт вызывающий) (Ф2e в слоте).
    await clickTab('Безопасность');
    const wS = windowRoot()!;
    expect(wS.querySelector('[data-testid="child-safety-section"]'), 'раздел наполнен').toBeTruthy();
    expect(wS.querySelector('[data-testid="guardian-u-papa"]')!.textContent, 'опекун назван именем').toContain('Папа');
  });

  it('отказ сохранения subject_*: текст в окне и отвергнутый предмет обведён (цепочка R-Ф2b)', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    fixed.set('u-liza', lizaResp);
    // Сервер отбивает сохранение по предмету «math» — id в тексте отказа (структурированного поля нет).
    putAnswers = [httpErr(400, 'subject_label_invalid', 'Название предмета «math» — от 1 до 40 символов.')];
    await mountModal();
    await clickSettle(openBtnIn(row('u-liza')!)!);
    await clickTab('Предметы');
    await clickSettle(windowRoot()!.querySelector('[data-testid="add-subject"]') as HTMLElement); // черновик dirty
    await clickSettle(doc.querySelector('[data-testid="child-settings-save"]') as HTMLElement);
    const w = windowRoot()!;
    expect(w.querySelector('[data-testid="child-settings-save-error"]')!.textContent, 'серверный текст в открытом окне').toContain('Название предмета «math»');
    const failed = Array.from(w.querySelectorAll('[data-testid="subject-row"]')).filter((x) => x.getAttribute('data-error') === 'true');
    expect(failed.length, 'обведён ровно один предмет').toBe(1);
    expect(failed[0].textContent, 'обведена строка отвергнутого предмета').toContain('Математика');
  });

  it('закрыл окно — вернулся к списку; окно не оставило второго запроса', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    fixed.set('u-liza', lizaResp);
    await mountModal();
    await clickSettle(openBtnIn(row('u-liza')!)!);
    expect(windowRoot()).toBeTruthy();
    const before = getCalls.length;
    // Закрытие — крестик Modal'а (у окна он есть: своей primary-кнопки в title нет).
    const closeBtn = Array.from(doc.querySelectorAll('button')).find((b) => (b.getAttribute('aria-label') ?? '').includes('Закрыть')) as HTMLElement;
    await clickSettle(closeBtn);
    expect(windowRoot(), 'окна нет').toBeNull();
    expect(row('u-liza'), 'список на месте').toBeTruthy();
    expect(getCalls.length, 'пока окно открыто — новых чтений нет').toBe(before);
  });
});

describe('UserManagementModal: редактор опекунов (Г2)', () => {
  // Тим — второй ребёнок фикстуры: включён подтверждённым снимком (тот же профиль, что у Лизы).
  const timResp: Resp = { profile: { ...lizaProfile, guardianUserIds: [] }, tutorPersonaId: 'p-tim-tutor' };

  it('пикер предлагает только не-детских: Мира да; Лиза (сама), Тим (ребёнок) и Папа (уже опекун) — нет; «Добавить»+«Сохранить» — целое тело, согласие не тронут', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    // Профиль без согласия (false + снятая пара): false в теле — унаследованное значение
    // черновика, а не работа патча правки (с true-фикстурой эта строка ничего не проверяла бы).
    fixed.set('u-liza', {
      profile: { ...lizaProfile, acknowledgeRouteRisk: false, routeRiskAcknowledgedAt: null, routeRiskAcknowledgedBy: null },
      tutorPersonaId: 'p-liza-tutor',
    });
    fixed.set('u-tim', timResp);
    await mountModal();
    await clickSettle(openBtnIn(row('u-liza')!)!);
    await clickTab('Безопасность');
    const w = windowRoot()!;
    const sel = guardianSelect();
    expect(sel, 'пикер в разделе').not.toBeNull();
    expect(selectValues(sel!), 'кандидаты — без детей, без себя, без текущих опекунов').toEqual(['u-mira']);

    await changeSelect(sel!, 'u-mira');
    await clickSettle(w.querySelector('[data-testid="guardian-add-btn"]') as HTMLElement);
    const rowMira = w.querySelector('[data-testid="guardian-u-mira"]') as HTMLElement;
    expect(rowMira, 'строка добавленного — до сохранения').not.toBeNull();
    expect(rowMira.textContent, 'по имени, не по id').toContain('Мира');
    expect(rowMira.querySelector('[data-testid="guardian-flag-unknown-u-mira"]'), 'флаг неизвестен — строка про сохранение').not.toBeNull();

    // «Сохранить» — PUT из живого черновика: заменён только список, согласие и остальной профиль целые
    const saveBtn = doc.querySelector('[data-testid="child-settings-save"]') as HTMLButtonElement;
    expect(saveBtn.disabled, 'есть несохранённое — кнопка живая').toBe(false);
    await clickSettle(saveBtn);
    expect(putCalls.length, 'один PUT').toBe(1);
    const body = putCalls[0].body as ChildSettingsProfile;
    expect(body.guardianUserIds, 'порядок: текущий + новый в конец').toEqual(['u-papa', 'u-mira']);
    expect(body.acknowledgeRouteRisk, 'снятая галка правкой опекунов не перезаписана (Г2-д1б)').toBe(false);
    expect(body.subjects, 'предмет не затёрт').toEqual(lizaProfile.subjects);
    expect(body.spaces, 'окно времени не затёрто').toEqual(lizaProfile.spaces);
    expect(body.providerKey).toBe('anthropic-api-kids');
    expect(body.model, 'маршрут не затёрт').toBe('sonnet');
  });

  it('отзыв + опекун: снятая в сессии галка согласия и добавление опекуна — тело несёт false, не принудительное true (Г2-д1б)', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    // Живое согласие в ответе (true + пара момента и автора): отзыв — снятие галки в окне,
    // не фикстурой: сценарий потери явного отзыва правкой списка.
    fixed.set('u-liza', lizaResp);
    await mountModal();
    await clickSettle(openBtnIn(row('u-liza')!)!);
    await clickTab('Безопасность');
    const w = windowRoot()!;

    // Явный отзыв: снятие галки согласия (onAcknowledgeChange → патч acknowledgeRouteRisk: false)
    const ack = w.querySelector('[role="checkbox"][aria-label="Я понимаю риски и беру их на себя"]') as HTMLElement;
    expect(ack.getAttribute('aria-checked'), 'согласие из снимка стоит').toBe('true');
    await clickSettle(ack);

    // Правка опекунов после отзыва: добавление Мире в конец
    await changeSelect(guardianSelect()!, 'u-mira');
    await clickSettle(w.querySelector('[data-testid="guardian-add-btn"]') as HTMLElement);

    await clickSettle(doc.querySelector('[data-testid="child-settings-save"]') as HTMLElement);
    expect(putCalls.length, 'один PUT').toBe(1);
    const body = putCalls[0].body as ChildSettingsProfile;
    expect(body.guardianUserIds, 'список изменён').toEqual(['u-papa', 'u-mira']);
    expect(body.acknowledgeRouteRisk, 'явный отзыв не стёрт правкой опекунов').toBe(false);
  });

  it('«Убрать» + отказ guardian_not_found: серверный текст в окне, изменённый список не пропал молча', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    fixed.set('u-liza', lizaResp);
    putAnswers = [httpErr(400, 'guardian_not_found', 'Среди опекунов есть id несуществующего пользователя.')];
    await mountModal();
    await clickSettle(openBtnIn(row('u-liza')!)!);
    await clickTab('Безопасность');
    const w = windowRoot()!;
    await changeSelect(guardianSelect()!, 'u-mira');
    await clickSettle(w.querySelector('[data-testid="guardian-add-btn"]') as HTMLElement);
    const rm = w.querySelector('[data-testid="guardian-remove-u-papa"]') as HTMLElement;
    expect(rm, 'кнопка убрать у строки').not.toBeNull();
    await clickSettle(rm);
    await clickSettle(doc.querySelector('[data-testid="child-settings-save"]') as HTMLElement);
    expect(putCalls.length).toBe(1);
    expect((putCalls[0].body as ChildSettingsProfile).guardianUserIds, 'тело — изменённый список').toEqual(['u-mira']);
    const err = w.querySelector('[data-testid="child-settings-save-error"]');
    expect(err, 'отказ в открытом окне').not.toBeNull();
    expect(err!.textContent).toContain('Среди опекунов есть id несуществующего пользователя.');
    const wAfter = windowRoot()!;
    expect(wAfter.querySelector('[data-testid="guardian-u-mira"]'), 'добавленный не пропал молча').not.toBeNull();
    expect(wAfter.querySelector('[data-testid="guardian-u-papa"]'), 'убранный вернулся бы только повтором действия').toBeNull();
  });

  it('«Убрать» + «Сохранить» — тело без него; ответ сервера становится списком окна', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    fixed.set('u-liza', lizaResp);
    await mountModal();
    await clickSettle(openBtnIn(row('u-liza')!)!);
    await clickTab('Безопасность');
    const w = windowRoot()!;
    await clickSettle(w.querySelector('[data-testid="guardian-remove-u-papa"]') as HTMLElement);
    await clickSettle(doc.querySelector('[data-testid="child-settings-save"]') as HTMLElement);
    expect(putCalls.length).toBe(1);
    expect((putCalls[0].body as ChildSettingsProfile).guardianUserIds).toEqual([]);
    const wAfter = windowRoot()!;
    expect(wAfter.querySelector('[data-testid="guardian-u-papa"]'), 'списка нет — он по ответу').toBeNull();
    expect(wAfter.textContent, 'empty-state по ответу').toContain('Опекунов нет');
  });
});

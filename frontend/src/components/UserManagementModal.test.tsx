// KM-F12 + доработка по ревью R-KM-F12: раздел «Детский режим» в «Пользователях» — только
// пользователям песочницы (минор 3, решение Софьи), свёрнут по умолчанию в строку статуса,
// раскрытие по нажатию. Тесты: граница container/local, раздел без флага на мобиле (минор 1),
// после сохранения профиль обновляется только у своего пользователя (минор 2).
// Д1 (дефект 06201244): при раскрытии живой профиль читается GET-ом — до ответа и при отказе
// редактора нет (пустой черновик затёр бы профиль), при отказе — Notice с «Повторить»; ответ
// проверяется по id: чужой профиль в чужую строку не попадает.
// ХВ5 (хвост 69c3ee96): строка статуса без снимка ничего не утверждает («Детский режим…»),
// при отказе — «не загрузилось», «выключен» — только по подтверждённому снимку; «Повторить» —
// prop action у Notice (он держит мобильный перенос); тело Notice — только серверный текст;
// тесты повторного раскрытия из loaded/error и конкурентность висящего запроса.
// Окружение — node + linkedom, как в соседних .test.tsx волны. Modal — портал в document.body:
// createPortal подменён pass-through (паттерн Modal.test.ts); список пользователей — фикстура
// через mock api.users.list; PUT child-profile — mock childApi, ответ повторяет тело запроса;
// GET child-profile — mock с отложенными ответами: тест сам решает, что и когда придёт.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Мобильная ветка: useIsMobile читает window.matchMedia при mount.
let mobile = false;
Object.assign(win, {
  matchMedia: () => ({
    matches: mobile, addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// Modal рендерится порталом: в тесте содержимое становится inline (тот же приём, что в Modal.test.ts)
vi.mock('react-dom', async (orig) => ({ ...(await orig<typeof import('react-dom')>()), createPortal: (n: unknown) => n }));

// Фикстура списка: админ на сервере (раздела не видит) и двое в песочнице — своя и «соседняя»
// для теста сохранения. Диалоговые ручки в этом тесте не вызываются; getRoutes mocked —
// редактор профиля зовёт его при открытии (пикер маршрута, Т4f).
const { usersFixture } = vi.hoisted(() => ({
  usersFixture: [
    { id: 'u-papa', username: 'Папа', role: 'admin', createdAt: '2026-01-01T00:00:00Z', executionEnvironment: 'local' },
    { id: 'u-liza', username: 'Лиза', role: 'user', createdAt: '2026-02-01T00:00:00Z', executionEnvironment: 'container' },
    { id: 'u-mira', username: 'Мира', role: 'user', createdAt: '2026-03-01T00:00:00Z', executionEnvironment: 'container' },
  ],
}));
vi.mock('../lib/api', () => ({
  api: {
    users: {
      list: () => Promise.resolve(usersFixture),
      create: () => Promise.reject(new Error('не используется в этом тесте')),
      delete: () => Promise.resolve(),
      resetPassword: () => Promise.resolve(),
    },
  },
}));

// PUT child-profile: записываем запрос; ответ повторяет тело и добавляет тьютора —
// как реальное сохранение (сервер возвращает то, что сохранил). saveAsSaved — то же, что
// отдал PUT: следующий GET по этому id отвечает сохранённым профилем (сервер возвращает
// то, что сохранил), «полный цикл» модалки после сохранения остаётся честным.
let putCalls: { userId: string; body: unknown }[] = [];
// GET child-profile (Д1): по умолчанию — сохранённый профиль id или пустой (пользователь,
// которого никогда не включали). prepared — ответ, который тест приготовил для конкретного
// вызова: он сам решает, когда тот придёт (состояние загрузки, отказ, порядок ответов).
type ProfileResp = { profile: unknown; tutorPersonaId: string };
let getProfileCalls: string[] = [];
const saveAsSaved = new Map<string, ProfileResp>();
const prepared = new Map<string, Promise<ProfileResp>>();
// Отложенный ответ для id: resolve/reject — во власти теста (загрузка «висит», пока не ответили).
function deferred(id: string): { resolve: (r: ProfileResp) => void; reject: (e: unknown) => void } {
  let resolve!: (r: ProfileResp) => void;
  let reject!: (e: unknown) => void;
  prepared.set(id, new Promise<ProfileResp>((res, rej) => { resolve = res; reject = rej; }));
  return { resolve, reject };
}
function resolveProfile(id: string, resp: ProfileResp) {
  deferred(id).resolve(resp);
}
vi.mock('../features/childMode/api', () => ({
  childApi: {
    putProfile: (userId: string, body: unknown) => {
      putCalls.push({ userId, body });
      const res = { profile: body, tutorPersonaId: 'p-tutor-new' };
      saveAsSaved.set(userId, res);
      return Promise.resolve(res);
    },
    getProfile: (userId: string) => {
      getProfileCalls.push(userId);
      const p = prepared.get(userId);
      if (p) {
        prepared.delete(userId);
        return p;
      }
      return Promise.resolve(saveAsSaved.get(userId) ?? { profile: null, tutorPersonaId: '' });
    },
    // Список маршрутов для пикера (Т4f): одна запись пула — на ней и выбирается провайдер.
    getRoutes: () => Promise.resolve([
      {
        providerKey: 'anthropic-api-kids', kind: 'pool', label: 'Anthropic · ключ «kids»',
        priced: true, requiresAck: false, models: [{ id: 'sonnet', label: 'Sonnet' }],
      },
    ]),
  },
}));

import { FLAGS, setAllFlags } from '../lib/featureFlags';
import { UserManagementModal } from './UserManagementModal';

async function mountModal() {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  // Список приходит в эффекте асинхронно: рендер + выгнать микротаски промиса (как settle в ChildProfileEditor.test)
  await act(async () => {
    root.render(createElement(UserManagementModal, { currentUserId: 'u-papa', onClose: () => {} }));
    for (let i = 0; i < 6; i++) await Promise.resolve();
  });
}

const editors = () => Array.from(doc.querySelectorAll('[data-testid="child-profile-editor"]'));
const toggles = () => Array.from(doc.querySelectorAll('[data-testid="child-section-toggle"]'));
const row = (id: string) => doc.querySelector(`[data-testid="user-row-${id}"]`) as HTMLElement | null;
const toggleIn = (r: HTMLElement) => r.querySelector('[data-testid="child-section-toggle"]') as HTMLElement | null;
const editorIn = (r: HTMLElement) => r.querySelector('[data-testid="child-profile-editor"]') as HTMLElement | null;
// Состояния загрузки профиля (Д1): блок «Загрузка…», Notice отказа с кнопкой «Повторить».
const loadingIn = (r: HTMLElement) => r.querySelector('[data-testid="child-profile-loading"]') as HTMLElement | null;
const errorIn = (r: HTMLElement) => r.querySelector('[data-testid="child-profile-error"]') as HTMLElement | null;
const retryIn = (r: HTMLElement) => r.querySelector('[data-testid="child-profile-retry"]') as HTMLButtonElement | null;
const saveIn = (r: HTMLElement) => r.querySelector('[data-testid="save-profile"]') as HTMLElement | null;
const enableToggle = (ed: HTMLElement) => ed.querySelector('[role="checkbox"][aria-label="Детский режим"]') as HTMLButtonElement;
const saveBtn = (ed: HTMLElement) => ed.querySelector('[data-testid="save-profile"]') as HTMLButtonElement;
// Маршрут помощника (providerKey/model) — пикер на вкладке «Предметы» (Т4f): первый Select
// его секции. К нему переходят внутри редактора.
const providerSelect = (ed: HTMLElement) => ed.querySelector('[data-testid="assistant-route"] select') as HTMLSelectElement;
const clickTabIn = async (ed: HTMLElement, label: string) => {
  const b = Array.from(ed.querySelectorAll('[role="tab"]')).find((x) => (x.textContent ?? '').includes(label)) as HTMLElement | undefined;
  if (!b) throw new Error(`вкладка «${label}» не найдена`);
  await click(b);
};

async function click(el: HTMLElement) {
  await act(async () => { el.click(); });
}
// Клик + выгнать цепочку then/catch обработчика ( hops микротасков с запасом, как settle в ChildProfileEditor.test)
async function clickSettle(el: HTMLElement) {
  await act(async () => {
    el.click();
    for (let i = 0; i < 8; i++) await Promise.resolve();
  });
}

// Набрать в поле ввода: React держит props узла под __reactProps$…; зовём onChange напрямую
// (тот же приём, что в ChildProfileEditor.test), без событийной обвязки linkedom.
const reactProps = (el: HTMLElement): { onChange?: (e: { target: { value: string } }) => void } | undefined => {
  const key = Object.keys(el).find((k) => k.startsWith('__reactProps$'));
  return key ? (el as unknown as Record<string, unknown>)[key] as { onChange?: (e: { target: { value: string } }) => void } : undefined;
};
const typeInto = async (el: HTMLInputElement | HTMLSelectElement, v: string) => {
  await act(async () => { reactProps(el)?.onChange?.({ target: { value: v } }); });
};

beforeEach(() => {
  // между тестами дерево чистят: editors()/toggles() ищут по всему документу
  doc.getElementById('root')!.innerHTML = '';
  setAllFlags({});
  mobile = false;
  putCalls = [];
  getProfileCalls = [];
  saveAsSaved.clear();
  prepared.clear();
});

// Живой профиль Лизы для тестов загрузки (Д1): включённый режим, окно, предмет — тем и
// проверяем, что в редактор дошёл профиль сервера, а не пустой черновик.
const lizaProfile: ProfileResp = {
  profile: {
    enabled: true,
    guardianUserIds: [],
    providerKey: 'anthropic-api-kids',
    model: 'sonnet',
    spaces: { lessons: { windows: [{ from: '09:00', to: '12:00' }], dailyMinutes: 30 } },
    subjects: [
      { id: 'math', label: 'Математика', glyph: '×', color: 'accent', providerKey: 'anthropic-api-kids', model: 'sonnet', enabled: true },
    ],
  },
  tutorPersonaId: 'p-liza-tutor',
};

describe('UserManagementModal: раздел «Детский режим» только у песочницы, свёрнут по умолчанию', () => {
  it('флаг включён: раздела нет у local-пользователя; у container — строка статуса, редактор после нажатия', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    await mountModal();
    // Админ на сервере: ни строки статуса, ни редактора (ср. not_container на бэке).
    const papa = row('u-papa')!;
    expect(papa, 'строка админа на месте').toBeTruthy();
    expect(toggleIn(papa), 'local-пользователю раздела нет').toBeNull();
    expect(editorIn(papa)).toBeNull();
    // Песочница: свёрнутая строка без утверждения статуса (снимка ещё нет), редактора до нажатия нет.
    for (const id of ['u-liza', 'u-mira']) {
      const r = row(id)!;
      const t = toggleIn(r);
      expect(t, `строка статуса ${id}`).toBeTruthy();
      expect(t!.textContent).toContain('Детский режим…');
      expect(t!.getAttribute('aria-expanded'), 'по умолчанию свёрнут').toBe('false');
      expect(editorIn(r), 'редактор до нажатия не смонтирован').toBeNull();
      // «после основных полей»: раздел — последний блок строки
      const last = r!.children[r!.children.length - 1];
      expect(last.querySelector('[data-testid="child-section-toggle"]'), 'раздел после основных полей').toBeTruthy();
    }
    // Нажатие — редактор внутри строки этого пользователя; соседняя строка не раскрылась.
    // clickSettle: после раскрытия профиль грузится GET-ом (Д1), редактор монтируется по ответу.
    await clickSettle(toggleIn(row('u-liza')!)!);
    const ed = editorIn(row('u-liza')!);
    expect(ed, 'редактор после нажатия').toBeTruthy();
    expect(ed!.textContent, 'редактор привязан к этому пользователю').toContain('Лиза');
    expect(editorIn(row('u-mira')!), 'соседний раздел не раскрылся').toBeNull();
  });

  it('флаг выключен: 0 узлов раздела; сами строки пользователей на месте', async () => {
    await mountModal();
    expect(editors().length).toBe(0);
    expect(toggles().length, 'без флага нет и свёрнутых строк').toBe(0);
    expect(row('u-liza'), 'строка без флага остаётся').toBeTruthy();
    expect(row('u-liza')!.textContent).toContain('Лиза');
  });

  it('флаг выключен + мобила (360): 0 узлов раздела (R-KM-F12, минор 1)', async () => {
    mobile = true;
    await mountModal();
    expect(editors().length).toBe(0);
    expect(toggles().length, 'мобильная ветка без флага не рендерит раздел').toBe(0);
    expect(row('u-liza'), 'строка без флага остаётся').toBeTruthy();
    expect(row('u-liza')!.textContent).toContain('Лиза');
  });

  it('мобила (360): строка статуса у песочницы внутри модалки, редактор после нажатия', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    mobile = true;
    await mountModal();
    expect(toggleIn(row('u-papa')!), 'local-пользователю раздела нет и на мобиле').toBeNull();
    const t = toggleIn(row('u-liza')!);
    expect(t, 'строка статуса в мобильной карточке').toBeTruthy();
    expect(t!.textContent).toContain('Детский режим…');
    await clickSettle(t!);
    const ed = editorIn(row('u-liza')!);
    expect(ed).toBeTruthy();
    expect(ed!.textContent).toContain('Лиза');
  });
});

describe('UserManagementModal: сохранение профиля (R-KM-F12, минор 2)', () => {
  it('после сохранения профиль обновлён только у своего пользователя; соседняя строка не тронута', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    await mountModal();
    // Свой путь: раскрыть → отметить режим и набрать провайдер → сохранить.
    const tLiza = toggleIn(row('u-liza')!)!;
    await click(tLiza);
    const ed = editorIn(row('u-liza')!)!;
    await click(enableToggle(ed));
    await clickTabIn(ed, 'Предметы');
    await typeInto(providerSelect(ed), 'anthropic-api-kids');
    await clickSettle(saveBtn(ed));
    expect(putCalls.map((c) => c.userId), 'запрос только по своему id').toEqual(['u-liza']);
    // Строка статуса своего пользователя отразила сохранённый профиль.
    expect(toggleIn(row('u-liza')!)!.textContent).toContain('Детский режим: включён');
    // Полный цикл свернуть/раскрыть: раскрытие снова грузит профиль — сервер отдаёт то, что
    // сохранил (mock повторяет PUT-ответ), редактор инициализирован сохранённым профилем.
    await click(toggleIn(row('u-liza')!)!);
    expect(editorIn(row('u-liza')!), 'свёрнут обратно').toBeNull();
    await clickSettle(toggleIn(row('u-liza')!)!);
    const ed2 = editorIn(row('u-liza')!)!;
    expect(enableToggle(ed2).getAttribute('aria-checked'), 'режим включён из сохранённого профиля').toBe('true');
    await clickTabIn(ed2, 'Предметы');
    expect(providerSelect(ed2).value).toBe('anthropic-api-kids');
    await clickTabIn(ed2, 'Режим и время');
    expect(ed2.querySelectorAll('[data-testid="time-window"]').length, 'окон не было').toBe(0);
    // Соседняя песочница: ни запроса, ни изменения — строка без утверждения, черновик пустой.
    const tMira = toggleIn(row('u-mira')!)!;
    expect(tMira.textContent).toContain('Детский режим…');
    await clickSettle(tMira);
    // «выключен» — только когда снимок пришёл: пустой профиль подтверждён сервером.
    expect(toggleIn(row('u-mira')!)!.textContent).toContain('Детский режим: выключен');
    const edMira = editorIn(row('u-mira')!)!;
    expect(enableToggle(edMira).getAttribute('aria-checked')).toBe('false');
    await clickTabIn(edMira, 'Предметы');
    expect(providerSelect(edMira).value).toBe('');
    expect(putCalls.length, 'чужого сохранения не было').toBe(1);
  });
});

describe('UserManagementModal: живой профиль грузится при раскрытии (Д1, дефект 06201244)', () => {
  it('раскрытие: getProfile своего id; редактор инициализирован живым профилем; строка — «включён»', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    resolveProfile('u-liza', lizaProfile);
    await mountModal();
    const r = row('u-liza')!;
    // До раскрытия — ни запроса, ни редактора; строка без утверждения: профиля мы не знаем.
    expect(getProfileCalls.length, 'свёрнутый раздел не грузится').toBe(0);
    expect(toggleIn(r)!.textContent).toContain('Детский режим…');
    await clickSettle(toggleIn(r)!);
    expect(getProfileCalls, 'раскрытие — запрос своего id').toEqual(['u-liza']);
    const ed = editorIn(r)!;
    expect(ed, 'редактор после ответа').toBeTruthy();
    expect(enableToggle(ed).getAttribute('aria-checked'), 'режим из живого профиля').toBe('true');
    expect(ed.querySelectorAll('[data-testid="time-window"]').length, 'живое окно дошло').toBe(1);
    await clickTabIn(ed, 'Предметы');
    expect(ed.textContent, 'предмет живого профиля виден').toContain('Математика');
    // Строка статуса — по загруженному профилю.
    expect(toggleIn(r)!.textContent).toContain('Детский режим: включён');
  });

  it('до ответа — «Загрузка…», редактора и сохранения нет, PUT не уходит; при отказе — Notice и «Повторить»', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    const d = deferred('u-liza');
    await mountModal();
    const r = row('u-liza')!;
    await click(toggleIn(r)!);
    expect(loadingIn(r), 'состояние загрузки').toBeTruthy();
    expect(editorIn(r), 'редактор до снимка не монтируется').toBeNull();
    expect(saveIn(r), 'сохранения до снимка нет').toBeNull();
    expect(putCalls.length, 'PUT не уходит').toBe(0);
    // Загрузка упала без серверного текста («нет связи» — сетевой сбой, не ответ контроллера):
    // Notice с заголовком и кнопкой, выдуманного тела нет; строка статуса при отказе — «не загрузилось».
    await act(async () => { d.reject(new Error('нет связи')); });
    expect(loadingIn(r)).toBeNull();
    const notice = errorIn(r);
    expect(notice, 'Notice отказа').toBeTruthy();
    expect(notice!.textContent).toContain('Профиль ребёнка не загрузился');
    expect(notice!.textContent, 'без серверного текста тела нет').not.toContain('Не удалось загрузить профиль');
    expect(toggleIn(r)!.textContent).toContain('Детский режим: не загрузилось');
    expect(editorIn(r), 'при отказе редактора нет').toBeNull();
    expect(saveIn(r), 'сохранения при отказе нет').toBeNull();
    expect(putCalls.length).toBe(0);
    // «Повторить» (prop action у Notice) — новый запрос того же id; сервер отвечает пустым
    // профилем — редактор чист, строка подтверждает «выключен» снимком.
    await clickSettle(retryIn(r)!);
    expect(getProfileCalls, 'повтор — второй запрос своего id').toEqual(['u-liza', 'u-liza']);
    expect(errorIn(r), 'после успешного повтора Notice снят').toBeNull();
    expect(toggleIn(r)!.textContent).toContain('Детский режим: выключен');
    const ed = editorIn(r)!;
    expect(ed).toBeTruthy();
    expect(enableToggle(ed).getAttribute('aria-checked')).toBe('false');
  });

  it('ответ прежнего пользователя не применяется: соседняя строка получает только свой профиль', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    const dLiza = deferred('u-liza');
    const dMira = deferred('u-mira');
    await mountModal();
    // Открыли Лизу (ответ висит) — закрыли. Открыли Миру (её ответ тоже висит).
    const rLiza = row('u-liza')!;
    const rMira = row('u-mira')!;
    await click(toggleIn(rLiza)!);
    await click(toggleIn(rLiza)!);
    await click(toggleIn(rMira)!);
    expect(loadingIn(rMira), 'Мира ждёт свой ответ').toBeTruthy();
    // Ответ Лизы приходит первым — в строку Миры он не попадает: та всё ещё ждёт.
    await act(async () => { dLiza.resolve(lizaProfile); });
    expect(loadingIn(rMira), 'чужой ответ не снял загрузку Миры').toBeTruthy();
    expect(editorIn(rMira), 'чужой профиль не открыл редактор Миры').toBeNull();
    // Мира в загрузке — строка без утверждения (и чужой «включён», и «выключен» ей не ставятся).
    expect(toggleIn(rMira)!.textContent, 'чужой профиль не красит строку Миры').toContain('Детский режим…');
    // Свой ответ Миры — пустой профиль: её редактор пуст, а не Лизин.
    await act(async () => { dMira.resolve({ profile: null, tutorPersonaId: '' }); });
    const edMira = editorIn(rMira)!;
    expect(edMira).toBeTruthy();
    expect(enableToggle(edMira).getAttribute('aria-checked'), 'чужой профиль не вошёл в черновик').toBe('false');
    expect(edMira.querySelectorAll('[data-testid="time-window"]').length).toBe(0);
    // Ответ Лизы остался при Лизе: её строка — «включён», её черновик — живой.
    // (Раскрытие — всегда запрос живого профиля; сервер для этого теста отвечает тем же профилем.)
    expect(toggleIn(rLiza)!.textContent).toContain('Детский режим: включён');
    resolveProfile('u-liza', lizaProfile);
    await clickSettle(toggleIn(rLiza)!);
    const edLiza = editorIn(rLiza)!;
    expect(enableToggle(edLiza).getAttribute('aria-checked'), 'свой профиль дошёл до своего редактора').toBe('true');
  });
});

describe('UserManagementModal: повторное раскрытие грузит профиль заново (хвост 69c3ee96)', () => {
  it('из «loaded»: свернуть → раскрыть — второй GET, редактор по новому снимку', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    resolveProfile('u-liza', lizaProfile);
    await mountModal();
    const r = row('u-liza')!;
    await clickSettle(toggleIn(r)!);
    expect(getProfileCalls.length).toBe(1);
    await click(toggleIn(r)!);
    expect(editorIn(r), 'свёрнут — редактора нет').toBeNull();
    // Снова раскрыть — запрос живого профиля, а не показ прежнего снимка.
    resolveProfile('u-liza', lizaProfile);
    await clickSettle(toggleIn(r)!);
    expect(getProfileCalls, 'повторное раскрытие — второй запрос').toEqual(['u-liza', 'u-liza']);
    expect(editorIn(r), 'редактор по новому снимку').toBeTruthy();
  });

  it('из «error»: свернуть → раскрыть — второй GET, Notice снят', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    const d = deferred('u-liza');
    await mountModal();
    const r = row('u-liza')!;
    await click(toggleIn(r)!);
    await act(async () => { d.reject(new Error('нет связи')); });
    expect(errorIn(r)).toBeTruthy();
    await click(toggleIn(r)!);
    expect(errorIn(r), 'в свёрнутом разделе Notice нет').toBeNull();
    resolveProfile('u-liza', { profile: null, tutorPersonaId: '' });
    await clickSettle(toggleIn(r)!);
    expect(getProfileCalls, 'раскрытие из error — второй запрос').toEqual(['u-liza', 'u-liza']);
    expect(errorIn(r), 'новая попытка — Notice снят').toBeNull();
  });

  it('первый запрос висящий: раскрыть → свернуть → раскрыть — второго запроса нет', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    // Ответ висит и остаётся висящим: весь тест — про состояние loading между раскрытиями.
    deferred('u-liza');
    await mountModal();
    const r = row('u-liza')!;
    await click(toggleIn(r)!);
    expect(getProfileCalls.length).toBe(1);
    await click(toggleIn(r)!);
    await click(toggleIn(r)!);
    expect(getProfileCalls.length, 'пока первый в пути — второго нет').toBe(1);
    expect(loadingIn(r), 'состояние ожидания сохранено').toBeTruthy();
  });

  it('серверный текст отказа — тело Notice; без серверного текста — только заголовок', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    const d = deferred('u-liza');
    await mountModal();
    const r = row('u-liza')!;
    await click(toggleIn(r)!);
    // Отказ с телом { code, message } — форма отказа UsersController.GetChildProfile.
    const err = Object.assign(new Error('Not Found'), { body: { code: 'user_not_found', message: 'Пользователь не найден.' } });
    await act(async () => { d.reject(err); });
    const notice = errorIn(r);
    expect(notice, 'Notice отказа').toBeTruthy();
    expect(notice!.textContent).toContain('Пользователь не найден.');
  });
});

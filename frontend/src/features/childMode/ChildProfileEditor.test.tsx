// KM-F11 + Т4f: редактор профиля ребёнка для админа — тело PUT child-profile (ADR-041, ручка №10),
// отказы 400 человеческим текстом и гонка смены пользователя. Т4f: вкладка «Предметы» — пикер
// «Помощника (правила)» вместо текстовых полей, список предметов и карточка его репетитора,
// subjects в теле, тьютор из ответа и красная строка названного отказом предмета.
// Окружение — node + linkedom, как в соседних .test.tsx волны: тело запроса проверяется по mock childApi,
// состояния — по атрибутам и inline-стилям. Черновик заполняется через user.childProfile (профиль даёт
// вызывающий), поэтому набирать текст в полях в тесте не нужно; переключения — кликами по контролам.
// «Необработанных rejection нет» не слушается отдельно: неохваченный rejection сам краснит прогон vitest.
// Мутации этого прогона (вывод — в отчёте задачи): снять сверку id в ответе getRoutes →
// «Subjects_RoutesResponseAfterUserSwitch_Ignored» краснеет; не резать tutorPersonaId из
// предмета в теле → «без полей ответа» краснеет.

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

// childApi.putProfile: ответ по индексу вызова. Нет записи — promise держится in-flight
// (на этом проверяются гонка ответа и блокировка кнопки); Error — отказ с status/body, как его
// выносит request() из lib/offline. getRoutes — тот же приём по индексу вызова (одна загрузка
// на пользователя): signal mock не читает, но фиксирует: сам вызов с сигналом — часть контракта.
let calls: { userId: string; body: unknown }[] = [];
let answers: (Error | object)[] = [];
let pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: unknown) => void }>();
let callIdx = 0;
let routeCalls: { signal?: AbortSignal }[] = [];
let routeAnswers: (Error | ChildRoute[])[] = [];
let routePending = new Map<number, { resolve: (v: ChildRoute[]) => void; reject: (e: unknown) => void }>();

// URL-хелперы — РЕАЛЬНЫЕ (тот же приём, что ChildTutorCard.test): карточка репетитора строит
// src настоящим хелпером, и проверяется именно эта строка.
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./api')>();
  return {
    ...actual,
    childApi: {
    putProfile: (userId: string, body: unknown) => {
      const i = callIdx++;
      calls.push({ userId, body });
      const a = answers[i];
      if (a instanceof Error) return Promise.reject(a);
      if (a) return Promise.resolve(a);
      return new Promise((res, rej) => { pending.set(i, { resolve: res, reject: rej }); });
    },
      getRoutes: (opts?: { signal?: AbortSignal }) => {
        const i = routeCalls.length;
        routeCalls.push({ signal: opts?.signal });
        const a = routeAnswers[i];
        if (a instanceof Error) return Promise.reject(a);
        if (a) return Promise.resolve(a);
        return new Promise((res, rej) => { routePending.set(i, { resolve: res, reject: rej }); });
      },
    },
  };
});

// Пикер голоса в карточке репетитора при монтировании зовёт api.tts.voices — мокаем:
// свой каталог голосов тесту не нужен, а реальный вызов ушёл бы за пределы mock-слоя.
vi.mock('../../lib/api', () => ({
  api: { tts: { voices: () => Promise.resolve({ voices: [], configured: true }) } },
}));

const { ChildProfileEditor } = await import('./ChildProfileEditor');
import type { ChildProfileEditorUser } from './ChildProfileEditor';
import type { ChildProfile, ChildRoute, ChildSubjectAdmin } from './types';

// Состав GET /api/admin/child-routes (тот же, что в тесте редактора предметов): пул и облачный —
// на этом проверяется, что пачка 1 отдаёт пикерам только пул.
const ROUTES: ChildRoute[] = [
  {
    providerKey: 'anthropic-api-kids', kind: 'pool', label: 'Anthropic · ключ «kids»',
    priced: true, requiresAck: false,
    models: [{ id: 'sonnet', label: 'Sonnet' }, { id: 'haiku', label: 'Haiku' }],
  },
  { providerKey: 'glm', kind: 'cloud', label: 'GLM (Zhipu)', priced: true, requiresAck: true, models: [{ id: 'glm-5', label: 'GLM-5' }] },
];

// Профиль с заполненными полями — то же, что даёт вызывающий: на нём проверяется тело PUT.
const fullProfile = (): ChildProfile => ({
  enabled: true,
  guardianUserIds: ['u-papa', 'u-mama'],
  providerKey: 'anthropic-api-kids',
  model: 'sonnet',
  spaces: { lessons: { windows: [{ from: '09:00', to: '20:00', weekdays: [1, 3, 5] }], dailyMinutes: 40 } },
  dailyBudgetUsd: 2,
  monthlyBudgetUsd: 40,
  warnMinutes: 5,
});

const liza = (over: Partial<ChildProfileEditorUser> = {}): ChildProfileEditorUser => ({
  id: 'u-liza',
  username: 'Лиза',
  childProfile: fullProfile(),
  tutorPersonaId: 'p-tutor-old',
  rulesAgreedAt: '2026-11-15T10:00:00Z',
  ...over,
});

const httpErr = (status: number, code?: string, message?: string) =>
  Object.assign(new Error('отказ'), { status, body: { ...(code ? { code } : {}), ...(message ? { message } : {}) } });

// Предмет профиля — тот же элемент, что поле subjects тела и его форма в ответе (Т2a).
const subject = (id: string, over: Partial<ChildSubjectAdmin> = {}): ChildSubjectAdmin => ({
  id, label: 'Математика', glyph: 'Мат', color: 'info',
  providerKey: 'anthropic-api-kids', model: 'sonnet', enabled: true,
  tutorPersonaId: 'p-tutor-math', ...over,
});
const withSubjects = (...subjects: ChildSubjectAdmin[]): ChildProfile => ({ ...fullProfile(), subjects });

// Тело на проводе: то, что уйдёт в JSON запроса (undefined-поля выбрасываются).
const wire = (i: number) => JSON.parse(JSON.stringify(calls[i].body)) as Record<string, unknown>;

let saved: unknown[] = [];

async function mount(user: ChildProfileEditorUser) {
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  const onSaved = (p: ChildProfile) => { saved.push(p); };
  await act(async () => { root.render(createElement(ChildProfileEditor, { user, onSaved })); });
  return {
    host: host as HTMLElement,
    // Тот же смонтированный экземпляр с другим пользователем: на этом проверяется сброс черновика
    // и отбрасывание ответа прежнего id, а не перемонтирование.
    async rerender(next: ChildProfileEditorUser) {
      await act(async () => { root.render(createElement(ChildProfileEditor, { user: next, onSaved })); });
    },
  };
}

const panel = (host: HTMLElement) => host.querySelector('[data-testid="child-profile-editor"]') as HTMLElement;
const saveBtn = (host: HTMLElement) => panel(host).querySelector('[data-testid="save-profile"]') as HTMLButtonElement;
const inputByPlaceholder = (host: HTMLElement, ph: string) =>
  panel(host).querySelector(`input[placeholder="${ph}"]`) as HTMLInputElement;
// Поле, значение которого всегда заполнено (placeholder не виден), ищется по подписи Field:
// обёртка Field — родитель label и содержит свой input.
const inputByLabel = (host: HTMLElement, label: string) => {
  const lbl = Array.from(panel(host).querySelectorAll('label')).find((l) => (l.textContent ?? '').includes(label));
  return (lbl?.parentElement?.querySelector('input') ?? null) as HTMLInputElement | null;
};
const windowInputs = (host: HTMLElement) =>
  Array.from(panel(host).querySelectorAll('[data-testid="time-window"] input')) as HTMLInputElement[];
const textOf = (host: HTMLElement, testid: string) =>
  (panel(host).querySelector(`[data-testid="${testid}"]`) as HTMLElement).textContent ?? '';
const toggle = (host: HTMLElement, label: string) =>
  panel(host).querySelector(`[role="checkbox"][aria-label="${label}"]`) as HTMLButtonElement;
const notice = (host: HTMLElement) => panel(host).querySelector('[role="status"]') as HTMLElement | null;

async function click(el: HTMLElement) {
  await act(async () => { el.click(); });
}

// Вкладка редактора: кнопка role="tab" с подписью. Клик переключает экран (onChange → setTab).
const tabBtn = (host: HTMLElement, label: string) =>
  Array.from(panel(host).querySelectorAll('[role="tab"]')).find((b) => (b.textContent ?? '').includes(label)) as HTMLElement | undefined;
const clickTab = async (host: HTMLElement, label: string) => {
  const b = tabBtn(host, label);
  if (!b) throw new Error(`вкладка «${label}» не найдена`);
  await click(b);
};
const tabSelected = (host: HTMLElement, label: string) =>
  (tabBtn(host, label)?.getAttribute('aria-selected') ?? 'false') === 'true';

// Набрать в поле ввода: React держит props узла под __reactProps$…; зовём onChange напрямую
// (тот же путь, что событие ввода), без событийной обвязки linkedom.
const reactProps = (el: HTMLElement): { onChange?: (e: { target: { value: string } }) => void } | undefined => {
  const key = Object.keys(el).find((k) => k.startsWith('__reactProps$'));
  return key ? (el as unknown as Record<string, unknown>)[key] as { onChange?: (e: { target: { value: string } }) => void } : undefined;
};
const typeInto = async (el: HTMLInputElement, v: string) => {
  await act(async () => { reactProps(el)?.onChange?.({ target: { value: v } }); });
};

// Ответ + выгнать цепочку then/catch обработчика (depth hops микротасков с запасом).
async function settle(fn: () => void) {
  await act(async () => {
    fn();
    for (let i = 0; i < 6; i++) await Promise.resolve();
  });
}

// Пикер «Помощника (правила)» — два Select: [поставщик, модель]. Пикер строки предмета — тот же
// тест внутри строки списка, он проверяется в ChildSubjectsEditor.test.tsx.
// Пикер помощника — единственный вне строк списка: ищем под его секцией, иначе тот же
// data-testid пикера строки предмета попался бы первым.
const assistantPicker = (host: HTMLElement) =>
  panel(host).querySelector('[data-testid="assistant-route"] [data-testid="child-route-picker"]') as HTMLElement | null;
const assistantSelects = (host: HTMLElement) =>
  Array.from(assistantPicker(host)?.querySelectorAll('select') ?? []) as HTMLSelectElement[];
const optionValues = (sel: HTMLSelectElement) => Array.from(sel.options).map((o) => o.value);
const pickAssistant = async (host: HTMLElement, which: 0 | 1, value: string) => {
  const sel = assistantSelects(host)[which];
  expect(sel, `пикер помощника: список №${which}`).not.toBeNull();
  await act(async () => { reactProps(sel)?.onChange?.({ target: { value } }); });
};
const subjectRows = (host: HTMLElement) => Array.from(panel(host).querySelectorAll('[data-testid="subject-row"]')) as HTMLElement[];
const subjectBtn = (host: HTMLElement, aria: string) => panel(host).querySelector(`[aria-label="${aria}"]`) as HTMLElement | null;
const subjectsEditor = (host: HTMLElement) => panel(host).querySelector('[data-testid="child-subjects-editor"]') as HTMLElement | null;
const tutorCard = (host: HTMLElement) => panel(host).querySelector('[data-testid="child-tutor-card"]') as HTMLElement | null;
// Поле строки списка — по подписи Field (тот же путь, что inputByLabel выше, только внутри строки).
const rowInput = (row: HTMLElement, label: string) => {
  const lbl = Array.from(row.querySelectorAll('label')).find((l) => (l.textContent ?? '').includes(label));
  return (lbl?.parentElement?.querySelector('input') ?? null) as HTMLInputElement | null;
};
const sentSubjects = (i: number) => (wire(i).subjects ?? []) as Record<string, unknown>[];

beforeEach(() => {
  calls = []; answers = []; pending = new Map(); callIdx = 0; saved = [];
  // Маршруты по умолчанию приходят сразу: на этом проверяется обычный экран. Без записи в
  // routeAnswers запрос висит — на этом проверяются загрузка и гонка.
  routeCalls = []; routeAnswers = [ROUTES]; routePending = new Map();
  mobile = false;
});

describe('ChildProfileEditor: тело PUT child-profile', () => {
  it('«Сохранить» шлёт putProfile(userId, ChildProfileDto) по ADR-041; resetRules не отмечен — поля нет', async () => {
    const { host } = await mount(liza());
    await click(saveBtn(host));
    expect(calls.map((c) => c.userId)).toEqual(['u-liza']);
    expect(wire(0)).toEqual({
      enabled: true,
      guardianUserIds: ['u-papa', 'u-mama'],
      providerKey: 'anthropic-api-kids',
      model: 'sonnet',
      spaces: { lessons: { windows: [{ from: '09:00', to: '20:00', weekdays: [1, 3, 5] }], dailyMinutes: 40 } },
      dailyBudgetUsd: 2,
      monthlyBudgetUsd: 40,
      warnMinutes: 5,
    });
    expect('resetRules' in wire(0)).toBe(false);
  });

  it('выключенный режим — тело { enabled: false }, как допускает ручка №10', async () => {
    const { host } = await mount(liza({
      childProfile: { enabled: false, guardianUserIds: [], providerKey: '', model: '', spaces: {} },
    }));
    await click(saveBtn(host));
    expect(wire(0)).toEqual({ enabled: false });
  });

  it('дней не выбрано — поля weekdays в теле нет; окно через полночь корректно; чужие пространства проходят как есть', async () => {
    const p = fullProfile();
    p.spaces = { lessons: { windows: [{ from: '22:00', to: '06:00' }] }, studio: { windows: [], dailyMinutes: null } };
    const { host } = await mount(liza({ childProfile: p }));
    await click(saveBtn(host));
    const body = wire(0);
    const spaces = body.spaces as Record<string, { windows: { weekdays?: number[] }[] }>;
    expect(spaces.lessons.windows).toEqual([{ from: '22:00', to: '06:00' }]);
    expect(spaces.lessons.windows[0].weekdays).toBeUndefined();
    expect(spaces.studio).toEqual({ windows: [], dailyMinutes: null });
  });

  it('профиля нет — пустой черновик с warnMinutes 5; пустые лимиты и бюджеты уходят null', async () => {
    const { host } = await mount({ id: 'u-new', username: 'Новый' });
    // Маршрут помощника на вкладке «Предметы»: переключаемся на неё, проверяем пустой черновик
    // (ничего не выбрано — placeholder), возвращаемся.
    await clickTab(host, 'Предметы');
    expect(assistantSelects(host)[0].value, 'маршрут не выбран').toBe('');
    await clickTab(host, 'Режим и время');
    const warn = Array.from(panel(host).querySelectorAll('input')).find((i) => i.value === '5');
    expect(warn, 'warnMinutes по умолчанию 5').toBeTruthy();
    await click(toggle(host, 'Детский режим'));
    await click(saveBtn(host));
    expect(wire(0)).toEqual({
      enabled: true,
      guardianUserIds: [],
      providerKey: '',
      model: '',
      spaces: { lessons: { windows: [], dailyMinutes: null } },
      dailyBudgetUsd: null,
      monthlyBudgetUsd: null,
      warnMinutes: 5,
    });
  });

  it('отметка «Сбросить согласие» уходит resetRules: true', async () => {
    const { host } = await mount(liza());
    await click(toggle(host, 'Сбросить согласие с правилами'));
    await click(saveBtn(host));
    expect(wire(0).resetRules).toBe(true);
  });
});

describe('ChildProfileEditor: время', () => {
  it('окно «25:00» — поле подсвечено и названо, запросов нет', async () => {
    const p = fullProfile();
    p.spaces = { lessons: { windows: [{ from: '25:00', to: '20:00' }] } };
    const { host } = await mount(liza({ childProfile: p }));
    const from = windowInputs(host)[0];
    expect(from.style.border, 'рамка поля C.dangerBorder').toContain('var(--c-danger-border)');
    // текст ошибки поля — в обороте Field (input → обёртка TextField → div Field)
    expect(from.parentElement?.parentElement?.textContent ?? '').toContain('Формат ЧЧ:ММ');
    await click(saveBtn(host));
    expect(calls.length, 'некорректное время — без запроса').toBe(0);
  });

  it('пустое окно после попытки сохранить тоже подсвечено', async () => {
    const { host } = await mount(liza());
    await click(panel(host).querySelector('[data-testid="add-window"]') as HTMLElement);
    const added = windowInputs(host);
    expect(added.length).toBe(4);
    await click(saveBtn(host));
    expect(calls.length).toBe(0);
    expect(added[2].style.border).toContain('var(--c-danger-border)');
  });
});

describe('ChildProfileEditor: «Минут в день»', () => {
  const minutesInput = (host: HTMLElement) => inputByPlaceholder(host, '30');
  const spacesOf = (i: number) => wire(i).spaces as Record<string, { dailyMinutes: unknown }>;

  for (const bad of ['3O', '0', '-5', '2000', '12.5']) {
    it(`«${bad}» — поле подсвечено и названо, запросов нет (R-KM-F11)`, async () => {
      const { host } = await mount(liza());
      await typeInto(minutesInput(host), bad);
      const el = minutesInput(host);
      expect(el.style.border, 'рамка поля C.dangerBorder').toContain('var(--c-danger-border)');
      // текст ошибки поля — в обороте Field (input → обёртка TextField → div Field)
      expect(el.parentElement?.parentElement?.textContent ?? '').toContain('Введи число минут, например 30');
      await click(saveBtn(host));
      expect(calls.length, 'некорректные минуты — без запроса').toBe(0);
    });
  }

  it('пустое — «лимита нет»: dailyMinutes null в теле, поле не подсвечено', async () => {
    const { host } = await mount(liza());
    await typeInto(minutesInput(host), '');
    expect(minutesInput(host).style.border).not.toContain('var(--c-danger-border)');
    await click(saveBtn(host));
    expect(spacesOf(0).lessons.dailyMinutes).toBeNull();
  });

  it('«30» — корректно: без подсветки, в теле 30', async () => {
    const { host } = await mount(liza());
    await typeInto(minutesInput(host), '30');
    expect(minutesInput(host).style.border).not.toContain('var(--c-danger-border)');
    await click(saveBtn(host));
    expect(spacesOf(0).lessons.dailyMinutes).toBe(30);
  });

  it('режим выключен: «9999» в «Минут в день» не подсвечено, сохранение проходит (R-KM-F11)', async () => {
    const { host } = await mount(liza());
    await click(toggle(host, 'Детский режим'));
    await typeInto(minutesInput(host), '9999');
    expect(minutesInput(host).style.border, 'при выключенном режиме поле не краснеет').not.toContain('var(--c-danger-border)');
    await click(saveBtn(host));
    expect(wire(0)).toEqual({ enabled: false });
  });
});

describe('ChildProfileEditor: «Предупредить за»', () => {
  const warnInput = (host: HTMLElement) => inputByLabel(host, 'Предупредить за') as HTMLInputElement;

  for (const bad of ['abc', '0', '99']) {
    it(`«${bad}» — поле подсвечено и названо, запросов нет (R-KM-F11)`, async () => {
      const { host } = await mount(liza());
      await typeInto(warnInput(host), bad);
      const el = warnInput(host);
      expect(el.style.border, 'рамка поля C.dangerBorder').toContain('var(--c-danger-border)');
      // текст ошибки поля — в обороте Field (input → обёртка TextField → div Field)
      expect(el.parentElement?.parentElement?.textContent ?? '').toContain('Введи число от 1 до 60, например 5');
      await click(saveBtn(host));
      expect(calls.length, 'некорректное предупреждение — без запроса').toBe(0);
    });
  }

  it('пустое — значение по умолчанию: без подсветки, в теле warnMinutes 5', async () => {
    const { host } = await mount(liza());
    await typeInto(warnInput(host), '');
    expect(warnInput(host).style.border).not.toContain('var(--c-danger-border)');
    await click(saveBtn(host));
    expect(wire(0).warnMinutes).toBe(5);
  });

  it('«15» — корректно: без подсветки, в теле 15', async () => {
    const { host } = await mount(liza());
    await typeInto(warnInput(host), '15');
    expect(warnInput(host).style.border).not.toContain('var(--c-danger-border)');
    await click(saveBtn(host));
    expect(wire(0).warnMinutes).toBe(15);
  });
});

describe('ChildProfileEditor: отказы ручки №10', () => {
  const REJECTS = [
    { code: 'not_container', text: 'только пользователям в песочнице' },
    { code: 'provider_not_api_key', text: 'среди записей с API-ключом' },
    { code: 'invalid_window', text: 'Окно времени задано неверно' },
    { code: 'invalid_space', text: 'Пространство задано неверно' },
    { code: 'guardian_not_found', text: 'Среди опекунов есть id' },
  ];
  for (const r of REJECTS) {
    it(`${r.code}: warning-плашка человеческим текстом без кода; черновик цел, кнопка снова доступна`, async () => {
      const { host } = await mount(liza());
      await click(saveBtn(host));
      await settle(() => pending.get(0)!.reject(httpErr(400, r.code, 'Серверный текст отказа')));
      const n = notice(host);
      expect(n).toBeTruthy();
      expect(n!.style.background, 'tone="warning"').toBe('var(--c-warning-bg)');
      expect(n!.textContent).toContain(r.text);
      expect(n!.textContent).toContain('Серверный текст отказа'); // message ответа — как есть
      expect(n!.textContent).not.toContain(r.code);
      // черновик сохранён: окно на «Режиме и времени», маршрут на «Предметах»
      expect(windowInputs(host).map((i) => i.value)).toEqual(['09:00', '20:00']);
      await clickTab(host, 'Предметы');
      expect(assistantSelects(host).map((s) => s.value)).toEqual(['anthropic-api-kids', 'sonnet']);
      expect(saveBtn(host).disabled).toBe(false);
    });
  }

  it('404 user_not_found: человеческий текст вместо кода', async () => {
    const { host } = await mount(liza());
    await click(saveBtn(host));
    await settle(() => pending.get(0)!.reject(httpErr(404, 'user_not_found', 'Пользователь не найден.')));
    const n = notice(host);
    expect(n).toBeTruthy();
    expect(n!.textContent).toContain('Пользователь не найден — обнови список');
    expect(n!.textContent).not.toContain('user_not_found');
  });

  it('403 без тела (гейт авторизации): «Нужны права администратора»', async () => {
    const { host } = await mount(liza());
    await click(saveBtn(host));
    await settle(() => pending.get(0)!.reject(httpErr(403)));
    const n = notice(host);
    expect(n).toBeTruthy();
    expect(n!.textContent).toContain('Нужны права администратора');
  });

  it('500 без кода: общая warning-плашка, черновик цел', async () => {
    const { host } = await mount(liza());
    await click(saveBtn(host));
    await settle(() => pending.get(0)!.reject(httpErr(500)));
    const n = notice(host);
    expect(n).toBeTruthy();
    expect(n!.style.background).toBe('var(--c-warning-bg)');
    expect(n!.textContent).toContain('Сохранить не удалось');
    expect(windowInputs(host).map((i) => i.value)).toEqual(['09:00', '20:00']);
  });

  it('успешный отказ следующего нажатия чистит плашку предыдущего', async () => {
    const { host } = await mount(liza());
    await click(saveBtn(host));
    await settle(() => pending.get(0)!.reject(httpErr(400, 'invalid_window')));
    expect(notice(host)).toBeTruthy();
    await click(saveBtn(host));
    expect(calls.length).toBe(2);
    await settle(() => pending.get(1)!.resolve({ profile: fullProfile(), tutorPersonaId: 'p-tutor-old' }));
    expect(notice(host)).toBeNull();
  });
});

describe('ChildProfileEditor: гонка и только-чтение', () => {
  it('save response for previous user is dropped', async () => {
    const { host, rerender } = await mount(liza());
    await click(saveBtn(host));
    expect(calls.map((c) => c.userId)).toEqual(['u-liza']);
    await rerender({ id: 'u-other', username: 'Другой' });
    // черновик прежнего пользователя сброшен вместе с его id: на «Предметах» своего ответа ещё
    // нет — экран загрузки, и чужой список не показан
    await clickTab(host, 'Предметы');
    expect(subjectsEditor(host)).toBeNull();
    await settle(() => routePending.get(1)!.resolve(ROUTES));
    expect(assistantSelects(host).map((s) => s.value), 'маршрут прежнего не перешёл').toEqual(['', '']);
    await clickTab(host, 'Режим и время');
    expect(textOf(host, 'tutor-state')).toContain('ещё нет');
    await settle(() => pending.get(0)!.resolve({ profile: fullProfile(), tutorPersonaId: 'p-tutor-late' }));
    expect(saved.length, 'onSaved для прежнего пользователя не зовётся').toBe(0);
    expect(textOf(host, 'tutor-state')).toContain('ещё нет'); // поздний ответ не пишет чужого тьютора
    expect(saveBtn(host).disabled, 'не завис в загрузке').toBe(false);
    await click(saveBtn(host));
    expect(calls.map((c) => c.userId)).toEqual(['u-liza', 'u-other']);
  });

  it('два нажатия «Сохранить»: один запрос, кнопка в загрузке', async () => {
    const { host } = await mount(liza());
    const btn = saveBtn(host);
    await click(btn);
    expect(btn.disabled).toBe(true);
    await click(btn);
    expect(calls.length).toBe(1);
    await settle(() => pending.get(0)!.resolve({ profile: fullProfile(), tutorPersonaId: 'p-tutor-old' }));
    expect(btn.disabled).toBe(false);
  });

  it('ответ сохранения берёт тьютора из ответа; без resetRules дата согласия цела', async () => {
    const { host } = await mount(liza());
    expect(textOf(host, 'rules-state')).toContain('15 ноября 2026');
    expect(textOf(host, 'tutor-state')).toContain('p-tutor-old');
    await click(saveBtn(host));
    await settle(() => pending.get(0)!.resolve({ profile: fullProfile(), tutorPersonaId: 'p-tutor-new' }));
    expect(textOf(host, 'tutor-state')).toContain('p-tutor-new');
    expect(textOf(host, 'rules-state')).toContain('15 ноября 2026');
    expect(saved.length).toBe(1);
  });

  it('resetRules этого сохранения гасит «Правила договорены»: «ещё нет»', async () => {
    const { host } = await mount(liza());
    await click(toggle(host, 'Сбросить согласие с правилами'));
    await click(saveBtn(host));
    await settle(() => pending.get(0)!.resolve({ profile: fullProfile(), tutorPersonaId: 'p-tutor-old' }));
    expect(textOf(host, 'rules-state')).toContain('ещё нет');
  });
});

describe('ChildProfileEditor: вкладки «Режим и время» / «Предметы»', () => {
  it('две вкладки; по умолчанию «Режим и время»; пикер и предметы только на «Предметах»', async () => {
    const { host } = await mount(liza());
    expect(panel(host).querySelectorAll('[role="tab"]').length, 'две вкладки').toBe(2);
    expect(tabSelected(host, 'Режим и время'), 'вкладка по умолчанию').toBe(true);
    expect(tabSelected(host, 'Предметы')).toBe(false);
    // на «Режиме и времени» — окно есть, пикера и списка предметов нет
    expect(windowInputs(host).map((i) => i.value)).toEqual(['09:00', '20:00']);
    expect(assistantPicker(host), 'пикера вне этой вкладки нет').toBeNull();
    expect(subjectsEditor(host), 'списка вне этой вкладки нет').toBeNull();
    // на «Предметах» — пикер с значением черновика, окна нет, список предметов есть
    await clickTab(host, 'Предметы');
    expect(tabSelected(host, 'Предметы')).toBe(true);
    expect(assistantSelects(host).map((s) => s.value)).toEqual(['anthropic-api-kids', 'sonnet']);
    expect(windowInputs(host).length, 'окно скрыто на другой вкладке').toBe(0);
    expect(subjectsEditor(host), 'список предметов').toBeTruthy();
    // пачка 1 (макет Д1): пикер знает только записи пула — облачный в списке не появляется,
    // и его подпись стоит у вкладки, а не у пикера
    expect(optionValues(assistantSelects(host)[0])).toEqual(['anthropic-api-kids']);
    expect(panel(host).textContent).toContain('Только записи пула с API-ключом');
    expect(panel(host).textContent).not.toContain('GLM');
  });

  it('переключение вкладки не теряет выбор; «Сохранить» одна на обе вкладки, тело — с новым маршрутом', async () => {
    const { host } = await mount(liza());
    await clickTab(host, 'Предметы');
    await pickAssistant(host, 1, 'haiku');
    await clickTab(host, 'Режим и время');
    await clickTab(host, 'Предметы');
    expect(assistantSelects(host).map((s) => s.value), 'выбор живёт на обеих вкладках').toEqual(['anthropic-api-kids', 'haiku']);
    // сохранение с «Предметов»: тело строится из общего черновика — прежний набор + новая модель
    await click(saveBtn(host));
    expect(wire(0)).toEqual({
      enabled: true,
      guardianUserIds: ['u-papa', 'u-mama'],
      providerKey: 'anthropic-api-kids',
      model: 'haiku',
      spaces: { lessons: { windows: [{ from: '09:00', to: '20:00', weekdays: [1, 3, 5] }], dailyMinutes: 40 } },
      dailyBudgetUsd: 2,
      monthlyBudgetUsd: 40,
      warnMinutes: 5,
    });
  });

  it('«Сохранить» с «Предметов» при невалидных полях на «Режиме и времени»: возврат на ту вкладку, поля красные, запросов нет', async () => {
    const { host } = await mount(liza());
    // Пустое окно на «Режиме и времени» — невалидные поля, пока ещё не подсвечены (attempted нет).
    await click(panel(host).querySelector('[data-testid="add-window"]') as HTMLElement);
    await clickTab(host, 'Предметы');
    await click(saveBtn(host));
    expect(calls.length, 'невалидные поля — без запроса').toBe(0);
    // Человек не остаётся на вкладке без полей: активная снова «Режим и время».
    expect(tabSelected(host, 'Режим и время'), 'активная вкладка снова «Режим и время»').toBe(true);
    expect(tabSelected(host, 'Предметы')).toBe(false);
    const inputs = windowInputs(host);
    expect(inputs[2].style.border, 'пустое «С» подсвечено').toContain('var(--c-danger-border)');
    expect(inputs[3].style.border, 'пустое «По» подсвечено').toContain('var(--c-danger-border)');
  });
});

describe('ChildProfileEditor: «Предметы» — тело, тьютор и отказ по предмету', () => {
  it('subjects уходят в теле в порядке редактора и без полей ответа; привязка знаний — как есть', async () => {
    const b = subject('literatura', {
      label: 'Литература', glyph: 'Лит', color: 'rose', tutorPersonaId: null,
      knowledge: { datasetId: 'kb-1', attachedAt: '2026-11-01T00:00:00Z' },
      acknowledgeRouteRisk: true, routeAcknowledgedAt: '2026-11-02T00:00:00Z',
    });
    const { host } = await mount(liza({ childProfile: withSubjects(subject('math'), b) }));
    await click(saveBtn(host));
    expect(sentSubjects(0)).toEqual([
      { id: 'math', label: 'Математика', glyph: 'Мат', color: 'info', providerKey: 'anthropic-api-kids', model: 'sonnet', enabled: true },
      { id: 'literatura', label: 'Литература', glyph: 'Лит', color: 'rose', providerKey: 'anthropic-api-kids', model: 'sonnet', enabled: true, knowledge: { datasetId: 'kb-1', attachedAt: '2026-11-01T00:00:00Z' } },
    ]);
    // ни одного поля ответа в теле: их нет ни у первой, ни у второй строки
    for (const s of sentSubjects(0)) {
      expect('tutorPersonaId' in s, 'тьютор — поле ответа').toBe(false);
      expect('routeAcknowledgedAt' in s).toBe(false);
      expect('acknowledgeRouteRisk' in s, 'согласие обратно как есть не отправляем').toBe(false);
    }
  });

  it('порядок строк — порядок тела: «Ниже» меняет местами и в списке, и в запросе', async () => {
    const { host } = await mount(liza({ childProfile: withSubjects(subject('math'), subject('istoriya', { label: 'История', glyph: 'Ист', color: 'accent' })) }));
    await clickTab(host, 'Предметы');
    const move = subjectBtn(host, 'Ниже: Математика');
    expect(move, 'стрелка порядка').toBeTruthy();
    await click(move!);
    await click(saveBtn(host));
    expect(sentSubjects(0).map((s) => s.id)).toEqual(['istoriya', 'math']);
  });

  it('предметов не знаем — поля в теле нет: «Сохранить» не трогает чужие предметы', async () => {
    const { host } = await mount(liza()); // fullProfile без subjects: вызывающий о них не знает
    await clickTab(host, 'Предметы');
    expect(subjectsEditor(host), 'пустой список показан честно').toBeTruthy();
    await click(saveBtn(host));
    expect('subjects' in wire(0), 'нет данных — нет и поля').toBe(false);
  });

  it('400 по предмету: Notice с текстом ответа, красная строка названного id, переход на «Предметы»', async () => {
    const { host } = await mount(liza({ childProfile: withSubjects(subject('math'), subject('istoriya', { label: 'История', glyph: 'Ист', color: 'accent' })) }));
    await click(saveBtn(host));
    await settle(() => pending.get(0)!.reject(httpErr(400, 'subject_color_invalid', 'Цвет предмета «istoriya» вне палитры: допустимы info, rose, success, warning, accent.')));
    const n = notice(host);
    expect(n).toBeTruthy();
    expect(n!.textContent).toContain('Такого цвета нет в палитре');
    expect(n!.textContent).toContain('Цвет предмета «istoriya» вне палитры'); // message — как есть
    expect(n!.textContent).not.toContain('subject_color_invalid');
    expect(tabSelected(host, 'Предметы'), 'человек видит красную строку, а не плашку над другой вкладкой').toBe(true);
    const rows = subjectRows(host);
    expect(rows[1].getAttribute('data-error'), 'строка названного предмета').toBe('true');
    expect(rows[1].style.border).toContain('var(--c-danger)');
    expect(rows[0].getAttribute('data-error'), 'остальные строки не тронуты').toBeNull();
  });

  it('subject_id_duplicate: id в тексте не назван — краснеет повтор в списке', async () => {
    const { host } = await mount(liza({ childProfile: withSubjects(subject('math'), subject('math', { label: 'Алгебра', glyph: 'Ал', color: 'rose' })) }));
    await click(saveBtn(host));
    await settle(() => pending.get(0)!.reject(httpErr(400, 'subject_id_duplicate', 'Два предмета с одним ключом. Обнови страницу и попробуй ещё раз.')));
    expect(tabSelected(host, 'Предметы')).toBe(true);
    expect(subjectRows(host)[1].getAttribute('data-error'), 'повтор — вторая строка').toBe('true');
  });

  it('отказ, не называющий предмета, красной строки не ставит', async () => {
    const { host } = await mount(liza({ childProfile: withSubjects(subject('math')) }));
    await click(saveBtn(host));
    await settle(() => pending.get(0)!.reject(httpErr(400, 'not_container', 'Пользователь не в песочнице.')));
    expect(tabSelected(host, 'Режим и время'), 'вкладка не прыгает').toBe(true);
    expect(subjectRows(host).some((r) => r.getAttribute('data-error') === 'true')).toBe(false);
  });

  it('нового предмета репетитор — из ответа сохранения: карточка открыта по его id', async () => {
    const { host } = await mount(liza({ childProfile: withSubjects(subject('math')) }));
    await clickTab(host, 'Предметы');
    await click(panel(host).querySelector('[data-testid="add-subject"]') as HTMLElement);
    const draft = subjectRows(host)[1];
    const nameInput = rowInput(draft, 'Название');
    expect(nameInput, 'поле «Название» новой строки').toBeTruthy();
    await typeInto(nameInput!, 'Астрономия');
    const open = subjectBtn(host, 'Открыть: Астрономия');
    expect(open, 'клик по строке — карточка репетитора').toBeTruthy();
    await click(open!);
    expect(subjectRows(host)[1].style.border, 'строка открытого предмета отмечена').toContain('var(--c-accent)');
    expect(tutorCard(host)?.textContent).toContain('Репетитор появится после сохранения');
    await click(saveBtn(host));
    expect(sentSubjects(0).map((s) => s.id)).toEqual(['math', 'astronomiya']);
    await settle(() => pending.get(0)!.resolve({
      profile: withSubjects(subject('math'), subject('astronomiya', { label: 'Астрономия', glyph: 'Аст', tutorPersonaId: 'p-tutor-new' })),
      tutorPersonaId: 'p-tutor-old',
    }));
    const card = tutorCard(host);
    expect(card, 'карточка открыта').toBeTruthy();
    expect(card!.textContent).not.toContain('Репетитор появится после сохранения');
    expect(card!.querySelector('img')?.getAttribute('src') ?? '', 'тьютор взят из ответа, не из профиля').toContain('/users/u-liza/child-tutors/p-tutor-new/avatar');
  });

  it('пока идёт сохранение, пикер и список гасятся; второе нажатие — того же запроса', async () => {
    const { host } = await mount(liza({ childProfile: withSubjects(subject('math')) }));
    await clickTab(host, 'Предметы');
    await click(saveBtn(host));
    expect((panel(host).querySelector('[data-testid="add-subject"]') as HTMLElement).getAttribute('disabled'), 'кнопка списка гасится').not.toBeNull();
    expect(subjectBtn(host, 'Удалить предмет «Математика»')!.getAttribute('disabled')).not.toBeNull();
    expect(assistantSelects(host)[0].getAttribute('disabled'), 'пикер гасится').not.toBeNull();
    await click(saveBtn(host));
    expect(calls.length).toBe(1);
  });

  it('id длинного названия — в потолке валидатора (32): тело принимает его, хвост срезан', async () => {
    const { host } = await mount(liza({ childProfile: withSubjects(subject('math')) }));
    await clickTab(host, 'Предметы');
    await click(panel(host).querySelector('[data-testid="add-subject"]') as HTMLElement);
    const nameInput = rowInput(subjectRows(host)[1], 'Название');
    expect(nameInput).toBeTruthy();
    await typeInto(nameInput!, 'Очень длинное название предмета для проверки потолка');
    await click(saveBtn(host));
    const id = String(sentSubjects(0)[1].id);
    expect(id.length, 'id не длиннее потолка ^[a-z0-9-]{1,32}$').toBeLessThanOrEqual(32);
    expect(id).toMatch(/^[a-z0-9-]{1,32}$/);
  });
});

describe('ChildProfileEditor: «Предметы» — загрузка и гонка списка маршрутов', () => {
  it('пока ответа нет — экран загрузки; свой ответ открыл пикеры и список', async () => {
    routeAnswers = [];
    const { host } = await mount(liza({ childProfile: withSubjects(subject('math')) }));
    await clickTab(host, 'Предметы');
    expect(panel(host).querySelector('[data-testid="routes-loading"]'), 'экран загрузки').toBeTruthy();
    expect(assistantPicker(host), 'пикера ещё нет').toBeNull();
    expect(subjectsEditor(host), 'списка ещё нет').toBeNull();
    await settle(() => routePending.get(0)!.resolve(ROUTES));
    expect(panel(host).querySelector('[data-testid="routes-loading"]')).toBeNull();
    expect(assistantSelects(host).map((s) => s.value)).toEqual(['anthropic-api-kids', 'sonnet']);
    expect(subjectsEditor(host)).toBeTruthy();
  });

  it('Subjects_RoutesResponseAfterUserSwitch_Ignored: ответ маршрутов прежнего пользователя не применяется', async () => {
    routeAnswers = [];
    const { host, rerender } = await mount(liza({ childProfile: withSubjects(subject('math')) }));
    await clickTab(host, 'Предметы');
    expect(panel(host).querySelector('[data-testid="routes-loading"]')).toBeTruthy();
    await rerender({ id: 'u-other', username: 'Другой' });
    expect(subjectsEditor(host), 'чужой список не показан').toBeNull();
    await settle(() => routePending.get(0)!.resolve(ROUTES));
    expect(subjectsEditor(host), 'поздний ответ прежнего id не открыл пикеры').toBeNull();
    expect(assistantPicker(host)).toBeNull();
    expect(routeCalls.length, 'нового пользователя ждёт его собственный запрос').toBe(2);
    expect(routeCalls[0].signal, 'загрузка отменяема').toBeInstanceOf(AbortSignal);
    await settle(() => routePending.get(1)!.resolve(ROUTES));
    expect(subjectsEditor(host), 'свой ответ применён').toBeTruthy();
  });

  it('отказ списка: плашка вместо пикера, предметы править и сохранить можно', async () => {
    routeAnswers = [httpErr(500)];
    const { host } = await mount(liza({ childProfile: withSubjects(subject('math')) }));
    await clickTab(host, 'Предметы');
    const n = panel(host).querySelector('[data-testid="routes-failed"]') as HTMLElement;
    expect(n, 'плашка отказа загрузки').toBeTruthy();
    expect(n.textContent).toContain('Список моделей не загрузился');
    expect(assistantPicker(host), 'пикера нет').toBeNull();
    expect(subjectsEditor(host), 'свои данные не заблокированы').toBeTruthy();
    await click(saveBtn(host));
    expect(sentSubjects(0).map((s) => s.id)).toEqual(['math']);
  });
});

describe('ChildProfileEditor: мобила (360)', () => {
  it('вкладки: тач-цель ≥ 40px', async () => {
    mobile = true;
    const { host } = await mount(liza());
    const tabs = Array.from(panel(host).querySelectorAll('[role="tab"]')) as HTMLElement[];
    expect(tabs.length).toBe(2);
    for (const t of tabs) {
      expect(Number.parseFloat(t.style.height), 'тач-цель вкладки (Tabs size=md, mobile=44)').toBeGreaterThanOrEqual(40);
    }
  });

  it('поля окна в одну колонку, тач-цели ≥ 40px', async () => {
    mobile = true;
    const { host } = await mount(liza());
    const row = panel(host).querySelector('[data-testid="window-row"]') as HTMLElement;
    expect(row.style.flexDirection).toBe('column');
    const btn = saveBtn(host);
    expect(Number.parseFloat(btn.style.minHeight), 'тач-цель «Сохранить»').toBeGreaterThanOrEqual(40);
    const chip = panel(host).querySelector('[aria-pressed]') as HTMLElement;
    expect(Number.parseFloat(chip.style.minHeight), 'тач-цель дня недели').toBeGreaterThanOrEqual(40);
    const box = toggle(host, 'Детский режим');
    expect(Number.parseFloat(box.style.width), 'тач-цель тумблера').toBeGreaterThanOrEqual(40);
  });

  it('«Предметы»: пикер столбиком, стрелки порядка — тач-цели ≥ 40px', async () => {
    mobile = true;
    const { host } = await mount(liza({ childProfile: withSubjects(subject('math'), subject('istoriya', { label: 'История', glyph: 'Ист', color: 'accent' })) }));
    await clickTab(host, 'Предметы');
    const pick = assistantPicker(host);
    expect(pick).toBeTruthy();
    expect((pick!.firstElementChild as HTMLElement).style.flexDirection, 'поставщик и модель — столбиком').toBe('column');
    const up = subjectBtn(host, 'Выше: История');
    expect(up, 'стрелка порядка в карточке строки').toBeTruthy();
    expect(Number.parseFloat(up!.style.minHeight), 'тач-цель «Выше»').toBeGreaterThanOrEqual(40);
    const open = subjectBtn(host, 'Открыть: Математика');
    expect(Number.parseFloat(open!.style.minHeight), 'тач-цель строки предмета').toBeGreaterThanOrEqual(40);
  });
});

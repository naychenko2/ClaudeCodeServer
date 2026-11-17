// KM-F11: редактор профиля ребёнка для админа — тело PUT child-profile (ADR-041, ручка №10),
// отказы 400 человеческим текстом и гонка смены пользователя.
// Окружение — node + linkedom, как в соседних .test.tsx волны: тело запроса проверяется по mock childApi,
// состояния — по атрибутам и inline-стилям. Черновик заполняется через user.childProfile (профиль даёт
// вызывающий), поэтому набирать текст в полях в тесте не нужно; переключения — кликами по контролам.
// «Необработанных rejection нет» не слушается отдельно: неохваченный rejection сам краснит прогон vitest.

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
// выносит request() из lib/offline.
let calls: { userId: string; body: unknown }[] = [];
let answers: (Error | object)[] = [];
let pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: unknown) => void }>();
let callIdx = 0;

vi.mock('./api', () => ({
  childApi: {
    putProfile: (userId: string, body: unknown) => {
      const i = callIdx++;
      calls.push({ userId, body });
      const a = answers[i];
      if (a instanceof Error) return Promise.reject(a);
      if (a) return Promise.resolve(a);
      return new Promise((res, rej) => { pending.set(i, { resolve: res, reject: rej }); });
    },
  },
}));

const { ChildProfileEditor } = await import('./ChildProfileEditor');
import type { ChildProfileEditorUser } from './ChildProfileEditor';
import type { ChildProfile } from './types';

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

beforeEach(() => {
  calls = []; answers = []; pending = new Map(); callIdx = 0; saved = [];
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
    expect(inputByPlaceholder(host, 'anthropic-api-kids').value).toBe('');
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
      // черновик сохранён: поля и окно прежние
      expect(windowInputs(host).map((i) => i.value)).toEqual(['09:00', '20:00']);
      expect(inputByPlaceholder(host, 'anthropic-api-kids').value).toBe('anthropic-api-kids');
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
    // черновик прежнего пользователя сброшен вместе с его id
    expect(inputByPlaceholder(host, 'anthropic-api-kids').value).toBe('');
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

describe('ChildProfileEditor: мобила (360)', () => {
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
});

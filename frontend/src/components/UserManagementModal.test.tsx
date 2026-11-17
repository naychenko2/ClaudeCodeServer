// KM-F12 + доработка по ревью R-KM-F12: раздел «Детский режим» в «Пользователях» — только
// пользователям песочницы (минор 3, решение Софьи), свёрнут по умолчанию в строку статуса,
// раскрытие по нажатию. Тесты: граница container/local, раздел без флага на мобиле (минор 1),
// после сохранения профиль обновляется только у своего пользователя (минор 2).
// Окружение — node + linkedom, как в соседних .test.tsx волны. Modal — портал в document.body:
// createPortal подменён pass-through (паттерн Modal.test.ts); список пользователей — фикстура
// через mock api.users.list; PUT child-profile — mock childApi, ответ повторяет тело запроса.

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
// для теста сохранения. Диалоговые ручки в этом тесте не вызываются.
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
// как реальное сохранение (сервер возвращает то, что сохранил).
let putCalls: { userId: string; body: unknown }[] = [];
vi.mock('../features/childMode/api', () => ({
  childApi: {
    putProfile: (userId: string, body: unknown) => {
      putCalls.push({ userId, body });
      return Promise.resolve({ profile: body, tutorPersonaId: 'p-tutor-new' });
    },
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
const enableToggle = (ed: HTMLElement) => ed.querySelector('[role="checkbox"][aria-label="Детский режим"]') as HTMLButtonElement;
const saveBtn = (ed: HTMLElement) => ed.querySelector('[data-testid="save-profile"]') as HTMLButtonElement;
const providerInput = (ed: HTMLElement) => ed.querySelector('input[placeholder="anthropic-api-kids"]') as HTMLInputElement;

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
const typeInto = async (el: HTMLInputElement, v: string) => {
  await act(async () => { reactProps(el)?.onChange?.({ target: { value: v } }); });
};

beforeEach(() => {
  // между тестами дерево чистят: editors()/toggles() ищут по всему документу
  doc.getElementById('root')!.innerHTML = '';
  setAllFlags({});
  mobile = false;
  putCalls = [];
});

describe('UserManagementModal: раздел «Детский режим» только у песочницы, свёрнут по умолчанию', () => {
  it('флаг включён: раздела нет у local-пользователя; у container — строка статуса, редактор после нажатия', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    await mountModal();
    // Админ на сервере: ни строки статуса, ни редактора (ср. not_container на бэке).
    const papa = row('u-papa')!;
    expect(papa, 'строка админа на месте').toBeTruthy();
    expect(toggleIn(papa), 'local-пользователю раздела нет').toBeNull();
    expect(editorIn(papa)).toBeNull();
    // Песочница: свёрнутая строка со статусом, редактора до нажатия нет.
    for (const id of ['u-liza', 'u-mira']) {
      const r = row(id)!;
      const t = toggleIn(r);
      expect(t, `строка статуса ${id}`).toBeTruthy();
      expect(t!.textContent).toContain('Детский режим: выключен');
      expect(t!.getAttribute('aria-expanded'), 'по умолчанию свёрнут').toBe('false');
      expect(editorIn(r), 'редактор до нажатия не смонтирован').toBeNull();
      // «после основных полей»: раздел — последний блок строки
      const last = r!.children[r!.children.length - 1];
      expect(last.querySelector('[data-testid="child-section-toggle"]'), 'раздел после основных полей').toBeTruthy();
    }
    // Нажатие — редактор внутри строки этого пользователя; соседняя строка не раскрылась.
    await click(toggleIn(row('u-liza')!)!);
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
    expect(t!.textContent).toContain('Детский режим: выключен');
    await click(t!);
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
    await typeInto(providerInput(ed), 'anthropic-api-kids');
    await clickSettle(saveBtn(ed));
    expect(putCalls.map((c) => c.userId), 'запрос только по своему id').toEqual(['u-liza']);
    // Строка статуса своего пользователя отразила сохранённый профиль.
    expect(toggleIn(row('u-liza')!)!.textContent).toContain('Детский режим: включён');
    // Полный цикл свернуть/раскрыть: редактор инициализирован сохранённым профилем.
    await click(toggleIn(row('u-liza')!)!);
    expect(editorIn(row('u-liza')!), 'свёрнут обратно').toBeNull();
    await click(toggleIn(row('u-liza')!)!);
    const ed2 = editorIn(row('u-liza')!)!;
    expect(enableToggle(ed2).getAttribute('aria-checked'), 'режим включён из сохранённого профиля').toBe('true');
    expect(providerInput(ed2).value).toBe('anthropic-api-kids');
    expect(ed2.querySelectorAll('[data-testid="time-window"]').length, 'окон не было').toBe(0);
    // Соседняя песочница: ни запроса, ни изменения — статус и черновик пустые.
    const tMira = toggleIn(row('u-mira')!)!;
    expect(tMira.textContent).toContain('Детский режим: выключен');
    await click(tMira);
    const edMira = editorIn(row('u-mira')!)!;
    expect(enableToggle(edMira).getAttribute('aria-checked')).toBe('false');
    expect(providerInput(edMira).value).toBe('');
    expect(putCalls.length, 'чужого сохранения не было').toBe(1);
  });
});

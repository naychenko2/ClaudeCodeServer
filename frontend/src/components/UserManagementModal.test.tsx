// KM-F12: раздел «Детский режим» в «Пользователях» — ChildProfileEditor под флагом child-mode,
// после основных полей строки пользователя, по его user.id (ADR-041: флаг прячет только этот
// интерфейс включения; ограничения ребёнка от флага не зависят).
// Окружение — node + linkedom, как в соседних .test.tsx волны. Modal — портал в document.body:
// createPortal подменён pass-through (паттерн Modal.test.ts), список пользователей — фикстура
// через mock api.users.list (своих запросов у встройки нет — запросы в самом редакторе).

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

// Фикстура списка: админ на сервере и ребёнок в песочнице — ровно те два состояния строки,
// которые видят бейджи. Диалоговые ручки в этом тесте не вызываются.
const { usersFixture } = vi.hoisted(() => ({
  usersFixture: [
    { id: 'u-papa', username: 'Папа', role: 'admin', createdAt: '2026-01-01T00:00:00Z', executionEnvironment: 'local' },
    { id: 'u-liza', username: 'Лиза', role: 'user', createdAt: '2026-02-01T00:00:00Z', executionEnvironment: 'container' },
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
const row = (id: string) => doc.querySelector(`[data-testid="user-row-${id}"]`) as HTMLElement | null;

beforeEach(() => {
  // между тестами дерево чистят: editors() ищёт по всему документу
  doc.getElementById('root')!.innerHTML = '';
  setAllFlags({});
  mobile = false;
});

describe('UserManagementModal: раздел «Детский режим» под флагом child-mode', () => {
  it('флаг включён: у каждого пользователя списка есть раздел, по его id и после основных полей', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    await mountModal();
    expect(editors().length, 'по одному разделу на пользователя').toBe(usersFixture.length);
    for (const u of usersFixture) {
      const r = row(u.id);
      expect(r, `строка пользователя ${u.id}`).toBeTruthy();
      const ed = r!.querySelector('[data-testid="child-profile-editor"]') as HTMLElement | null;
      expect(ed, `раздел внутри строки ${u.id}`).toBeTruthy();
      expect(ed!.textContent, 'редактор привязан к этому пользователю').toContain(u.username);
      // «после основных полей»: раздел — последний блок строки, после имени и кнопок действий
      const last = r!.children[r!.children.length - 1];
      expect(last === ed || last.querySelector('[data-testid="child-profile-editor"]'), 'раздел после основных полей').toBeTruthy();
    }
  });

  it('флаг выключен: 0 узлов раздела; сами строки пользователей на месте', async () => {
    await mountModal();
    expect(editors().length).toBe(0);
    expect(row('u-liza'), 'строка без флага остаётся').toBeTruthy();
    expect(row('u-liza')!.textContent).toContain('Лиза');
  });

  it('мобила (360): раздел внутри модалки, по одному на пользователя', async () => {
    setAllFlags({ [FLAGS.childMode]: true });
    mobile = true;
    await mountModal();
    expect(editors().length).toBe(usersFixture.length);
    const ed = row('u-liza')!.querySelector('[data-testid="child-profile-editor"]') as HTMLElement | null;
    expect(ed).toBeTruthy();
    expect(ed!.textContent).toContain('Лиза');
  });
});

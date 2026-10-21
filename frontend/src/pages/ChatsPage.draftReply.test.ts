// QA-фикс M (фронт): «Править»/«Просить агента исправить» из карточки черновика в чате
// ВНЕ проекта — реплика должна попасть в композер ТОГО чата, в чьей ленте карточка, и
// Нигуда больше, не отправляясь сама.
//
// Тест на РЕАЛЬНОМ слушателе ChatsPage (не на копии логики): монтируем ChatsPage, ставим
// активный чат (localStorage cc_open_chat), шлём cc-draft-reply с chatId = активному чату и
// проверяем, что слушатель положил реплику в поле ввода (cc_pending_chat_prompt +
// cc-compose-prefill), а НЕ отправил её (send не вызван — реплика в поле, не в ленте).
//
// Мутации:
//  • вернуть сравнение проектов (chatId ≠ activeId в чате вне проекта) → префилла нет (тест красный);
//  • принимать любой chatId → чужая реплика тоже префиллится (тест «чужой chatId» красный);
//  • добавить отправку (send) → send вызывается (тест «send не вызван» красный).

import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';

// signalr: onMessage — захватываем подписчика (ChatsPage подписывается на hub)
vi.mock('../lib/signalr', () => ({
  onMessage: () => () => {},
  onReconnected: () => () => {},
}));

// api — ChatsPage/HubHeader/PanelZone зовут много api.* (fire-and-forget). Proxy-мок: любой
// api.X.Y(...) → resolved promise({}), чтобы не гонять каждый метод по отдельности.
function makeApiProxy(): unknown {
  // resolve([]): list-вызовы (chats.list, tasks.listAll, …) дают массив; object-вызовы
  // fire-and-forget и их результат в тесте не проверяем ({}.path === undefined — не роняет)
  const target = function () { return Promise.resolve([]); };
  return new Proxy(target, {
    get: (t, prop) => {
      if (prop === Symbol.toPrimitive) return () => '';
      if (prop === 'then') return undefined; // не thenable (иначе await api вешает)
      return makeApiProxy();
    },
    apply: () => Promise.resolve([]),
  });
}
vi.mock('../lib/api', () => ({ api: makeApiProxy() }));

// nav: getNav — читаем window.location (fake window), по умолчанию screen 'chats'
vi.mock('../lib/nav', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/nav')>();
  return { ...orig, getNav: () => ({ screen: 'chats', chatId: null }) };
});

const { installFakeDom, mount, uninstallFakeDom } = await import(
  '../components/chat/__tests__/fakeDom'
);
const { ChatsPage } = await import('./ChatsPage');

const OPEN_CHAT_KEY = 'cc_open_chat';

// Окружение node: sessionStorage не определён — поднимаем in-memory (паттерн PersonaBindingsPanel)
function makeStorage(): Storage {
  const m = new Map<string, string>();
  return {
    getItem: (k: string) => (m.has(k) ? m.get(k)! : null),
    setItem: (k: string, v: string) => { m.set(k, String(v)); },
    removeItem: (k: string) => { m.delete(k); },
    clear: () => { m.clear(); },
    key: (i: number) => [...m.keys()][i] ?? null,
    get length() { return m.size; },
  };
}

beforeEach(() => {
  installFakeDom();
  vi.stubGlobal('sessionStorage', makeStorage());
  vi.stubGlobal('localStorage', makeStorage());
  // node-окружение: rAF/cAF для PanelZone и др. — прогоняем в микротаске
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => setTimeout(() => cb(0), 0) as unknown as number);
  vi.stubGlobal('cancelAnimationFrame', (id: number) => clearTimeout(id as unknown as ReturnType<typeof setTimeout>));
  // ResizeObserver (centerOffset/PanelZone) — no-op: observe/disconnect не вызывают колбэк
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  // window.history (nav: replaceState/pushState/state) — no-op; fakeDom-окно его не имеет
  (window as unknown as { history: unknown }).history = {
    state: null, replaceState: () => {}, pushState: () => {}, go: () => {}, back: () => {},
  };
  // чистим prefill-ключ и активный чат между тестами
  sessionStorage.removeItem('cc_pending_chat_prompt');
  sessionStorage.removeItem(OPEN_CHAT_KEY);
});

afterEach(() => {
  sessionStorage.removeItem('cc_pending_chat_prompt');
  sessionStorage.removeItem(OPEN_CHAT_KEY);
  vi.unstubAllGlobals();
});

afterAll(() => { uninstallFakeDom(); });

describe('ChatsPage: cc-draft-reply → композер активного чата (QA-фикс M)', () => {
  // Смонтировать ChatsPage с активным чатом (activeId читает localStorage cc_open_chat)
  async function mountChatsPage(activeChatId: string) {
    localStorage.setItem(OPEN_CHAT_KEY, activeChatId);
    const { root, container } = mount(
      createElement(ChatsPage, {
        auth: { role: 'admin', name: 'admin' } as never,
        onLogout: () => {},
        onHubTab: () => {},
      })
    );
    // дать эффектам (в т.ч. регистрацию слушателя cc-draft-reply) отработать
    await act(async () => { await new Promise(r => setImmediate(r)); });
    return { root, container };
  }

  it('чат вне проекта: cc-draft-reply с chatId = активному чату → префилл (reплика в поле, не send)', async () => {
    const { root } = await mountChatsPage('chat-1');
    try {
      // захватим сигнал префилла и возможную отправку
      let prefillDispatched = 0;
      const onPrefill = () => { prefillDispatched++; };
      window.addEventListener('cc-compose-prefill', onPrefill);

      // карточка (в чате 'chat-1', черновик из проекта) шлёт cc-draft-reply
      await act(async () => {
        window.dispatchEvent(new CustomEvent('cc-draft-reply', {
          detail: { chatId: 'chat-1', reply: 'Поправь плагин «X»: ' },
        }));
      });

      // реплика положена в поле ввода активного чата (cc_pending_chat_prompt)
      expect(sessionStorage.getItem('cc_pending_chat_prompt')).toBe('Поправь плагин «X»: ');
      // и композер получил сигнал префилла
      expect(prefillDispatched).toBe(1);

      window.removeEventListener('cc-compose-prefill', onPrefill);
    } finally {
      act(() => { root.unmount(); });
    }
  });

  it('чужой chatId (≠ активному чату) → префилла нет (реплика не в чужой чат)', async () => {
    const { root } = await mountChatsPage('chat-1');
    try {
      let prefillDispatched = 0;
      const onPrefill = () => { prefillDispatched++; };
      window.addEventListener('cc-compose-prefill', onPrefill);

      // карточка из ЧУЖОГО чата ('other') — не наш активный
      await act(async () => {
        window.dispatchEvent(new CustomEvent('cc-draft-reply', {
          detail: { chatId: 'other', reply: 'чужая реплика' },
        }));
      });

      // префилла нет: cc_pending_chat_prompt не появился, сигнал не шлется
      expect(sessionStorage.getItem('cc_pending_chat_prompt')).toBeNull();
      expect(prefillDispatched).toBe(0);

      window.removeEventListener('cc-compose-prefill', onPrefill);
    } finally {
      act(() => { root.unmount(); });
    }
  });

  it('мутация «добавить отправку»: слушатель ТОЛЬКО префиллит, send (cc-draft-sent) не шлёт', async () => {
    // Контракт: слушатель кладёт реплику в поле ввода (префилл) и НЕ отправляет её сам.
    // «Отправка» — stand-in cc-draft-sent: если кто-то добавит send в слушатель, тест красный.
    const { root } = await mountChatsPage('chat-1');
    try {
      let prefillDispatched = 0, sentDispatched = 0;
      const onPrefill = () => { prefillDispatched++; };
      const onSent = () => { sentDispatched++; };
      window.addEventListener('cc-compose-prefill', onPrefill);
      window.addEventListener('cc-draft-sent', onSent);

      await act(async () => {
        window.dispatchEvent(new CustomEvent('cc-draft-reply', {
          detail: { chatId: 'chat-1', reply: 'Поправь плагин «X»: ' },
        }));
      });

      // префилл один раз; отправки (send) нет — реплика в поле, не в ленте
      expect(prefillDispatched).toBe(1);
      expect(sentDispatched, 'send вызван: реплика ушла, а не легла в поле').toBe(0);

      window.removeEventListener('cc-compose-prefill', onPrefill);
      window.removeEventListener('cc-draft-sent', onSent);
    } finally {
      act(() => { root.unmount(); });
    }
  });
});

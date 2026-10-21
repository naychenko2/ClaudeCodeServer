// QA-фикс O (фронт, Глеб MAJOR-3): обработчик cc-draft-reply в WorkspacePage — «префилл,
// а не отправка». Мутация setPendingMessage(reply) (автосабмит через ChatPanel.send) проходила
// при 5300/5300, потому что WorkspacePage не был покрыт (ChatsPage — да).
//
// Тест на РЕАЛЬНОМ слушателе страницы (не на копии логики): монтируем WorkspacePage, ставим
// активный чат (sessionStorage cc_pending_session), шлём cc-draft-reply и проверяем, что
// слушатель положил реплику в поле ввода (cc_pending_chat_prompt + cc-compose-prefill), а НЕ
// отправил её (send не вызван — реплика в поле, не в ленте).
//
// Мутация: вернуть setPendingMessage(reply) → тест «send не вызван» красный.

import { afterAll, afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, createElement } from 'react';
import type { Project } from '../types';

// signalr — все экспорты no-op: без подключения к /hubs/session (в node относительный URL не
// резолвится). Подписки (on*) → unsubscribe, async → promise. Явный список: Proxy-мок vitest
// не видит как named-экспорт.
vi.mock('../lib/signalr', () => {
  const sub = () => () => {};
  const asyncFn = () => Promise.resolve(null);
  return {
    getConnection: () => ({} as never),
    resetConnection: asyncFn,
    ensureConnected: asyncFn,
    joinSession: asyncFn,
    leaveSession: asyncFn,
    sendMessage: asyncFn,
    respondPermission: asyncFn,
    interruptSession: asyncFn,
    setMode: asyncFn,
    compactSession: asyncFn,
    answerQuestion: asyncFn,
    respondPlan: asyncFn,
    respondTeamPlan: asyncFn,
    respondTeamEscalation: asyncFn,
    onMessage: sub,
    onPreviewMessage: sub,
    onFilesChanged: sub,
    onGitStatusChanged: sub,
    onLocalModelMessage: sub,
    joinProject: asyncFn,
    leaveProject: asyncFn,
    joinPreviewLog: asyncFn,
    leavePreviewLog: asyncFn,
    joinUser: asyncFn,
    leaveUser: asyncFn,
    onReconnected: sub,
  };
});

// DesktopWorkspace — главный layout (ChatPanel/Composer, TerminalView, GitChangesRail и др.):
// мок-нулл, чтобы тяжёлые дети не тянули свои зависимости; слушатель cc-draft-reply живёт в
// самом WorkspacePage, а не в детях, поэтому «реальный слушатель» сохраняется.
vi.mock('./workspace/DesktopWorkspace', () => ({ DesktopWorkspace: () => null }));

// git: useGitState → фиксированное состояние с корректным (не null) status; ensureGit no-op,
// чтобы не гонять loadGitStatus → api.git.status (прокси даёт [], а не GitStatus)
vi.mock('../lib/git', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/git')>();
  const emptyStatus = {
    isRepo: false, branch: null, upstream: null, ahead: 0, behind: 0, detached: false,
    staged: [], unstaged: [], untracked: [], isWorktree: false,
  };
  const state = {
    status: emptyStatus, statusLoaded: true,
    log: [], logLoaded: true, unpushed: [], unpushedLoaded: true,
    branches: [], stashes: [], remote: null, error: null, diverged: false, conflictFiles: [],
    busy: false, changedBy: new Map(), myChangedPaths: undefined, dirtySessionIds: undefined,
  };
  return {
    ...orig,
    useGitState: () => state,
    ensureGit: () => {},
    loadUnpushedLog: () => {},
    setActiveSessionForChangedBy: () => {},
  };
});

// terminalSignalr — SignalR-хаб /hubs/terminal (useProjectTerminals → onTerminalMessage):
// no-op, чтобы не поднимать соединение (в node относительный URL не резолвится)
vi.mock('../lib/terminalSignalr', () => {
  const p = () => Promise.resolve(null);
  return {
    resetTerminalConnection: p,
    createTerminal: p,
    connectTerminal: p,
    // listTerminals → [] (useProjectTerminals делает setTerminals(await listTerminals) — null роняет .find)
    listTerminals: () => Promise.resolve([]),
    stopTerminal: p,
    renameTerminal: p,
    sendTerminalInput: p,
    resizeTerminal: p,
    onTerminalMessage: () => () => {},
  };
});

// api — WorkspacePage/HubHeader/панели зовут много api.* (fire-and-forget). Proxy-мок: любой
// api.X.Y(...) → resolved promise([]), чтобы не гонять каждый метод по отдельности.
// importOriginal: именованные экспорты (getGitSessionContext и др.) остаются реальными.
// sessions.list → список с активным чатом: ghost-валидация (WorkspacePage) иначе сбрасывает
// activeSession в null (чат «не найден» в пустом списке), и ветка по chatId не срабатывает.
function makeApiProxy(): unknown {
  const target = function () { return Promise.resolve([]); };
  const make = (path: string) => new Proxy(target, {
    get: (t, prop) => {
      if (prop === Symbol.toPrimitive) return () => '';
      if (prop === 'then') return undefined; // не thenable (иначе await api вешает)
      return make(path + '.' + String(prop));
    },
    apply: () => {
      if (path.endsWith('.sessions.list')) return Promise.resolve([{ id: 'chat-1', projectId: 'proj-1' }]);
      return Promise.resolve([]);
    },
  });
  return make('api');
}
vi.mock('../lib/api', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/api')>();
  return { ...orig, api: makeApiProxy() };
});

// nav: getNav → null (снимка нет), parseHash → null (window.location в фейке нет);
// navPush/navReplace — реальные (fake window.history — no-op)
vi.mock('../lib/nav', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../lib/nav')>();
  return { ...orig, getNav: () => null, parseHash: () => null };
});

// Окружение node: sessionStorage/localStorage не определены — поднимаем in-memory (паттерн ChatsPage).
// makeStorage + стабы ДО импорта: boardControls читает localStorage на уровне модуля (loadGroup).
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
vi.stubGlobal('sessionStorage', makeStorage());
vi.stubGlobal('localStorage', makeStorage());
// xterm (TerminalView) — UMD-модуль ссылается на `self` как на root; в node его нет
vi.stubGlobal('self', globalThis);
// Image (аватары/иконки) — браузерный конструктор; в node нет
vi.stubGlobal('Image', class { set src(_v: string) {} });

const { installFakeDom, mount, uninstallFakeDom } = await import(
  '../components/chat/__tests__/fakeDom'
);
const { WorkspacePage } = await import('./WorkspacePage');

const PROJECT: Project = {
  id: 'proj-1',
  name: 'Тест',
  rootPath: '/test',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
};

beforeEach(() => {
  installFakeDom();
  vi.stubGlobal('sessionStorage', makeStorage());
  vi.stubGlobal('localStorage', makeStorage());
  // Image — браузерный конструктор; afterEach unstubAllGlobals снимает стаб, поэтому ре-стабим
  vi.stubGlobal('Image', class { set src(_v: string) {} });
  // node-окружение: rAF/cAF для панелей — прогоняем в микротаске
  vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => setTimeout(() => cb(0), 0) as unknown as number);
  vi.stubGlobal('cancelAnimationFrame', (id: number) => clearTimeout(id as unknown as ReturnType<typeof setTimeout>));
  // ResizeObserver (centerOffset/панели) — no-op: observe/disconnect не вызывают колбэк
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  // window.history (nav: replaceState/pushState/state) — no-op; fakeDom-окно его не имеет
  (window as unknown as { history: unknown }).history = {
    state: null, replaceState: () => {}, pushState: () => {}, go: () => {}, back: () => {},
  };
  // window.location: инициализатор openFeatureId читает window.location.hash (parseHash замокан,
  // но само обращение к location остаётся)
  (window as unknown as { location: unknown }).location = { hash: '', pathname: '/', search: '' };
  // чистим prefill-ключ и активный чат между тестами
  sessionStorage.removeItem('cc_pending_chat_prompt');
  sessionStorage.removeItem('cc_pending_session');
});

afterEach(() => {
  sessionStorage.removeItem('cc_pending_chat_prompt');
  sessionStorage.removeItem('cc_pending_session');
  vi.unstubAllGlobals();
});

afterAll(() => { uninstallFakeDom(); });

// Смонтировать WorkspacePage с активным чатом: activeSession берётся из sessionStorage
// cc_pending_session (projectId совпадает) — это и есть «открытый чат» воркспейса.
async function mountWorkspace(activeChatId: string) {
  sessionStorage.setItem('cc_pending_session', JSON.stringify({ id: activeChatId, projectId: PROJECT.id }));
  const { root, container } = mount(
    createElement(WorkspacePage, {
      project: PROJECT,
      onGoToProjects: () => {},
      onSwitchHub: () => {},
      auth: { role: 'admin', name: 'admin' } as never,
      onLogout: () => {},
    })
  );
  // дать эффектам (в т.ч. регистрацию слушателя cc-draft-reply) отработать
  await act(async () => { await new Promise(r => setImmediate(r)); });
  return { root, container };
}

describe('WorkspacePage: cc-draft-reply → композер активного чата (MAJOR-3)', () => {
  it('chatId = активному чату → префилл (реплика в поле, не send)', async () => {
    const { root } = await mountWorkspace('chat-1');
    try {
      let prefillDispatched = 0;
      const onPrefill = () => { prefillDispatched++; };
      window.addEventListener('cc-compose-prefill', onPrefill);

      // карточка черновика (в чате 'chat-1') шлёт cc-draft-reply
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
    const { root } = await mountWorkspace('chat-1');
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

  it('ветка настроек проекта (project = проект страницы, без chatId) → префилл', async () => {
    const { root } = await mountWorkspace('chat-1');
    try {
      let prefillDispatched = 0;
      const onPrefill = () => { prefillDispatched++; };
      window.addEventListener('cc-compose-prefill', onPrefill);

      // «Попросить агента исправить» из настроек проекта: project = этому проекту, chatId нет
      await act(async () => {
        window.dispatchEvent(new CustomEvent('cc-draft-reply', {
          detail: { project: { id: PROJECT.id }, reply: 'Поправь в настройках' },
        }));
      });

      expect(sessionStorage.getItem('cc_pending_chat_prompt')).toBe('Поправь в настройках');
      expect(prefillDispatched).toBe(1);

      window.removeEventListener('cc-compose-prefill', onPrefill);
    } finally {
      act(() => { root.unmount(); });
    }
  });

  it('мутация «добавить отправку»: слушатель ТОЛЬКО префиллит, send (cc-draft-sent) не шлёт', async () => {
    // Контракт: слушатель кладёт реплику в поле ввода (префилл) и НЕ отправляет её сам.
    // «Отправка» — stand-in cc-draft-sent: если кто-то вернёт setPendingMessage (send), тест красный.
    const { root } = await mountWorkspace('chat-1');
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

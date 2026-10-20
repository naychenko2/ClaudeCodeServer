// FileViewer — «просмотр в центре» (волна «страница как файл»). Минимальный набор,
// защищающий неизменность поведения (у FileViewer в frontend/ раньше не было НИ ОДНОГО
// теста — Глеб MAJOR-1: «вид и поведение не изменились» было нечем доказать):
//   • Esc закрывает ОДИН раз (MAJOR-1: дубль useEscapeClose убран — единственный слушатель
//     вешает оболочка CenterViewShell; мутация «вернуть второй useEscapeClose» красит);
//   • Esc не перехватывает, если печатают в поле (INPUT) — Escape нужнее ему;
//   • кнопка закрытия (X) закрывает;
//   • имя файла в шапке (mono 700) + ellipsis (п.4: осознанное улучшение, Майя на приёмке);
//   • скрытый центр (offsetParent === null, файл под открытой страницей фичи) — Esc не
//     закрывает файл (п.5: один Esc не должен закрыть и страницу, и файл).
//
// Окружение node (без jsdom): fakeDom (mount) + spyOn на api.files (getContent/getDiff/
// fileUrl) + toast no-op. Файл — простой .txt (isMarkdown=false → useDocProps не тянет
// api.docs; без медиа → streamUrl не зовётся).

import { act } from 'react';
import { createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FileViewer } from './FileViewer';
import { api } from '../lib/api';
import type { Project } from '../types';
import { installFakeDom, uninstallFakeDom, mount, findNodeByAttr, textNodes, reactProps } from './chat/__tests__/fakeDom';

vi.mock('../lib/toast', () => ({ showToast: () => {} }));
// signalr: ensureGit (wireRealtime) строит соединение и читает localStorage — в node без
// сервера/хранилища это падает. Все подписки — no-op (вернуты unsubscribe-fункции).
vi.mock('../lib/signalr', () => ({
  getConnection: () => ({ start: async () => {}, on: () => {}, stop: async () => {} }),
  onMessage: () => () => {},
  onPreviewMessage: () => () => {},
  onFilesChanged: () => () => {},
  onGitStatusChanged: () => () => {},
  onLocalModelMessage: () => () => {},
  onReconnected: () => () => {},
  joinUser: async () => {},
}));

// Минимальный проект (id/name/rootPath/createdAt/updatedAt — обязательные поля).
const project: Project = {
  id: 'p1', name: 'Проект', rootPath: '/home/an/proj',
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-02T00:00:00Z',
};

// Установить window-слушатели (Esc-обработчик CenterViewShell вешает window.addEventListener)
// и document.querySelector (guard [role=dialog]/[role=menu]) — как в FeaturePage.test.ts.
function wireWindow() {
  const win = globalThis as any;
  const doc = (globalThis as any).document;
  if (!win.window.addEventListener) {
    const listeners: Record<string, Set<Function>> = {};
    win.window.addEventListener = (t: string, f: Function) => { (listeners[t] ??= new Set()).add(f); };
    win.window.removeEventListener = (t: string, f: Function) => { listeners[t]?.delete(f); };
    win.window.dispatchEvent = (e: { type: string }) => { listeners[e.type]?.forEach(f => f(e)); return true; };
    win.__winListeners = listeners;
  }
  if (!doc.querySelector) doc.querySelector = () => null;
  return { win, doc };
}

function mountViewer(onClose: () => void) {
  installFakeDom();
  const { win, doc } = wireWindow();
  const { root, container } = mount(createElement(FileViewer, {
    project, filePath: 'docs/readme.txt', onClose,
  }));
  return { root, container, win, doc };
}

// Прогнать microtasks (api.files.getContent/getDiff — resolved-промисы) + таймеры.
async function flush() {
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
}

function esc(win: any, target: unknown = { tagName: 'DIV', isContentEditable: false }) {
  win.window.dispatchEvent({ type: 'keydown', key: 'Escape', defaultPrevented: false, target });
}

describe('FileViewer: Esc / закрытие / имя в шапке', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    // localStorage/sessionStorage — node их не несёт; ensureGit → joinUserGroup их читает.
    const store: Record<string, string> = {};
    (globalThis as any).localStorage = {
      getItem: (k: string) => (k in store ? store[k] : null),
      setItem: (k: string, v: string) => { store[k] = String(v); },
      removeItem: (k: string) => { delete store[k]; },
      clear: () => { for (const k of Object.keys(store)) delete store[k]; },
      key: () => null, get length() { return Object.keys(store).length; },
    };
    (globalThis as any).sessionStorage = (globalThis as any).localStorage;
    vi.spyOn(api.files, 'getContent').mockResolvedValue({
      content: 'hello world', isBinary: false, isImage: false, mimeType: 'text/plain', fileSize: 11,
    });
    vi.spyOn(api.files, 'getDiff').mockResolvedValue({ diff: null });
    vi.spyOn(api.files, 'fileUrl').mockReturnValue('http://localhost:5000/files/x');
  });
  afterEach(() => {
    vi.useRealTimers();
    uninstallFakeDom();
    vi.restoreAllMocks();
  });

  // MAJOR-1: один Esc → ровно один вызов onClose. Дубль useEscapeClose (старый локальный
  // Esc в FileViewer) звал handleClose ДВАЖДЫ; мутация «вернуть второй useEscapeClose»
  // даёт 2 вызова → тест краснеет.
  it('Esc: закрывает ровно один раз (один слушатель, не два)', async () => {
    const onClose = vi.fn();
    const { win } = mountViewer(onClose);
    await flush();
    esc(win);
    expect(onClose, 'один Esc → ровно один handleClose/onClose').toHaveBeenCalledTimes(1);
  });

  it('Esc, когда печатают в поле (INPUT): не перехватывает', async () => {
    const onClose = vi.fn();
    const { win } = mountViewer(onClose);
    await flush();
    esc(win, { tagName: 'INPUT', isContentEditable: false });
    expect(onClose, 'Escape нужнее полю — файл не закрыт').not.toHaveBeenCalled();
  });

  it('кнопка закрытия (X, desktop): закрывает', async () => {
    const onClose = vi.fn();
    const { container } = mountViewer(onClose);
    await flush();
    // X — ToolbarIconButton с title «Закрыть»; ищем по title и кликаем.
    const close = findCloseButton(container);
    expect(close, 'кнопка закрытия в шапке').not.toBeNull();
    // React держит onClick в reactProps узла; вызываем напрямую (fakeDom не диспатчит клики).
    const onClick = (reactProps(close as never) ?? {}) as { onClick?: () => void };
    expect(onClick.onClick, 'onClick на кнопке закрытия').toBeDefined();
    onClick.onClick!();
    expect(onClose, 'клик по X → handleClose').toHaveBeenCalledTimes(1);
  });

  it('имя файла в шапке (mono 700) + ellipsis (п.4: осознанное улучшение)', async () => {
    const onClose = vi.fn();
    const { container } = mountViewer(onClose);
    await flush();
    // Имя — span[data-testid=file-name] (CenterViewShell): mono 700 + ellipsis.
    const name = findNodeByAttr(container, 'data-testid', 'file-name');
    expect(name, 'имя файла в шапке (data-testid=file-name)').not.toBeNull();
    const style = (name as unknown as { style: Record<string, string> }).style;
    expect(style.fontWeight, 'имя — mono 700').toBe('700');
    // п.4: внешний span имени получил overflow/textOverflow/whiteSpace (ellipsis) —
    // осознанное улучшение (имя сжимается многоточием, а не переносится).
    expect(style.overflow, 'ellipsis: overflow hidden').toBe('hidden');
    expect(style.textOverflow, 'ellipsis: textOverflow ellipsis').toBe('ellipsis');
    expect(style.whiteSpace, 'ellipsis: whiteSpace nowrap').toBe('nowrap');
    // Имя реально содержит basename файла.
    expect(textNodes(name as never).join('')).toContain('readme');
  });

  // п.5: скрытый центр (display:none → offsetParent === null) — Esc не закрывает файл.
  // Симуляция: ставим offsetParent=null на корень (как FeatureCenter прячет центр
  // display:none, когда открыта страница фичи). Мутация «убрать проверку offsetParent»
  // в useEscapeClose → тест краснеет.
  it('п.5: скрытый центр (offsetParent=null) — Esc не закрывает файл', async () => {
    const onClose = vi.fn();
    const { win, container } = mountViewer(onClose);
    await flush();
    // Корень CenterViewShell — div с background bgCard; ищем его по data-testid=file-name
    // (внутри) и поднимаемся до корня, либо ловим корень по ref. Проще: корень — первый
    // div контейнера. Ставим на нём offsetParent=null (display:none).
    const rootEl = (container as unknown as { childNodes: { childNodes: unknown[] }[] }).childNodes[0]
      ?? (container as unknown as { childNodes: unknown[] })[0];
    // Корень FileViewer = корень CenterViewShell (div). Находим его: у него data-testid нет,
    // но он — родитель span[data-testid=file-name].
    const name = findNodeByAttr(container, 'data-testid', 'file-name');
    let root: unknown = name;
    while (root && (root as { parentNode: unknown }).parentNode !== (container as unknown)) {
      root = (root as { parentNode: unknown }).parentNode;
    }
    // Корень — ближайший предок name, чей parentNode === container.
    const rootDiv = root as { offsetParent?: unknown; style?: Record<string, string> };
    rootDiv.offsetParent = null; // display:none (центр под открытой страницей фичи)
    esc(win);
    expect(onClose, 'скрытый центр — Esc не закрывает файл (п.5)').not.toHaveBeenCalled();
    // Контроль: видимый центр (offsetParent ≠ null) — Esc закрывает.
    rootDiv.offsetParent = { tagName: 'BODY' };
    esc(win);
    expect(onClose, 'видимый центр — Esc закрывает').toHaveBeenCalledTimes(1);
  });
});

// Кнопка закрытия (X) — ToolbarIconButton с title «Закрыть»; ищем по title.
function findCloseButton(container: unknown): unknown {
  let hit: unknown = null;
  const walk = (n: unknown) => {
    if (hit) return;
    const el = n as { nodeType?: number; attributes?: Record<string, string>; childNodes?: unknown[] };
    if (el.nodeType === 1 && el.attributes?.title === 'Закрыть') { hit = n; return; }
    for (const c of el.childNodes ?? []) walk(c);
  };
  walk(container);
  return hit;
}

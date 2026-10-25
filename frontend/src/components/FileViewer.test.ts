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

function mountViewer(onClose: () => void, isMobile = false, extra: Record<string, unknown> = {}) {
  installFakeDom();
  const { win, doc } = wireWindow();
  const { root, container } = mount(createElement(FileViewer, {
    project, filePath: 'docs/readme.txt', onClose, isMobile, ...extra,
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

// Элемент role=button, чей потомок-текст равен `text` (BackButton: видимая подпись → кнопка).
function findButtonByText(container: unknown, text: string): { getAttribute: (k: string) => string | null } | null {
  let hit: { getAttribute: (k: string) => string | null } | null = null;
  const walk = (n: { nodeType?: number; nodeValue?: string; parentNode?: unknown; childNodes?: unknown[] }) => {
    if (hit) return;
    if (n.nodeType === 3 && n.nodeValue === text) {
      let p = n.parentNode as { getAttribute?: (k: string) => string | null; parentNode?: unknown } | null;
      while (p && p.getAttribute?.('role') !== 'button') p = p.parentNode ?? null;
      hit = p as { getAttribute: (k: string) => string | null } | null;
      return;
    }
    for (const c of n.childNodes ?? []) walk(c as never);
  };
  walk(container as never);
  return hit;
}

// Окружение прогона: таймеры, storage, заглуки api.files. Ответ getContent по умолчанию
// БЕЗ branch/isAnchor — это и есть случай «источник неизвестен» (relay-путь, R-F1a);
// тесты метки дерева переопредивают его сами. contentSpy держится снаружи, чтобы их
// можно было переписать и посмотреть, что реально ушло в вызов.
let contentSpy: { mockResolvedValue: (v: unknown) => void; mock: { calls: unknown[][] } } | null = null;

function setupMocks() {
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
  contentSpy = vi.spyOn(api.files, 'getContent').mockResolvedValue({
    content: 'hello world', isBinary: false, isImage: false, mimeType: 'text/plain', fileSize: 11,
  }) as never;
  vi.spyOn(api.files, 'getDiff').mockResolvedValue({ diff: null });
  vi.spyOn(api.files, 'fileUrl').mockReturnValue('http://localhost:5000/files/x');
}

function teardown() {
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
  contentSpy = null;
}

describe('FileViewer: Esc / закрытие / имя в шапке', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

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

  // m2 (Майя): aria-label с полным текстом возврата (BackButton, мобила). Полный текст
  // «К списку файлов» — в title И aria-label; видимая подпись — короткая «Файлы».
  // Мутация «убрать aria-label из BackButton/CenterViewShell» красит.
  it('m2: aria-label «К списку файлов» (BackButton, мобила), видимая подпись «Файлы»', async () => {
    const onClose = vi.fn();
    const { container } = mountViewer(onClose, true);
    await flush();
    // aria-label — полный текст возврата.
    const back = findNodeByAttr(container, 'aria-label', 'К списку файлов');
    expect(back, 'aria-label «К списку файлов» (BackButton, мобила)').not.toBeNull();
    // Видимая подпись — короткая «Файлы» (текст внутри кнопки).
    expect(textNodes(back as never).join(''), 'видимая подпись «Файлы»').toContain('Файлы');
  });

  it('имя файла в шапке (mono 700): основа — ellipsis, расширение — отдельный flex-item (Глеб m1 = Майя m4)', async () => {
    const onClose = vi.fn();
    const { container } = mountViewer(onClose);
    await flush();
    // Имя — span[data-testid=file-name] (CenterViewShell): mono 700, flex-контейнер.
    const name = findNodeByAttr(container, 'data-testid', 'file-name');
    expect(name, 'имя файла в шапке (data-testid=file-name)').not.toBeNull();
    const style = (name as unknown as { style: Record<string, string> }).style;
    expect(style.fontWeight, 'имя — mono 700').toBe('700');
    // Обёртка (data-testid) — БЕЗ ellipsis (мутация: перенести стиль на обёртку → red).
    expect(style.textOverflow, 'ellipsis НЕ на обёртке (data-testid)').toBeUndefined();
    // Имя — flex-контейнер: дети = [основа, расширение] (readme.txt → 'readme' + '.txt').
    const kids = (name as unknown as { childNodes: { nodeType: number; style: Record<string, string> }[] })
      .childNodes.filter(c => c.nodeType === 1);
    // 1) Основа — первый flex-item: ellipsis (сжимается «…»). Мутация «убрать ellipsis» → red.
    const base = kids[0] as { style: Record<string, string> } | undefined;
    expect(base, 'основа имени (первый flex-item)').toBeDefined();
    expect(base!.style.overflow, 'ellipsis: overflow hidden').toBe('hidden');
    expect(base!.style.textOverflow, 'ellipsis: textOverflow ellipsis').toBe('ellipsis');
    expect(base!.style.whiteSpace, 'ellipsis: whiteSpace nowrap').toBe('nowrap');
    expect(textNodes(base! as never).join(''), 'основа содержит имя файла').toContain('readme');
    // 2) Расширение — ОТДЕЛЬНЫЙ flex-item с flexShrink:0 (не уходит под многоточие).
    // Мутация «вложить расширение внутрь обрезаемого span» → flexShrink:0 пропадает → red.
    const ext = kids[1] as { style: Record<string, string> } | undefined;
    expect(ext, 'расширение — отдельный элемент (второй flex-item)').toBeDefined();
    expect(ext!.style.flexShrink, 'расширение — flexShrink:0 (всегда видно)').toBe('0');
    expect(textNodes(ext! as never).join(''), 'расширение = .txt').toBe('.txt');
  });

  // m2 (Майя): у BackButton полный текст возврата — не только в title, но и в aria-label
  // (видимая подпись короткая, «Файлы»). Мутация «убрать ariaLabel» → aria-label пуст → red.
  it('m2: BackButton (мобила) — aria-label с полным текстом («К списку файлов»)', async () => {
    const onClose = vi.fn();
    const { container } = mountViewer(onClose, true);
    await flush();
    const btn = findButtonByText(container, 'Файлы');
    expect(btn, 'BackButton (role=button) с подписью «Файлы»').not.toBeNull();
    expect(btn!.getAttribute('aria-label'), 'aria-label = полный текст возврата').toBe('К списку файлов');
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

// F2b (фича «Резолвинг ссылок по worktree»): проп sessionId доходит до эндпоинта, а файл,
// пришедший НЕ из заявленного дерева, помечен в шапке чипом «из дерева <ветка>» — фолбэк
// по worktree есть, но он не тихий (разрез bc27cbee §1). Мутации:
//   • убрать `isAnchor === false` из treeChipVisible → тест «при undefined isAnchor чипа
//     нет» краснеет (метка появляется на неизвестном источнике);
//   • не передать sessionId в getContent → тест про аргумент краснеет.
describe('FileViewer: проп sessionId и чип «из дерева <ветка>»', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  const contentWith = (tree: { branch?: string; isAnchor?: boolean }) => ({
    content: 'hello world', isBinary: false, isImage: false, mimeType: 'text/plain', fileSize: 11, ...tree,
  });

  it('при isAnchor=false виден чип с именем ветки, sessionId ушёл в getContent', async () => {
    contentSpy!.mockResolvedValue(contentWith({ branch: 'feat/worktree-material-links', isAnchor: false }));
    const { container } = mountViewer(vi.fn(), false, { sessionId: 'sess-1' });
    await flush();
    // Проп доходит до эндпоинта: 4-й аргумент getContent — { sessionId }
    expect(contentSpy!.mock.calls[0][3], 'sessionId передан opts-аргументом getContent').toEqual({ sessionId: 'sess-1' });
    const chip = findNodeByAttr(container, 'data-testid', 'file-tree-chip');
    expect(chip, 'чип «из дерева <ветка>» в шапке').not.toBeNull();
    expect(textNodes(chip as never).join(''), 'в чипе имя ветки').toContain('из дерева feat/worktree-material-links');
  });

  // Хвост R-F1a: для relay-пути branch есть, а isAnchor может отсутствовать — источник
  // неизвестен, выдумывать метку нельзя. Мутация «убрать isAnchor === false из
  // treeChipVisible» даёт чип здесь же → тест краснеет.
  it('при undefined isAnchor чипа нет', async () => {
    contentSpy!.mockResolvedValue(contentWith({ branch: 'feat/worktree-material-links' }));
    const { container } = mountViewer(vi.fn(), false, { sessionId: 'sess-1' });
    await flush();
    expect(findNodeByAttr(container, 'data-testid', 'file-tree-chip'), 'источник неизвестен — метки нет').toBeNull();
  });

  // Файл из заявленного дерева-анкера — ничего не скрываем, но и не маркируем
  it('при isAnchor=true (файл из анкера) чипа нет', async () => {
    contentSpy!.mockResolvedValue(contentWith({ branch: 'main', isAnchor: true }));
    const { container } = mountViewer(vi.fn(), false, { sessionId: 'sess-1' });
    await flush();
    expect(findNodeByAttr(container, 'data-testid', 'file-tree-chip'), 'файл из анкера — метки нет').toBeNull();
  });

  // Без пропа вызов остаётся прежним (старые вызывающие не меняются)
  it('без sessionId в getContent нет opts-аргумента', async () => {
    const { container } = mountViewer(vi.fn());
    await flush();
    expect(contentSpy!.mock.calls[0][3], 'без пропа — старая сигнатура вызова').toBeUndefined();
    expect(findNodeByAttr(container, 'data-testid', 'file-tree-chip'), 'без дерева-метки').toBeNull();
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

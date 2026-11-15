// MediaBlock: кнопка «Добавить в проект» по роли (S2c-F4i). Точка права — contentRights.ts
// (S2c-F4a); здесь — только гейт canWriteContent('file.saveFromUrl') в условии рендера
// кнопки. Закрытая дверь ПРЯЧЕТ (0 узлов в DOM): кнопка — единственный вход в диалог
// «Добавить в проект» и в doSave → api.files.saveFromUrl, поэтому спрятанный вход прячет
// всю цепочку записи (на бэке save-from-url остаётся на Write).
//   • читатель и редактор — кнопки сохранения нет; само медиа и «Скачать» на месте;
//   • владелец — кнопка есть; без myRole (обычный проект/старый бэк) — как раньше;
//   • локальное медиа (уже лежит в проекте) — кнопки сохранения нет ни у кого (как раньше).
// Мутация «убрать canWriteContent из canSaveFromUrl» красит тесты читателя и редактора;
// «canSaveFromUrl = !!project» (дверь снята целиком) — то же.
//
// Ф-8 (требование Андрея 15.11): в личном чате (нет проекта, есть ChatSessionContext) кнопка
// становится личной — «Сохранить в Личное», и её цепочка уходит на личный маршрут чата
// (api.files.saveFromUrlPersonal → /personal/chats/{id}/files/save-from-url). Без сессии
// (рендер вне ленты чата) личной области нет — кнопки нет вовсе.
//
// Окружение node (без jsdom), по образцу components/__tests__/FileExplorerRole.test.ts:
// mount fakeDom. Стаб storage не нужен: readStoredToken (mediaSrc/proxyUrl) безопасен без
// него — readFrom в lib/offline.ts закрыт try/catch и typeof-проверкой.

import { act, createElement } from 'react';
import { describe, it, expect, afterEach, vi } from 'vitest';
import { MediaBlock, type MediaItem } from '../MediaBlock';
import { ChatProjectContext, ChatSessionContext } from '../contexts';
import type { ProjectRoleView } from '../../../types';
import { installFakeDom, uninstallFakeDom, mount, textNodes, reactProps } from './fakeDom';
// Конец цепочки личной записи (Ф-8): request() мокнут, цепка api → personalFilesBase настоящая
// (тот же образец, что features/files/__tests__/personalFiles.test.ts); цепочка проходит
// через компонент (MediaBlock → api), не через прямой вызов filesApi
const { request } = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../../../lib/offline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/offline')>();
  return { ...orig, request };
});
import { personalFilesSupports } from '../../../features/files/personalFiles';

const REMOTE: MediaItem = { kind: 'image', url: 'https://fal.media/img.png', width: 100, height: 100 };
// Локальное медиа — строгий stream-URL: оно уже в проекте, save-from-url к нему не применяется
const LOCAL: MediaItem = { kind: 'image', url: '/api/projects/p1/files/stream?path=docs/img.png', width: 100, height: 100 };

function mountBlock(role?: ProjectRoleView, m: MediaItem = REMOTE) {
  installFakeDom();
  const { root, container } = mount(
    createElement(
      ChatProjectContext.Provider, { value: { id: 'p1', rootPath: '/tmp/p1', myRole: role } },
      createElement(MediaBlock, { m, filename: 'img.png' }),
    ),
  );
  return { root, container };
}

const hasText = (container: unknown, text: string): boolean =>
  textNodes(container as never).some(t => t.includes(text));

describe('MediaBlock: «Добавить в проект» по роли (S2c-F4i)', () => {
  afterEach(uninstallFakeDom);

  it('читатель: нет кнопки сохранения; медиа и «Скачать» на месте', async () => {
    const { root, container } = mountBlock('reader');
    expect(hasText(container, 'Добавить в проект'), 'кнопка сохранения скрыта').toBe(false);
    expect(hasText(container, 'Скачать'), 'медиа и «Скачать» на месте').toBe(true);
    await act(async () => { root.unmount(); });
  });

  it('редактор: нет кнопки сохранения (дверь file.saveFromUrl закрыта редактору)', async () => {
    const { root, container } = mountBlock('editor');
    expect(hasText(container, 'Добавить в проект'), 'кнопка сохранения скрыта').toBe(false);
    expect(hasText(container, 'Скачать'), 'медиа и «Скачать» на месте').toBe(true);
    await act(async () => { root.unmount(); });
  });

  it('владелец: кнопка есть', async () => {
    const { root, container } = mountBlock('owner');
    expect(hasText(container, 'Добавить в проект'), 'кнопка сохранения на месте у владельца').toBe(true);
    await act(async () => { root.unmount(); });
  });

  // Контроль: без myRole (обычный проект/старый бэк/чат вне проекта) — как раньше
  it('без myRole — как раньше: кнопка есть', async () => {
    const { root, container } = mountBlock(undefined);
    expect(hasText(container, 'Добавить в проект'), 'обычный проект — кнопка на месте').toBe(true);
    await act(async () => { root.unmount(); });
  });

  it('локальное медиа: нет кнопки сохранения даже владельцу (оно уже в проекте)', async () => {
    const { root, container } = mountBlock('owner', LOCAL);
    expect(hasText(container, 'Добавить в проект'), 'локальное медиа не копируют через save-from-url').toBe(false);
    expect(hasText(container, 'Скачать'), 'локальное медиа и «Скачать» на месте').toBe(true);
    await act(async () => { root.unmount(); });
  });
});

// Личный чат: ChatProjectContext нет (ChatPanel не проведёт его без проекта), есть
// ChatSessionContext — по нему блок опознаёт личную область и рисует личную кнопку
function mountPersonal(m: MediaItem = REMOTE, sessionId: string | null = 's1') {
  installFakeDom();
  const { root, container } = mount(
    createElement(
      ChatSessionContext.Provider, { value: sessionId },
      createElement(MediaBlock, { m, filename: 'img.png' }),
    ),
  );
  return { root, container };
}

// Диалог сохранения (Modal) — портал в document.body: его кнопки ищем обходом body,
// кнопку самого блока — обходом контейнера mount (контейнер в body не пристёгнут)
const body = () => (globalThis as { document: { body: unknown } }).document.body as never;
const buttonByLabel = (from: unknown, label: string): never => {
  let hit: never | null = null;
  const walk = (x: { nodeType: number; nodeName: string; childNodes?: unknown[] }) => {
    if (hit) return;
    if (x.nodeType === 1 && x.nodeName === 'BUTTON' && textNodes(x as never).join('').includes(label)) hit = x as never;
    for (const c of x.childNodes ?? []) walk(c);
  };
  walk(from as never);
  return hit!;
};

describe('MediaBlock: «Сохранить в Личное» в личном чате (Ф-8)', () => {
  afterEach(uninstallFakeDom);

  it('личный чат (нет проекта, сессия есть): кнопка «Сохранить в Личное» есть, проектной — нет', async () => {
    const { root, container } = mountPersonal();
    expect(hasText(container, 'Сохранить в Личное'), 'личная кнопка сохранения на месте').toBe(true);
    expect(hasText(container, 'Добавить в проект'), 'проектной кнопки в личном чате нет').toBe(false);
    expect(hasText(container, 'Скачать'), 'медиа и «Скачать» на месте').toBe(true);
    await act(async () => { root.unmount(); });
  });

  it('локальное медиа в личном чате: нет кнопки сохранения (тот же гейт isLocalStreamUrl)', async () => {
    const { root, container } = mountPersonal(LOCAL);
    expect(hasText(container, 'Сохранить в Личное'), 'локальное медиа не копируют в «Личное»').toBe(false);
    expect(hasText(container, 'Скачать'), 'локальное медиа и «Скачать» на месте').toBe(true);
    await act(async () => { root.unmount(); });
  });

  it('без сессии (рендер вне ленты чата): личной области нет — кнопки нет', async () => {
    const { root, container } = mountPersonal(REMOTE, null);
    expect(hasText(container, 'Сохранить в Личное'), 'без сессии личной кнопки нет').toBe(false);
    expect(hasText(container, 'Добавить в проект'), 'проектной кнопки тоже нет').toBe(false);
    await act(async () => { root.unmount(); });
  });

  // Конец цепочки личной кнопки (вердикт Р-Ф-8): клик «Сохранить в Личное» → диалог
  // с предлагаемой папкой → «Сохранить» → doSave → saveFromUrlPersonal → личный маршрут
  // чата, не /projects/…. Панель «Файлы» закрыта (стора нет): doSave подставляет медиальную
  // папку типа медиа, и path в теле запроса начинается с 'images/' — контракт бэка
  // (PersonalFilesController.MediaKindOf). Мутация «resolveSaveDir для личной области
  // возвращает ''» (MediaBlock.tsx) красит тест и на строке папки диалога, и на path.
  it('клик «Сохранить в Личное»: диалог предлагает images/, тело запроса — path с images/', async () => {
    request.mockReset();
    request.mockResolvedValue({ path: 'images/img.png' });
    expect(personalFilesSupports('POST files/save-from-url'), 'личный маршрут объявляет save-from-url').toBe(true);
    const { root, container } = mountPersonal();
    act(() => {
      (reactProps(buttonByLabel(container, 'Сохранить в Личное'))!.onClick as (e: { stopPropagation: () => void }) => void)({ stopPropagation: () => {} });
    });
    // Диалог показывает предлагаемую папку: не пусто и не папку панели (панель закрыта) — медиальную
    expect(textNodes(body()).join(''), 'диалог показывает медиальную папку').toContain('Папка: images/');
    await act(async () => {
      (reactProps(buttonByLabel(body(), 'Сохранить'))!.onClick as () => void)();
    });
    const [url, init] = request.mock.calls[0] as [string, { method: string; body: string }];
    expect(url).toBe('/personal/chats/s1/files/save-from-url');
    expect(init.method).toBe('POST');
    const bodyJson = JSON.parse(init.body) as { url: string; path: string };
    expect(bodyJson.url).toBe('https://fal.media/img.png');
    expect(bodyJson.path.startsWith('images/'), 'path в теле запроса начинается с медиальной папки').toBe(true);
    await act(async () => { root.unmount(); });
  });
});

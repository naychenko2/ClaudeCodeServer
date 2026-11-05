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
// Окружение node (без jsdom), по образцу components/__tests__/FileExplorerRole.test.ts:
// mount fakeDom. Стаб storage не нужен: readStoredToken (mediaSrc/proxyUrl) безопасен без
// него — readFrom в lib/offline.ts закрыт try/catch и typeof-проверкой.

import { act, createElement } from 'react';
import { describe, it, expect, afterEach } from 'vitest';
import { MediaBlock, type MediaItem } from '../MediaBlock';
import { ChatProjectContext } from '../contexts';
import type { ProjectRoleView } from '../../../types';
import { installFakeDom, uninstallFakeDom, mount, textNodes } from './fakeDom';

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

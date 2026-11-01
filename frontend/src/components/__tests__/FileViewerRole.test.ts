// FileViewer: контролы записи по роли (S2c-F4c). Точка права — contentRights.ts (S2c-F4a);
// здесь — только связка can(маршрут) && canWriteContent(дверь) в гейтах рендера
// canEdit/canDelete/canRevert/… (FileViewer, блок «Маршрут + право записи по роли»).
// Закрытая дверь ПРЯЧЕТ (0 узлов в DOM), не отключена:
//   • читатель — ни «Править», ни «Откатить изменения» (↺), ни «Удалить»;
//   • редактор — «Править» и «Удалить» есть, «Откатить изменения» — нет
//     (дверь 'file.revert' закрыта на бэке — закрыта и здесь);
//   • владелец и контроль без myRole (обычный проект/старый бэк) — всё как раньше.
// Остальные точки записи вьювера (сохранение и диалог конфликта, автосейв draw.io,
// office-правок, стейдж ханки, «Вернуть эту версию») рисуются только из-под этих гейтов —
// отдельного гейта не требуют (перечень — в отчёте шага).
// Мутации: «снять && canWriteContent у canEdit» красит читателя; «у canRevert» —
// отсутствие ↺ у читателя и редактора; «у canDelete» — отсутствие «Удалить» у читателя.
//
// Окружение — как FileViewerSaveConflict.test.ts (тот же монтаж .txt-файла): node +
// fakeDom + spyOn на api.files. diff в mocked getDiff ненулевой: кнопка «Откатить
// изменения» рисуется только при наличии чего откатывать (гейт diff && canRevert).
// Вторичные действия (↺, «Удалить») в тестовом окружении не меряются (у полосы нет
// ширины) и уезжают в меню «Ещё» — его открываем кликом по триггеру и ищем пункты
// в портале body; «Править» — главное действие, оно в строке всегда.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FileViewer } from '../FileViewer';
import { api } from '../../lib/api';
import type { Project, ProjectRoleView } from '../../types';
import { installFakeDom, uninstallFakeDom, mount, findNodeByAttr, textNodes, reactProps } from '../chat/__tests__/fakeDom';

vi.mock('../../lib/toast', () => ({ showToast: () => {} }));
// В node navigator.onLine не определён → «офлайн», а в офлайне контролы записи не рисуются вовсе
vi.mock('../../hooks/useOnline', () => ({ useOnline: () => true }));
// signalr: ensureGit (wireRealtime) строит соединение и читает localStorage — в node падает
vi.mock('../../lib/signalr', () => ({
  getConnection: () => ({ start: async () => {}, on: () => {}, stop: async () => {} }),
  onMessage: () => () => {},
  onPreviewMessage: () => () => {},
  onFilesChanged: () => () => {},
  onGitStatusChanged: () => () => {},
  onLocalModelMessage: () => () => {},
  onReconnected: () => () => {},
  joinUser: async () => {},
}));

function projectWith(role?: ProjectRoleView): Project {
  return {
    id: 'p1', name: 'Проект', rootPath: '/home/an/proj',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-02T00:00:00Z',
    myRole: role,
  };
}

let root: { unmount: () => void } | null = null;

function setupMocks() {
  vi.useFakeTimers();
  const store: Record<string, string> = {};
  (globalThis as any).localStorage = {
    getItem: (k: string) => (k in store ? store[k] : null),
    setItem: (k: string, v: string) => { store[k] = String(v); },
    removeItem: (k: string) => { delete store[k]; },
    clear: () => { for (const k of Object.keys(store)) delete store[k]; },
    key: () => null, get length() { return Object.keys(store).length; },
  };
  (globalThis as any).sessionStorage = (globalThis as any).localStorage;
  // getComputedStyle — в fakeDom его нет; все зоны «visible» (прокрутки нет), как в FeaturePage.test.ts
  if (!(globalThis as any).getComputedStyle) (globalThis as any).getComputedStyle = () => ({ overflowY: 'visible' });
  // Файл из корня (без branch) и с ненулевым диффом: иначе «Откатить» не проверяется —
  // оно и без роли не рисуется, когда откатывать нечего
  vi.spyOn(api.files, 'getContent').mockResolvedValue({
    content: 'hello world', isBinary: false, isImage: false, mimeType: 'text/plain', fileSize: 11,
  }) as never;
  vi.spyOn(api.files, 'getDiff').mockResolvedValue({ diff: '+hello\n-world' });
  vi.spyOn(api.files, 'fileUrl').mockReturnValue('http://localhost:5000/files/x');
}

function teardown() {
  act(() => root?.unmount());
  root = null;
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
}

async function mountViewer(role?: ProjectRoleView) {
  installFakeDom();
  const r = mount(createElement(FileViewer, { project: projectWith(role), filePath: 'docs/readme.txt', onClose: vi.fn() }));
  root = r.root;
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
  return r.container;
}

// Открыть меню «Ещё» (триггер вторичных действий; его пункт-портал — в document.body)
async function openOverflowMenu(container: unknown) {
  const trigger = findNodeByAttr(container as never, 'title', 'Ещё');
  expect(trigger, 'триггер «Ещё» в тулбаре').not.toBeNull();
  const onClick = (reactProps(trigger as never) ?? {}) as { onClick?: () => void };
  expect(onClick.onClick, 'onClick на триггере').toBeDefined();
  await act(async () => { onClick.onClick!(); await vi.advanceTimersByTimeAsync(0); });
}

// Главное действие («Править») — в строке тулбара всегда; ищем по title, как FileViewer.test.ts
const hasInlineControl = (container: unknown, title: string) => findNodeByAttr(container as never, 'title', title) !== null;
// Пункт меню «Ещё» — по подписи строки в портале body
const hasMenuItem = (label: string) => textNodes((globalThis as any).document.body as never).includes(label);

describe('FileViewer: контролы записи по роли (S2c-F4c)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  // Просмотр — всем ролям: проверка не про «пустая панель», а про контролы записи
  it('файл виден любой роли: имя в шапке на месте', async () => {
    const container = await mountViewer('reader');
    expect(findNodeByAttr(container as never, 'data-testid', 'file-name'), 'имя файла в шапке').not.toBeNull();
  });

  it('читатель: нет «Править», нет «Откатить изменения», нет «Удалить»', async () => {
    const container = await mountViewer('reader');
    expect(hasInlineControl(container, 'Править'), 'правка скрыта').toBe(false);
    await openOverflowMenu(container);
    expect(hasMenuItem('Откатить изменения'), 'откат скрыт').toBe(false);
    expect(hasMenuItem('Удалить'), 'удаление скрыто').toBe(false);
  });

  it('редактор: «Править» и «Удалить» есть; «Откатить изменения» — нет (door file.revert)', async () => {
    const container = await mountViewer('editor');
    expect(hasInlineControl(container, 'Править'), 'правка открыта редактору').toBe(true);
    await openOverflowMenu(container);
    expect(hasMenuItem('Удалить'), 'удаление открыто редактору').toBe(true);
    expect(hasMenuItem('Откатить изменения'), 'откат закрыт редактору').toBe(false);
  });

  it('владелец: все контролы как раньше', async () => {
    const container = await mountViewer('owner');
    expect(hasInlineControl(container, 'Править'), 'правка есть').toBe(true);
    await openOverflowMenu(container);
    expect(hasMenuItem('Откатить изменения'), 'откат есть').toBe(true);
    expect(hasMenuItem('Удалить'), 'удаление есть').toBe(true);
  });

  // Контроль: без myRole (обычный проект/старый бэк) — как владелец: не «всегда прятать»
  it('без myRole — как раньше: все контролы на месте', async () => {
    const container = await mountViewer(undefined);
    expect(hasInlineControl(container, 'Править'), 'правка есть').toBe(true);
    await openOverflowMenu(container);
    expect(hasMenuItem('Откатить изменения'), 'откат есть').toBe(true);
    expect(hasMenuItem('Удалить'), 'удаление есть').toBe(true);
  });
});

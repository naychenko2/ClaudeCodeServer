// FileViewer: контролы записи по роли (S2c-F4c). Точка права — contentRights.ts (S2c-F4a);
// здесь — только связка can(маршрут) && canWriteContent(дверь) в гейтах рендера
// canEdit/canDelete/canRevert/… (FileViewer, блок «Маршрут + право записи по роли»).
// Закрытая дверь ПРЯЧЕТ (0 узлов в DOM), не отключена:
//   • читатель — ни «Править», ни «Откатить изменения» (↺), ни «Удалить»;
//   • редактор — «Править» и «Удалить» есть, «Откатить изменения» — нет
//     (дверь 'file.revert' закрыта на бэке — закрыта и здесь);
//   • владелец и контроль без myRole (обычный проект/старый бэк) — всё как раньше.
// Двери не переносимы: у каждой точки записи свой гейт по СВОЕЙ двери. Сохранение и автосейв
// draw.io живут в ветке редактирования под canEdit, office-правок — под canOfficeEdit, стейдж
// ханки и «Вернуть эту версию» — своими строками, «Сохранить мою копией» в диалоге конфликта —
// под canCopyOnConflict ('file.create': копия создаёт файл, 'file.save' её не гейтит).
// Мутации: «снять && canWriteContent у canEdit» красит читателя; «у canRevert» —
// отсутствие ↺ у читателя и редактора; «у canDelete» — отсутствие «Удалить» у читателя;
// «у canCopyOnConflict» — кнопку «Сохранить мою копией» у читателя (диалог конфликта).
//
// Окружение — как FileViewerSaveConflict.test.ts (тот же монтаж .txt-файла): node +
// fakeDom + spyOn на api.files. diff в mocked getDiff ненулевой: кнопка «Откатить
// изменения» рисуется только при наличии чего откатывать (гейт diff && canRevert).
// Вторичные действия (↺, «Удалить») в тестовом окружении не меряются (у полосы нет
// ширины) и уезжают в меню «Ещё» — его открываем кликом по триггеру и ищем пункты
// в портале body; «Править» — главное действие, оно в строке всегда.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FileViewer, __test } from '../FileViewer';
import { api, FileConflictError } from '../../lib/api';
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

// ——— Диалог конфликта и «копия» (доработка по ревью d90f7171) ———
// «Сохранить мою копией» — создание файла: свой маршрут 'POST files/create' и своя дверь
// 'file.create'. Роль с закрытой 'file.create' (читатель) имеет закрытую и 'file.save':
// «Править» ей нет, а значит через UI к 409 она не доходит — диалог конфликта в этой роли
// НЕДОСТИЖИМ. Состояние «диалог открыт, дверь копии закрыта» получаем тестовым хуком
// __test.writeHooks.save() — тем же паттерном, что FileViewerSaveConflict.test.ts (хук держит
// замыкание handleSave последнего рендера; из UI его никто не зовёт). Проверяем рендер
// диалога, а не путь к нему: путь появится, если дверь 'file.create' когда-нибудь закроют
// роли, которая писать может.
const CONFLICT_BODY = { sha256: 'sha-other', content: 'their text', author: null, changedAt: '2026-11-01T09:00:00Z', source: null };

async function openConflictAs(role?: ProjectRoleView) {
  (globalThis as any).localStorage.setItem('cc_display_name', 'Мария');
  vi.spyOn(api.files, 'saveContent').mockImplementation(() => { throw new FileConflictError(CONFLICT_BODY); });
  const container = await mountViewer(role);
  const hooks = __test.writeHooks.current;
  expect(hooks, 'тестовый хук сохранения не установлен').not.toBeNull();
  await act(async () => { await hooks!.save(); await vi.advanceTimersByTimeAsync(100); });
  return container;
}

// Диалог — портал в document.body
const bodyEl = () => (globalThis as any).document.body as never;
const bodyText = () => textNodes(bodyEl()).join('\n');
const dialogActions = () => findNodeByAttr(bodyEl(), 'data-testid', 'conflict-actions');

// Кнопка диалога по подписи: текстовый узел → ближайший предок с onClick (ui/Button)
function clickDialogAction(label: string): (() => void) | null {
  let hit: (() => void) | null = null;
  const walk = (n: any): void => {
    if (hit) return;
    if (n.nodeType === 3 && n.nodeValue === label) {
      let p = n.parentNode;
      while (p) {
        const props = reactProps(p) as { onClick?: unknown } | null;
        if (props && typeof props.onClick === 'function') { hit = props.onClick as () => void; return; }
        p = p.parentNode;
      }
      return;
    }
    for (const c of n.childNodes ?? []) walk(c);
  };
  walk(bodyEl());
  return hit;
}

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

  // ——— «Сохранить мою копией» в диалоге конфликта: за своей дверью 'file.create' ———
  it('конфликт у читателя: диалог открыт, кнопки копии — 0 узлов, подписи имени копии нет', async () => {
    await openConflictAs('reader');
    const text = bodyText();
    expect(dialogActions(), 'диалог конфликта открыт').not.toBeNull();
    expect(text).toContain('Файл изменили, пока вы правили');
    expect(text, 'кнопка «копия» скрыта целиком, не отключена').not.toContain('Сохранить мою копией');
    expect(text, 'подписи имени копии нет').not.toContain('readme (копия, Мария).txt');
    expect(text, 'остальные действия диалога на месте').toContain('Заменить своей');
    expect(text).toContain('Взять чужую версию');
  });

  it('конфликт у редактора: копия на месте (дверь file.create редактору открыта)', async () => {
    await openConflictAs('editor');
    expect(bodyText(), 'кнопка «копия» есть').toContain('Сохранить мою копией');
    expect(bodyText(), 'имя копии подписано').toContain('readme (копия, Мария).txt');
  });

  it('конфликт у владельца: копия работает как раньше — создаёт файл-копию и закрывает диалог', async () => {
    const create = vi.spyOn(api.files, 'createFile').mockResolvedValue(undefined);
    await openConflictAs('owner');
    const btn = clickDialogAction('Сохранить мою копией');
    expect(btn, 'кнопка «копия» в диалоге').not.toBeNull();
    await act(async () => { btn!(); await vi.advanceTimersByTimeAsync(100); });
    expect(create, 'копия — новый файл с моим текстом, оригинал не тронут')
      .toHaveBeenCalledWith('p1', 'docs/readme (копия, Мария).txt', 'hello world');
    expect(dialogActions(), 'конфликт закрыт').toBeNull();
  });
});

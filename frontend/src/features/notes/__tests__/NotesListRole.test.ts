// NotesList: контролы записи по роли (S2c-F4d). Точка права — contentRights.ts (S2c-F4a);
// здесь — только связка canNote(source, дверь): проект источника берётся из кэша
// useAllProjects по id == source. Закрытая дверь ПРЯЧЕТ (0 узлов, draggable не ставится),
// не отключена:
//   • читатель — в меню заметки нет «Удалить»/«Переместить в…»/«Добавить в избранное»
//     («Открыть» — чтение — остаётся); в меню папки нет «Новая папка»/«Переименовать»/
//     «Переместить в…»/«Удалить папку»; hover-иконок удаления/избранного/папки нет;
//     draggable ни у строки заметки, ни у строки папки;
//   • редактор — всё перечисленное есть (двери 'note.*' открыты редактору);
//   • владелец, проект без myRole, проект не в списке (кэш не прогрет), 'personal' и
//     вызывающий без колбэков — как раньше.
// Мутации: «снять гейт note.delete» красит тест читателя (меню заметки); «снять гейт
// note.folder» — тест читателя (меню папки и draggable строки папки).
// Ветка БЕЗ sourceFilter (NotesPage) — единственная, где рисуется шапка источника с её
// контекстным меню (kind:'source', NotesList.tsx:671); с sourceFilter (панель воркспейса)
// шапок нет вовсе (:656). Её пункты «Новая заметка» (:746) и «Новая папка» (:747) загейтированы
// теми же дверями — последние два теста проверяют именно эту ветку; мутация «снять оба
// гейта» (:746/:747) красит их.
//
// Окружение — по образцу NoteViewSaveConflict.test.ts: node + fakeDom + мок signalr.
// Список заметок передаётся пропом (стор заметок здесь не грузится); папка «docs»
// строится buildTree из пути заметки. Проекты — мок api.projects.list + принудительный
// invalidateProjectsCache: кэш useAllProjects общий на процесс, без force второй тест
// получил бы список первого. Контекстное меню NotesList рендерится внутри компонента
// (не портал) — поиск по контейнеру; hover-иконки — по aria-label (ui/IconButton).
import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { NotesList } from '../NotesList';
import { api } from '../../../lib/api';
import { invalidateProjectsCache } from '../../projects/useAllProjects';
import type { NoteSummary, Project, ProjectRoleView } from '../../../types';
import { installFakeDom, uninstallFakeDom, mount, findNodeByAttr, reactProps, textNodes } from '../../../components/chat/__tests__/fakeDom';

// signalr: цепочка импортов кита (lib/notes) тянет модуль; в node реальное соединение
// и localStorage не нужны — все подписки no-op (как в FileExplorerRole.test.ts)
vi.mock('../../../lib/signalr', () => ({
  getConnection: () => ({ start: async () => {}, on: () => {}, stop: async () => {} }),
  onMessage: () => () => {},
  onPreviewMessage: () => () => {},
  onFilesChanged: () => () => {},
  onGitStatusChanged: () => () => {},
  onLocalModelMessage: () => () => {},
  onReconnected: () => () => {},
  joinUser: async () => {},
}));

// Заметка проекта 'p1' в папке 'docs' — из её пути buildTree строит и узел папки
const NOTE: NoteSummary = {
  id: 'n1', title: 'Заметка', source: 'p1', sourceLabel: 'Проект', path: 'docs/Заметка.md',
  tags: [], createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-11-01T08:00:00Z',
};

function projectWith(role?: ProjectRoleView): Project {
  return {
    id: 'p1', name: 'Проект', rootPath: '/home/an/proj',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-11-02T00:00:00Z',
    myRole: role,
  };
}

let root: { unmount: () => void } | null = null;
let container: any = null;

function setupMocks() {
  vi.useFakeTimers();
}
function teardown() {
  act(() => { root?.unmount(); });
  root = null; container = null;
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
}

// Смонтировать список одного источника и дождаться загрузки проектов (useAllProjects
// → api.projects.list). projects — состав кэша для этого теста; [] — «проект не найден».
// withCallbacks — пропы родителя (создание заметки): без них пунктов «Новая заметка»
// нет ни у какой роли — это поведение родителя (S2c-F4h), не гейт.
// noSourceFilter — ветка без sourceFilter (NotesPage): единственная с шапкой источника;
// компонент запросит в ней лейблы источников (NotesList.tsx:128) — метка берётся из
// sourceLabel заметки, мок — чтобы эффект не ходил в сеть.
async function mountList(projects: Project[], opts: { source?: string; withCallbacks?: boolean; noSourceFilter?: boolean } = {}) {
  const source = opts.source ?? 'p1';
  installFakeDom();
  vi.spyOn(api.projects, 'list').mockResolvedValue(projects);
  if (opts.noSourceFilter) vi.spyOn(api.notes, 'sources').mockResolvedValue([]);
  invalidateProjectsCache();
  const r = mount(createElement(NotesList, {
    notes: [source === 'p1' ? NOTE : { ...NOTE, source: 'personal', sourceLabel: 'Личный' }],
    selectedId: null,
    onSelect: () => {},
    ...(opts.noSourceFilter ? {} : { sourceFilter: source }),
    ...(opts.withCallbacks ? { onCreateInFolder: () => {}, onMoved: () => {}, onDeleted: () => {}, onIdsRemapped: () => {} } : {}),
  }));
  root = r.root; container = r.container;
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
}

const hasText = (t: string) => textNodes(container).some(n => n.includes(t));

// Найти строку по тексту и подняться до предка с нужным React-пропом (null, если пропа
// нет ни у одного предка — так проверяется «draggable не ставится»)
function rowWith(text: string, prop: string): any {
  let hit: any = null;
  const walk = (n: any): void => {
    if (hit) return;
    if (n.nodeType === 3 && (n.nodeValue ?? '').includes(text)) { hit = n; return; }
    for (const c of n.childNodes ?? []) walk(c);
  };
  walk(container);
  let el = hit;
  while (el && !((reactProps(el) ?? {})[prop])) el = el.parentNode;
  return el;
}

async function openCtx(text: string) {
  const row = rowWith(text, 'onContextMenu');
  expect(row, `строка «${text}» с onContextMenu`).not.toBeNull();
  await act(async () => {
    const fn = (reactProps(row) as any).onContextMenu as (e: unknown) => void;
    fn({ preventDefault: () => {}, stopPropagation: () => {}, clientX: 10, clientY: 10 });
  });
}

async function hoverOn(text: string) {
  const row = rowWith(text, 'onMouseEnter');
  expect(row, `строка «${text}» с onMouseEnter`).not.toBeNull();
  await act(async () => { (reactProps(row) as any).onMouseEnter(); });
}

const icon = (label: string) => findNodeByAttr(container, 'aria-label', label);

// Контейнер контекстного меню: единственный узел с пропом onMouseDown (NotesList.tsx:694).
// Меню шапки с закрытыми дверями существует, но пустое — «0 узлов» проверяется числом его
// элементных детей, а не только отсутствием текста
const menuEl = () => {
  let hit: any = null;
  const walk = (n: any): void => {
    if (hit) return;
    if (n.nodeType === 1 && (reactProps(n) ?? {}).onMouseDown) { hit = n; return; }
    for (const c of n.childNodes ?? []) walk(c);
  };
  walk(container);
  return hit;
};
const menuItems = (el: any): number => (el.childNodes ?? []).filter((c: any) => c.nodeType === 1).length;

describe('NotesList: контролы записи по роли (S2c-F4d)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  // Дерево видно любой роли — проверка не про «пустой список», а про контролы записи
  it('читатель: нет «Удалить», «Новой папки» и draggable; дерево и «Открыть» — на месте', async () => {
    await mountList([projectWith('reader')], { withCallbacks: true });
    expect(hasText('Заметка'), 'заметка в дереве').toBe(true);
    expect(hasText('docs'), 'папка в дереве').toBe(true);
    // draggable не ставится ни строке заметки, ни строке папки
    expect(rowWith('Заметка', 'draggable'), 'строка заметки не draggable').toBeNull();
    expect(rowWith('docs', 'draggable'), 'строка папки не draggable').toBeNull();
    // hover-иконок записи нет (метки — точные, как в коде: «Удалить заметку», «В избранное»)
    await hoverOn('Заметка');
    expect(icon('Удалить заметку'), 'иконки удаления заметки нет').toBeNull();
    expect(icon('В избранное'), 'иконки избранного нет').toBeNull();
    await hoverOn('docs');
    expect(icon('Удалить папку'), 'иконки удаления папки нет').toBeNull();
    expect(icon('Переименовать/перенести папку'), 'иконки переименования папки нет').toBeNull();
    // меню заметки: только чтение
    await openCtx('Заметка');
    // «Открыть» — по подстроке: это и подпись ConfirmDialog (его текст обёрнут в span),
    // точное совпадение ловило бы разметку кита, а не гейт
    expect(hasText('Открыть'), 'чтение остаётся').toBe(true);
    expect(hasText('Удалить'), 'пункта «Удалить» нет').toBe(false);
    expect(hasText('Переместить в...'), 'пункта «Переместить в…» нет').toBe(false);
    expect(hasText('В избранное'), 'пункта «В избранное» нет').toBe(false);
    // меню папки: ни одной двери 'note.folder' и нет «Новой заметки» ('note.create')
    await openCtx('docs');
    expect(hasText('Новая папка'), 'пункта «Новая папка» нет').toBe(false);
    expect(hasText('Переименовать'), 'пункта «Переименовать» нет').toBe(false);
    expect(hasText('Удалить папку'), 'пункта «Удалить папку» нет').toBe(false);
    expect(hasText('Новая заметка'), 'пункта «Новая заметка» нет').toBe(false);
  });

  it('редактор: все контролы записи есть', async () => {
    await mountList([projectWith('editor')], { withCallbacks: true });
    const noteRow = rowWith('Заметка', 'draggable');
    expect(noteRow, 'строка заметки draggable').not.toBeNull();
    expect((reactProps(noteRow) as any).draggable, 'draggable=true').toBe(true);
    expect(rowWith('docs', 'draggable'), 'строка папки draggable').not.toBeNull();
    await hoverOn('Заметка');
    expect(icon('Удалить заметку'), 'иконка удаления заметки есть').not.toBeNull();
    expect(icon('В избранное'), 'иконка избранного есть').not.toBeNull();
    await hoverOn('docs');
    expect(icon('Удалить папку'), 'иконка удаления папки есть').not.toBeNull();
    expect(icon('Переименовать/перенести папку'), 'иконка переименования папки есть').not.toBeNull();
    await openCtx('Заметка');
    expect(hasText('Удалить'), 'пункт «Удалить» есть').toBe(true);
    expect(hasText('Переместить в...'), 'пункт «Переместить в…» есть').toBe(true);
    expect(hasText('В избранное'), 'пункт «В избранное» есть').toBe(true);
    await openCtx('docs');
    expect(hasText('Новая папка'), 'пункт «Новая папка» есть').toBe(true);
    expect(hasText('Переименовать'), 'пункт «Переименовать» есть').toBe(true);
    expect(hasText('Удалить папку'), 'пункт «Удалить папку» есть').toBe(true);
    expect(hasText('Новая заметка'), 'пункт «Новая заметка» есть').toBe(true);
  });

  it('владелец: как раньше', async () => {
    await mountList([projectWith('owner')]);
    expect(rowWith('Заметка', 'draggable'), 'строка заметки draggable').not.toBeNull();
    expect(rowWith('docs', 'draggable'), 'строка папки draggable').not.toBeNull();
    await openCtx('Заметка');
    expect(hasText('Удалить'), 'пункт «Удалить» есть').toBe(true);
    await openCtx('docs');
    expect(hasText('Новая папка'), 'пункт «Новая папка» есть').toBe(true);
    expect(hasText('Удалить папку'), 'пункт «Удалить папку» есть').toBe(true);
  });

  // Контроль: проект без myRole (обычный проект/старый бэк) — не «всегда прятать»
  it('проект без myRole: как раньше', async () => {
    await mountList([projectWith(undefined)]);
    expect(rowWith('Заметка', 'draggable'), 'строка заметки draggable').not.toBeNull();
    await openCtx('docs');
    expect(hasText('Новая папка'), 'пункт «Новая папка» есть').toBe(true);
  });

  // Контроль: кэш проектов не прогрет (проект не найден) — как раньше: бэк ответит сам
  it('проект не в списке: как раньше', async () => {
    await mountList([]);
    expect(rowWith('docs', 'draggable'), 'строка папки draggable').not.toBeNull();
    await openCtx('Заметка');
    expect(hasText('Удалить'), 'пункт «Удалить» есть').toBe(true);
  });

  // 'personal' — проекта нет по построению: все двери открыты, даже когда в кэше есть
  // проект-читатель с другим id
  it('personal: всё разрешено', async () => {
    await mountList([projectWith('reader')], { source: 'personal' });
    expect(rowWith('Заметка', 'draggable'), 'строка заметки draggable').not.toBeNull();
    await openCtx('Заметка');
    expect(hasText('Удалить'), 'пункт «Удалить» есть').toBe(true);
    await openCtx('docs');
    expect(hasText('Новая папка'), 'пункт «Новая папка» есть').toBe(true);
  });

  // Вызывающий без колбэков: пунктов создания заметки нет ни у какой роли — это
  // поведение родителя (S2c-F4h), а не ролевой гейт
  it('без колбэков родителя: «Новой заметки» нет и у редактора', async () => {
    await mountList([projectWith('editor')]);
    await openCtx('docs');
    expect(hasText('Новая заметка'), 'пункта «Новая заметка» нет').toBe(false);
    expect(hasText('Новая папка'), 'свои двери редактора при этом открыты').toBe(true);
  });

  // Ветка без sourceFilter (NotesPage): единственный путь, где есть контекстное меню шапки
  // источника (kind:'source'). Пункты :746/:747 загейтированы ролевыми дверями — с закрытой
  // дверью меню открывается пустым (0 узлов). Мутация «снять оба гейта» красит этот тест.
  it('без sourceFilter: читателю в меню шапки источника — 0 узлов', async () => {
    await mountList([projectWith('reader')], { noSourceFilter: true, withCallbacks: true });
    expect(hasText('Заметка'), 'заметка в дереве').toBe(true);
    expect(hasText('Проект'), 'шапка источника на месте').toBe(true);
    await openCtx('Проект');
    const menu = menuEl();
    expect(menu, 'меню шапки открыто').not.toBeNull();
    expect(menuItems(menu), 'закрытые двери: ни одного пункта').toBe(0);
    expect(hasText('Новая заметка'), 'пункта «Новая заметка» нет').toBe(false);
    expect(hasText('Новая папка'), 'пункта «Новая папка» нет').toBe(false);
  });

  it('без sourceFilter: редактору в меню шапки источника — «Новая заметка» и «Новая папка» есть', async () => {
    await mountList([projectWith('editor')], { noSourceFilter: true, withCallbacks: true });
    expect(hasText('Проект'), 'шапка источника на месте').toBe(true);
    await openCtx('Проект');
    expect(menuEl(), 'меню шапки открыто').not.toBeNull();
    expect(hasText('Новая заметка'), 'пункт «Новая заметка» есть').toBe(true);
    expect(hasText('Новая папка'), 'пункт «Новая папка» есть').toBe(true);
  });
});

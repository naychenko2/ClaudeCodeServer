// ProjectNotesPanel: контролы создания заметки по роли (S2c-F4h). Точка права —
// contentRights.ts (S2c-F4a); здесь — собственные точки записи панели: кнопка «Заметка»
// в шапке, проп onCreateInFolder в NotesList («+» на папке) и диалог создания, достижимый
// только из них (api.notes.create живёт в NewNoteDialog). Контролы самого списка
// загейтированы в S2c-F4d (NotesListRole.test.ts), здесь не проверяются. Закрытое ПРЯЧЕТ
// (0 узлов — ни кнопки, ни пустой строки шапки с ней), не отключено:
//   • читатель — в панели ни одного <button>: кнопки создания нет, onCreateInFolder не
//     передан (hover-иконки «Новая заметка в папке» на папке нет); список и папки — на месте;
//   • редактор и владелец — кнопка есть, проп передан («+» на папке при ховере);
//   • проект без myRole и проект не в кэше (не прогрет) — как раньше: бэк ответит сам.
// Мутация «убрать гейт note.create» (canWriteContent → true) красит тест читателя
// (кнопка и <button> в дереве), откат зелёный.
//
// Окружение — по образцу NotesListRole.test.ts (node + fakeDom + no-op мок signalr) и
// NotesPage.test.ts: панель берёт список через стор useNotes() — мок на leaf-модуле
// lib/notes (стор глобален и между тестами не прогрелся бы); папки — мок [].
// Проекты — мок api.projects.list + принудительный invalidateProjectsCache: кэш
// useAllProjects общий на процесс, без force второй тест получил бы список первого.
import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { ProjectNotesPanel } from '../ProjectNotesPanel';
import { api } from '../../../lib/api';
import { invalidateProjectsCache } from '../../projects/useAllProjects';
import type { NoteSummary, Project, ProjectRoleView } from '../../../types';
import { installFakeDom, uninstallFakeDom, mount, findNodeByAttr, reactProps, textNodes } from '../../../components/chat/__tests__/fakeDom';

// signalr: цепочка импортов кита (lib/notes) тянет модуль; в node реальное соединение
// и localStorage не нужны — все подписки no-op (как в NotesListRole.test.ts)
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

// Стор заметок — leaf lib/notes (кит реэкспортирует из того же модуля): список из мока,
// загрузка и папки — пусто (панель не должна ходить в сеть и греть глобальный стор)
const { notesMock } = vi.hoisted(() => ({ notesMock: [] as NoteSummary[] }));
vi.mock('../../../lib/notes', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/notes')>();
  return { ...orig, useNotes: () => notesMock, ensureNotesLoaded: async () => {}, useNoteFolders: () => [] };
});

// Заметка проекта 'p1' в папке 'docs' — из её пути buildTree строит и узел папки.
// Название намеренно не «Заметка»: текст кнопки создания — «Заметка», проверка
// «кнопки нет» не должна рифмоваться с названием заметки.
const NOTE: NoteSummary = {
  id: 'n1', title: 'Моя заметка', source: 'p1', sourceLabel: 'Проект', path: 'docs/Моя заметка.md',
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

// Смонтировать панель проекта 'p1' и дождаться загрузки проектов (useAllProjects →
// api.projects.list). projects — состав кэша для этого теста; [] — «проект не найден».
async function mountPanel(projects: Project[]) {
  notesMock.length = 0;
  notesMock.push(NOTE);
  installFakeDom();
  vi.spyOn(api.projects, 'list').mockResolvedValue(projects);
  invalidateProjectsCache();
  const r = mount(createElement(ProjectNotesPanel, { projectId: 'p1', onOpenFile: () => {} }));
  root = r.root; container = r.container;
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
}

const hasText = (t: string) => textNodes(container).some(n => n.includes(t));

// Кнопка создания — по точному title атрибуту (ui/Button отдаёт title на <button>,
// Button.tsx:119); текст «Заметка» для поиска не годится: он рифмуется с названием заметки
const createBtn = () => findNodeByAttr(container, 'title', 'Новая заметка проекта');

// Все <button> в дереве панели: у читателя их не должно быть вовсе — это и есть «0 узлов»
// (строка шапки с кнопкой при закрытой двери не рендерится целиком)
const countButtons = (): number => {
  let n = 0;
  const walk = (x: any): void => {
    if (x.nodeType === 1 && x.nodeName === 'BUTTON') n++;
    for (const c of x.childNodes ?? []) walk(c);
  };
  walk(container);
  return n;
};

// Найти строку по тексту и подняться до предка с нужным React-пропом (как в NotesListRole)
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

async function hoverOn(text: string) {
  const row = rowWith(text, 'onMouseEnter');
  expect(row, `строка «${text}» с onMouseEnter`).not.toBeNull();
  await act(async () => { (reactProps(row) as any).onMouseEnter(); });
}

// hover-иконки — по aria-label (ui/IconButton: aria-label = title, IconButton.tsx:90)
const icon = (label: string) => findNodeByAttr(container, 'aria-label', label);

describe('ProjectNotesPanel: создание заметки по роли (S2c-F4h)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  // Дерево видно любой роли — проверка не про «пустой список», а про контролы записи
  it('читатель: ни одного <button>; списка и папок не тронуто', async () => {
    await mountPanel([projectWith('reader')]);
    expect(hasText('Моя заметка'), 'заметка в дереве').toBe(true);
    expect(hasText('docs'), 'папка в дереве').toBe(true);
    expect(createBtn(), 'кнопки «Заметка» в шапке нет').toBeNull();
    expect(hasText('Заметка'), 'текста кнопки нет').toBe(false);
    expect(countButtons(), 'узлов <button> в панели нет').toBe(0);
    // onCreateInFolder не передан: даже при ховере на папке «+» создания нет (и своих
    // иконок папки тоже — их гейт в NotesList, S2c-F4d)
    await hoverOn('docs');
    expect(icon('Новая заметка в папке'), 'плюса на папке нет').toBeNull();
    expect(countButtons(), 'после ховера по-прежнему 0').toBe(0);
  });

  it('редактор: кнопка есть, onCreateInFolder передан («+» на папке)', async () => {
    await mountPanel([projectWith('editor')]);
    expect(createBtn(), 'кнопка «Заметка» есть').not.toBeNull();
    await hoverOn('docs');
    expect(icon('Новая заметка в папке'), 'плюс на папке есть').not.toBeNull();
  });

  it('владелец: как раньше', async () => {
    await mountPanel([projectWith('owner')]);
    expect(createBtn(), 'кнопка «Заметка» есть').not.toBeNull();
    await hoverOn('docs');
    expect(icon('Новая заметка в папке'), 'плюс на папке есть').not.toBeNull();
  });

  // Контроль: проект без myRole (обычный проект/старый бэк) — не «всегда прятать»
  it('проект без myRole: как раньше', async () => {
    await mountPanel([projectWith(undefined)]);
    expect(createBtn(), 'кнопка «Заметка» есть').not.toBeNull();
  });

  // Контроль: кэш проектов не прогрет (проект не найден) — как раньше: бэк ответит сам
  it('проект не в списке: как раньше', async () => {
    await mountPanel([]);
    expect(createBtn(), 'кнопка «Заметка» есть').not.toBeNull();
  });
});

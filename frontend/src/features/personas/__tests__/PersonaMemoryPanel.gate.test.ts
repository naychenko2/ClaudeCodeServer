// W3-F1h: гейт по роли во вкладке памяти персоны (матрица §2 разреза
// docs/research/shared-projects-personas-cut-2026-11.md, §3.4). Фронт не обещает того,
// что бэк откажет участнику: читатель — без добавления/правки/удаления/подтверждения
// (memoryWrite — WriteContent) и без «в заметку» (memoryNote — Configure); редактор —
// запись есть, «в заметку» нет; владелец и обычный проект (роли нет) — всё, как раньше.
//
// Окружение node (без jsdom), SSR renderToStaticMarkup — образец ProjectPersonasPanel.gate.test.ts.
// Effects в SSR не идут, память не грузится — карточные кнопки в рендере самой панели
// не появились бы, поэтому проверка двухчастная:
// 1) связка — делегирующий мок общего MemoryPanel: запоминает обработчики, которые
//    PersonaMemoryPanel пробросил по роли, и рендерит НАСТОЯЩУЮ панель — карточка
//    добавления («Добавить вручную»/«Запомнить») ловится в настоящем HTML;
// 2) общие компоненты — настоящая панель с фикстурной записью (обычной и pending):
//    нет обработчика — нет контролов («Редактировать», «Забыть», «Превратить в заметку»,
//    «Подтвердить») и нет пустой колонки действий на мобиле.
// Имя файла .test.ts, а не .tsx: vitest.config.ts включает только src/**/*.test.ts,
// все gate-тесты проекта — .ts (JSX не нужен, createElement).

import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { ReactElement } from 'react';
import { PersonaMemoryPanel } from '../PersonaMemoryPanel';
import { MemoryEntryCard } from '../../memory/MemoryEntryCard';
import { C } from '../../../lib/design';
import type { MemoryPanelProps } from '../../memory/MemoryPanel';
import type { MemoryEntryView, MemoryTypeMeta } from '../../memory/memoryTypes';
import type { Persona, PersonaMemoryType, ProjectRoleView } from '../../../types';

type PanelProps = MemoryPanelProps<PersonaMemoryType>;
type PanelFn = (props: PanelProps) => ReactElement;

// Делегирующий мок: remembers пробросанные обработчики (тестируемая связка) и рендерит
// настоящую MemoryPanel. real — та же функция для второй части теста.
const captured = vi.hoisted(() => ({
  props: null as Record<string, unknown> | null,
  real: null as PanelFn | null,
}));
vi.mock('../../memory/MemoryPanel', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../memory/MemoryPanel')>();
  const real = orig.MemoryPanel as unknown as PanelFn;
  captured.real = real;
  const spy: PanelFn = (props) => { captured.props = { ...props }; return real(props); };
  return { ...orig, MemoryPanel: spy as unknown as typeof orig.MemoryPanel };
});

// Проектная персона проекта 'proj1' (свой заголовок панели не нужен для проверки)
const PERSONA: Persona = {
  id: 'per-1', ownerId: 'o1', name: 'Светлана', role: 'Дизайнер', handle: 'designer',
  scope: 'project', projectId: 'proj1', memoryEnabled: false,
} as Persona;

// Та же разметка типов, что в панели (токены из design.ts, без сырых hex)
const TYPE_META: Record<PersonaMemoryType, MemoryTypeMeta> = {
  semantic: { title: 'Факты', hint: 'устойчивые сведения', color: C.accent, softBg: C.accentSoft },
  episodic: { title: 'Эпизоды', hint: 'что было в разговорах', color: C.info, softBg: C.infoBg },
  procedural: { title: 'Приёмы', hint: 'привычные способы', color: C.success, softBg: C.successBg },
};
const TYPE_ORDER: PersonaMemoryType[] = ['semantic', 'episodic', 'procedural'];

// Ручная запись: «Забыть» (не «Отклонить»), без pending — без кнопки подтверждения
const ENTRY: MemoryEntryView<PersonaMemoryType> = {
  id: 'e1', type: 'semantic', text: 'Тестовая запись', tags: [], salience: 0.5,
  createdAt: '2026-11-01T00:00:00Z', origin: 'manual',
};

// Pending-запись (предложено autolearn, ждёт решения): «Подтвердить» живёт по onConfirm —
// панель отдаёт его карточке только при открытом обработчике И pending
// (провязка onConfirm={e.pending && onConfirm ? … : undefined} в MemoryPanel).
const PENDING_ENTRY: MemoryEntryView<PersonaMemoryType> = {
  id: 'e2', type: 'episodic', text: 'Предложенная запись', tags: [], salience: 0.6,
  createdAt: '2026-11-02T00:00:00Z', origin: 'auto', originDetail: 'из хода', pending: true,
};

function countOf(html: string, name: string): number {
  return html.split(name).length - 1;
}

const ADD_CARD = 'Добавить вручную';
const ADD_BTN = 'Запомнить';
const EDIT_BTN = 'title="Редактировать"';
const REMOVE_BTN = 'title="Забыть"';
const NOTE_BTN = 'title="Превратить в заметку"';
const CONFIRM_BTN = 'title="Подтвердить — запомнить"';

describe('PersonaMemoryPanel: действия записи по роли (W3-F1h)', () => {
  function renderWithRole(role?: ProjectRoleView | null): string {
    captured.props = null;
    return renderToStaticMarkup(createElement(PersonaMemoryPanel, { persona: PERSONA, isMobile: false, role }));
  }

  it('читатель: нет добавления/правки/удаления/подтверждения, нет «в заметку»', () => {
    const html = renderWithRole('reader');
    const props = captured.props!;
    expect(typeof props.onAdd, 'добавление (memoryWrite) читателю').toBe('undefined');
    expect(typeof props.onEdit, 'правка (memoryWrite) читателю').toBe('undefined');
    expect(typeof props.onRemove, 'удаление (memoryWrite) читателю').toBe('undefined');
    expect(typeof props.onConfirm, 'подтверждение (memoryWrite) читателю').toBe('undefined');
    expect(typeof props.onToNote, '«в заметку» (memoryNote) читателю').toBe('undefined');
    expect(countOf(html, ADD_CARD), 'карточка добавления в HTML читателю').toBe(0);
  });

  it('редактор: запись есть; «в заметку» нет', () => {
    const html = renderWithRole('editor');
    const props = captured.props!;
    expect(typeof props.onAdd, 'добавление редактору (memoryWrite открыт)').toBe('function');
    expect(typeof props.onEdit, 'правка редактору (memoryWrite открыт)').toBe('function');
    expect(typeof props.onRemove, 'удаление редактору (memoryWrite открыт)').toBe('function');
    expect(typeof props.onConfirm, 'подтверждение редактору (memoryWrite открыт)').toBe('function');
    expect(typeof props.onToNote, '«в заметку» (memoryNote — Configure) редактору').toBe('undefined');
    expect(countOf(html, ADD_CARD), 'карточка добавления редактору').toBe(1);
  });

  it('владелец: всё, как раньше', () => {
    const html = renderWithRole('owner');
    const props = captured.props!;
    expect(typeof props.onAdd, 'добавление владельцу').toBe('function');
    expect(typeof props.onEdit, 'правка владельцу').toBe('function');
    expect(typeof props.onRemove, 'удаление владельцу').toBe('function');
    expect(typeof props.onConfirm, 'подтверждение владельцу').toBe('function');
    expect(typeof props.onToNote, '«в заметку» владельцу').toBe('function');
    expect(countOf(html, ADD_CARD), 'карточка добавления владельцу').toBe(1);
  });

  // «нет роли» = обычный проект / старый бэк — матрица §2 читает её как «все»
  it('без роли (обычный проект / старый бэк) — всё открыто', () => {
    const html = renderWithRole(null);
    const props = captured.props!;
    expect(typeof props.onAdd, 'добавление без роли').toBe('function');
    expect(typeof props.onEdit, 'правка без роли').toBe('function');
    expect(typeof props.onRemove, 'удаление без роли').toBe('function');
    expect(typeof props.onConfirm, 'подтверждение без роли').toBe('function');
    expect(typeof props.onToNote, '«в заметку» без роли').toBe('function');
    expect(countOf(html, ADD_CARD), 'карточка добавления без роли').toBe(1);
  });

  // П4 хвостов ревью: проп role ОПУЩЕН (старые вызывающие его не передают) — путь
  // default-параметра PersonaMemoryPanel.tsx:40 (role = null) обязан давать «как владелец».
  it('роль не передана пропом: default-путь — как владелец', () => {
    captured.props = null;
    const html = renderToStaticMarkup(createElement(PersonaMemoryPanel, { persona: PERSONA, isMobile: false }));
    const props = captured.props!;
    expect(typeof props.onAdd, 'добавление без пропа role').toBe('function');
    expect(typeof props.onEdit, 'правка без пропа role').toBe('function');
    expect(typeof props.onRemove, 'удаление без пропа role').toBe('function');
    expect(typeof props.onConfirm, 'подтверждение без пропа role').toBe('function');
    expect(typeof props.onToNote, '«в заметку» без пропа role').toBe('function');
    expect(countOf(html, ADD_CARD), 'карточка добавления без пропа role').toBe(1);
  });
});

describe('Общая панель и карточка записи: нет обработчика — нет контролов (W3-F1h)', () => {
  function renderRealPanel(withWrite: boolean, withNote: boolean, entry: MemoryEntryView<PersonaMemoryType> = ENTRY): string {
    return renderToStaticMarkup(createElement(captured.real!, {
      entries: [entry],
      typeMeta: TYPE_META,
      typeOrder: TYPE_ORDER,
      addHint: '', addPlaceholder: '',
      onAdd: withWrite ? async () => undefined : undefined,
      onEdit: withWrite ? async () => undefined : undefined,
      onRemove: withWrite ? async () => undefined : undefined,
      onConfirm: withWrite ? async () => undefined : undefined,
      onToNote: withNote ? () => undefined : undefined,
    } satisfies PanelProps));
  }

  it('с обработчиками: карточка добавления и кнопки записи на месте', () => {
    const html = renderRealPanel(true, true);
    expect(countOf(html, ADD_CARD), 'карточка добавления с onAdd').toBe(1);
    expect(countOf(html, ADD_BTN), 'кнопка «Запомнить» с onAdd').toBe(1);
    expect(countOf(html, EDIT_BTN), 'кнопка правки с onEdit').toBe(1);
    expect(countOf(html, REMOVE_BTN), 'кнопка удаления с onRemove').toBe(1);
    expect(countOf(html, NOTE_BTN), '«в заметку» с onToNote').toBe(1);
  });

  it('без обработчиков: нет карточки добавления, нет кнопок записи, нет пустой колонки действий', () => {
    const html = renderRealPanel(false, false);
    expect(countOf(html, ADD_CARD), 'карточка добавления без onAdd').toBe(0);
    expect(countOf(html, ADD_BTN), 'кнопка «Запомнить» без onAdd').toBe(0);
    expect(countOf(html, EDIT_BTN), 'кнопка правки без onEdit').toBe(0);
    expect(countOf(html, REMOVE_BTN), 'кнопка удаления без onRemove').toBe(0);
    expect(countOf(html, NOTE_BTN), '«в заметку» без onToNote').toBe(0);
  });

  // П3 хвостов ревью: «Подтвердить» у pending-записи проверяется в разметке, а не только
  // по props.onConfirm. Путь читателя = настоящая панель с теми же пропсами, что панель
  // отдаёт читателю (обработчиков права нет): карточка получает onConfirm=undefined —
  // кнопки в HTML нет. Положительный кейс с onConfirm держит сигнал: без него и
  // отрицательная проверка была бы пустой.
  it('pending-запись без onConfirm (путь читателя): «Подтвердить» в разметке нет', () => {
    const html = renderRealPanel(false, false, PENDING_ENTRY);
    expect(countOf(html, CONFIRM_BTN), '«Подтвердить» у pending-записи без onConfirm').toBe(0);
  });

  it('pending-запись с onConfirm: «Подтвердить» в разметке на месте', () => {
    const html = renderRealPanel(true, false, PENDING_ENTRY);
    expect(countOf(html, CONFIRM_BTN), '«Подтвердить» у pending-записи с onConfirm').toBe(1);
  });

  // Только карточка записи (без панели): скрытие колонки действий — по всем четырём
  // обработчикам сразу; при любом открытом — колонка остаётся.
  function renderCard(open: { remove?: boolean; edit?: boolean; note?: boolean }): string {
    return renderToStaticMarkup(createElement(MemoryEntryCard, {
      entry: ENTRY, typeMeta: TYPE_META.semantic, removing: false,
      onRemove: open.remove ? () => undefined : undefined,
      onStartEdit: open.edit ? () => undefined : undefined,
      onToNote: open.note ? () => undefined : undefined,
      editing: false, editText: '', onEditTextChange: () => undefined,
      onSaveEdit: async () => undefined, onCancelEdit: () => undefined, savingEdit: false,
    }));
  }

  it('карточка без действий: колонки действий нет вовсе', () => {
    const html = renderCard({});
    expect(countOf(html, EDIT_BTN), 'кнопка правки без onStartEdit').toBe(0);
    expect(countOf(html, REMOVE_BTN), 'кнопка удаления без onRemove').toBe(0);
    expect(countOf(html, NOTE_BTN), '«в заметку» без onToNote').toBe(0);
  });

  it('карточка с одним открытым действием: колонка есть, остальные кнопки нет', () => {
    const html = renderCard({ remove: true });
    expect(countOf(html, REMOVE_BTN), 'кнопка удаления с onRemove').toBe(1);
    expect(countOf(html, EDIT_BTN), 'кнопка правки без onStartEdit').toBe(0);
    expect(countOf(html, NOTE_BTN), '«в заметку» без onToNote').toBe(0);
  });
});

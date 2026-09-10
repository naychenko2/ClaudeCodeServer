// Публичный API фичи «Заметки».
//
// Включённость подсистемы приходит из /api/auth/me (поле subsystems) и
// раздаётся хуком useSubsystem('notes') из lib/subsystems. Пока стор не
// подключён к me, useSubsystem возвращает false (fail-closed по умолчанию):
// вызывающий код, который не проверил включённость сам, просто ничего не
// делает. Точки расширения (несколько альтернативных реализаций) НЕ заводим:
// каждый вклад ниже имеет одного участника без второго кандидата, поэтому
// «точку с одним пунктом» делать не нужно — это была бы лишняя абстракция.
// Включённость проверяет сам вызывающий код (useSubsystem('notes') на уровне
// компонента или React.lazy), а плоский named-export ниже — стабильный
// публичный адрес фичи, чтобы рефакторинг внутренних модулей не ломал
// потребителей.
//
// Внешние потребители импортируют ТОЛЬКО отсюда. Прямой импорт из
// ./saveToNote, ./shared, ./NoteView и т.д. — нарушение правила фичи
// (легко отлавливается по grep'у). Комментарии у каждого символа в
// реэкспорте ниже фиксируют, где он вкраплён, — это индекс мест.

import { openNoteById, saveChatNote } from './saveToNote';
import { IconNotes } from './shared';
import { NewNoteDialog } from './NewNoteDialog';
import { DocCommentedMarkdown } from './DocComments';
import { NoteConnections } from './NoteConnections';
import { NoteView } from './NoteView';
import { NoteEditor } from './NoteEditor';
import { ProjectNotesPanel } from './ProjectNotesPanel';
import { NotesPage } from './NotesPage';

// Плоские реэкспорты — основной API для внешних потребителей. Здесь же
// перечислены все «наружу», чтобы grep по этому файлу был источником истины
// о публичной поверхности. Комментарии у каждого символа фиксируют, где он
// вкраплён, — индекс мест на случай рефакторинга внутренних модулей.
export {
  // Действия с заметками (6 вкраплений: ChatItemView, ChatHeaderBar,
  // PlanSection, PlanReviewView, GlobalSearch, lib/ai/actions).
  openNoteById,
  saveChatNote,
  // Иконка-заглушка (4 вкрапления: ChatItemView, PlanSection, PlanReviewView, FileExplorer).
  IconNotes,
  // Диалог создания (3 вкрапления: FileExplorer, home/NotesWidget, home/QuickActions).
  NewNoteDialog,
  // Markdown с комментариями документов (1 вкрапление: FileViewer).
  DocCommentedMarkdown,
  // Граф связей заметки (1 вкрапление: FileViewer).
  NoteConnections,
  // Просмотр заметки (1 вкрапление: FileViewer).
  NoteView,
  // Редактор заметки (1 вкрапление: FileViewer, lazy).
  NoteEditor,
  // Панель заметок проекта (1 вкрапление: WorkspacePage).
  ProjectNotesPanel,
  // Страница «Заметки» в хабе (1 вкрапление: App.tsx).
  NotesPage,
};

// Манифест фичи: метаданные для тумблера, документации, диагностики.
// Ключ подсистемы здесь обязан совпадать с ключом в lib/subsystems.SUBSYSTEMS
// и с бэковым реестром подсистем.
export const manifest = {
  key: 'notes',
  title: 'Заметки',
  description:
    'Obsidian-совместимые заметки с обратными ссылками и графом. ' +
    'Включает раздел «Заметки» в хабе, панель заметок проекта, ' +
    'кнопки «В заметку» в чате и DocComments во вьюере файлов.',
} as const;

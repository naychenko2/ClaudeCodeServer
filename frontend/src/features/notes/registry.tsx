// Регистрация подсистемы «Заметки»: манифест (раздел хаба + вклады слотов).
//
// Это единственный файл фичи, который знает про реестр. Каркас (App, общие
// компоненты, lib) о существовании Notes не знает: он читает слоты и раздел из
// реестра. Удалить подсистему = удалить папку features/notes и одну строку в
// lib/subsystems/registry.ts.
//
// Тяжёлые части (страница раздела, редактор заметок) остаются ленивыми.

import { lazy } from 'react';
import { registerSubsystem } from '../../lib/subsystems/registryCore';
import type {
  SubsystemManifest,
  FileViewerNoteViewCtx, FileViewerNoteEditorCtx, FileViewerNoteConnectionsCtx, FileViewerDocCommentsCtx,
  FileExplorerFolderIconCtx, FileExplorerNoteDialogCtx,
  WorkspacePanelNotesCtx, HomeWidgetNotesCtx, QuickActionNoteCtx,
  ChatItemSaveNoteCtx, ChatHeaderSummaryCtx, ChatHeaderMenuItemCtx,
  PlanChipCtx, PlanButtonCtx, MarkdownEditorCtx,
} from '../../lib/subsystems/registryCore';

import { NoteView } from './NoteView';
import { NoteConnections } from './NoteConnections';
import { DocCommentedMarkdown } from './DocComments';
import { NewNoteDialog } from './NewNoteDialog';
import { ProjectNotesPanel } from './ProjectNotesPanel';
import { NotesWidget } from './NotesWidget';
import { QuickNoteAction } from './QuickNoteAction';
import { ChatSaveNoteAction } from './ChatSaveNoteAction';
import { SessionSummaryAction, ChatSummaryMenuItem } from './SessionSummaryAction';
import { PlanSaveChip, PlanSaveButton } from './PlanNoteActions';
import { IconNotes } from './shared';
import { openNoteById } from './saveToNote';

const NotesPage = lazy(() => import('./NotesPage').then(m => ({ default: m.NotesPage })));
const NoteEditor = lazy(() => import('./NoteEditor').then(m => ({ default: m.NoteEditor })));

const manifest: SubsystemManifest = {
  key: 'notes',
  title: 'Заметки',
  icon: <IconNotes size={18} />,
  order: 35,
  tab: { component: NotesPage },
  slots: {
    // --- Вьювер файлов: рендер vault-заметки, редактора, связей и комментариев ---
    'file-viewer-panel': [
      {
        name: 'note-view', order: 10,
        render: (ctx: FileViewerNoteViewCtx) => (
          <NoteView
            key={ctx.noteId}
            noteId={ctx.noteId}
            existingTitles={ctx.existingTitles}
            onWikilink={ctx.onWikilink}
            onSelectNote={ctx.onSelectNote}
            onDeleted={ctx.onDeleted}
            isMobile={ctx.isMobile}
            onBack={ctx.onBack}
            onOpenFileRef={ctx.onOpenFileRef}
            extraToolbar={ctx.extraToolbar}
          />
        ),
      },
      {
        name: 'note-editor', order: 20,
        render: (ctx: FileViewerNoteEditorCtx) => (
          <NoteEditor key={ctx.filePath} value={ctx.value} onChange={ctx.onChange} onWikilink={ctx.onWikilink} fill />
        ),
      },
      {
        name: 'note-connections', order: 30,
        render: (ctx: FileViewerNoteConnectionsCtx) => (
          <NoteConnections note={ctx.note} onOpenNote={ctx.onOpenNote} onWikilink={ctx.onWikilink} />
        ),
      },
      {
        name: 'doc-commented-markdown', order: 40,
        render: (ctx: FileViewerDocCommentsCtx) => (
          <DocCommentedMarkdown
            scope={ctx.scope} docPath={ctx.docPath} content={ctx.content} isMobile={ctx.isMobile}
            onCounts={ctx.onCounts} panelTarget={ctx.panelTarget} deferPanel
            viewer={{ onDocLink: ctx.onDocLink, resolveImageSrc: ctx.resolveImageSrc }}
          />
        ),
      },
    ],

    // --- Проводник файлов: иконка папки vault и диалог новой заметки ---
    'file-explorer-action': [
      { name: 'folder-icon', order: 10, render: (ctx: FileExplorerFolderIconCtx) => <IconNotes size={ctx.size} /> },
      {
        name: 'new-note-dialog', order: 20,
        render: (ctx: FileExplorerNoteDialogCtx) => (
          <NewNoteDialog defaults={ctx.defaults} onClose={ctx.onClose} onCreated={ctx.onCreated} />
        ),
      },
    ],

    // --- Панель воркспейса «Заметки проекта» ---
    'workspace-panel': [
      {
        name: 'project-notes', order: 10,
        render: (ctx: WorkspacePanelNotesCtx) => (
          <ProjectNotesPanel projectId={ctx.projectId} activeFilePath={ctx.activeFilePath} onOpenFile={ctx.onOpenFile} />
        ),
      },
    ],

    // --- Дашборд: виджет заметок и кнопка быстрого создания ---
    'home-widget': [
      { name: 'notes-widget', order: 10, render: (ctx: HomeWidgetNotesCtx) => <NotesWidget onHubTab={ctx.onHubTab} /> },
    ],
    'quick-action': [
      { name: 'new-note', order: 10, render: (ctx: QuickActionNoteCtx) => <QuickNoteAction ActionButton={ctx.ActionButton} /> },
    ],

    // --- Лента чата: сохранение ответа в заметку и карточка изменённого .md-заметки ---
    'chat-item-action': [
      {
        name: 'save-note', order: 10,
        render: (ctx: ChatItemSaveNoteCtx) => (
          <ChatSaveNoteAction text={ctx.text} projectId={ctx.projectId} online={ctx.online} />
        ),
      },
      {
        name: 'file-changed', order: 20,
        action: {
          match: (path: string) => /(^|\/)notes\/[^/]*\.md$/i.test(path),
          open: (path: string) => {
            const title = path.split(/[\\/]/).pop()!.replace(/\.md$/i, '');
            sessionStorage.setItem('cc_pending_note_title', title);
            window.dispatchEvent(new Event('cc-open-note'));
          },
          icon: <IconNotes size={14} />,
        },
      },
    ],

    // --- Шапка чата: «Итог сессии в заметку» (невидимый слушатель + пункт меню) ---
    'chat-header-action': [
      {
        name: 'session-summary', order: 10,
        render: (ctx: ChatHeaderSummaryCtx) => (
          <SessionSummaryAction session={ctx.session} hasMessages={ctx.hasMessages} online={ctx.online} />
        ),
      },
      {
        name: 'summary-menu-item', order: 20,
        render: (ctx: ChatHeaderMenuItemCtx) => <ChatSummaryMenuItem run={ctx.run} />,
      },
    ],

    // --- Markdown-редактор в общих формах (описание/результат задачи) ---
    'markdown-editor': [
      {
        name: 'note-editor', order: 10,
        render: (ctx: MarkdownEditorCtx) => (
          <NoteEditor value={ctx.value} onChange={ctx.onChange} placeholder={ctx.placeholder} minHeight={ctx.minHeight} />
        ),
      },
    ],

    // --- План: заметка из плана (чип навигатора и кнопка карточки согласования) ---
    'plan-action': [
      { name: 'plan-chip', order: 10, render: (ctx: PlanChipCtx) => <PlanSaveChip plan={ctx.plan} projectId={ctx.projectId} /> },
      { name: 'plan-button', order: 20, render: (ctx: PlanButtonCtx) => <PlanSaveButton plan={ctx.plan} online={ctx.online} /> },
    ],

    // --- Единый поиск: открытие найденной заметки ---
    'global-search-provider': [
      { name: 'note', order: 10, action: { open: (id: string) => openNoteById(id) } },
    ],

    // --- Действия ИИ: открытие заметки (утренний бриф и пр.) ---
    'ai-action': [
      { name: 'note', order: 10, action: { openNote: (id: string) => openNoteById(id) } },
    ],
  },
};

registerSubsystem(manifest);

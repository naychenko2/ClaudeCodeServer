// Runtime-кит оболочки для ВНУТРЕННИХ подсистем (module-federation remote).
//
// В отличие от design-kit (внешний контракт, только leaf-файлы), кит не
// версионируется: все подсистемы живут в одном репозитории и выкатываются
// одной сборкой. Surface намеренно широк — он покрывает всё, что подсистема
// потребляет из каркаса, чтобы не тащить вторую копию модульного состояния.
//
// Потребитель: `import { C, api, useNotes } from 'aihome_shell/kit'`.
// В dev/prod MF-рантайм модуля резолвит это через remoteEntry.js хоста;
// в хостовой сборке (tsc/vitest/vite) — через алиас на этот файл.

import { lazy } from 'react';

// ─── design ──────────────────────────────────────────────────────────────────
export { FONT, FS, C, R, SP, SHADOW, ISLAND, Z, GROUP_COLORS, CHAT_MAX_W, TB, CONTENT_MAX_W, MODAL_W, FAB_CLEAR_PAD } from '../design';

// ─── api ─────────────────────────────────────────────────────────────────────
export { api } from '../api';

// ─── signalr (события проекта) ───────────────────────────────────────────────
export { onFilesChanged } from '../signalr';

// ─── notes (стор + hooks) ─────────────────────────────────────────────────────
export {
  useNotes, useNoteFolders, useNotesVersion, ensureNotesLoaded, existingTitleSet, bumpNotes,
  isFavorite, toggleFavorite, withFavoriteTag, withoutFavoriteTag, FAVORITE_TAG,
} from '../notes';

// ─── notesOffline ────────────────────────────────────────────────────────────
export {
  getNoteForView, saveNoteOffline, createNoteOffline, deleteNoteOffline, offlineResolve,
} from '../notesOffline';

// ─── wiki (LLM-wiki: стор + операции) ────────────────────────────────────────
// Единственный стор — хостовый: MF-модуль «Заметки» берёт стор вики, флаги и WikiError
// через кит — своя копия относительных импортов дублировала бы состояние стора и давала
// отдельный экземпляр WikiError (QA P1, дефект B1).
export {
  parseSectionKey, groupSectionsByKind, WikiError,
  ensureWikiLoaded, loadWikiTree, loadWikiCatalog, loadWikiPage,
  getWikiTreeSnapshot, initializeWiki, adoptWikiPage, deleteWikiSection,
  listWikiPackages, decideWikiPackage, acceptAllWikiPackage,
  loadWikiJournal, retractWikiSource, getWikiHealth, runWikiLint,
  analyzeWikiChat, loadWikiChatAnalyzeStatus, loadWikiChats, setWikiChatMark, ingestWikiNote, transferWikiNote,
  adoptWikiPageMine,
  useWikiTree, useWikiCatalog, useWikiPage, useWikiPageGone,
  requestOpenWikiPage, consumeOpenWikiPage, useWikiOpenRequest,
  loadWikiSettings, useWikiSettings, putWikiNightly, runWikiNightlyNow, putWikiAutoApply, resumeWikiApplyClass,
  previewWikiPurge, runWikiPurge,
  wikiAuthorshipOf, editWikiPage, wikiFootnoteLabel, loadWikiSourceCard,
  restoreWikiPage, wikiOverridesOf, wikiHasReplacement, normWikiStatement, wikiActiveLines, useWikiReplacementDot,
} from '../wiki';
export type {
  WikiSectionDto, WikiPageInfoDto, WikiPackageDto, WikiPackageOperationDto,
  WikiApplyOutcomeDto, WikiDecisionDto, WikiJournalDto, WikiJournalEntryDto,
  WikiRetractDto, WikiChatAnalyzeOk, WikiNoteIngestDto, WikiNoteTransferDto, WikiOpenRequest,
  WikiChatTriage, WikiChatMark,
  WikiSettingsSnapshot, WikiApplyClassDto, WikiNightlySettings, WikiLastNight, WikiPutNightlyResult,
  WikiNightlyPatch, WikiNightlyOutcome,
  WikiAuthorship, WikiPageInfoAuthoredDto, WikiCatalogDto, WikiSourceCardInfo, WikiPageEditableDto, WikiConflictPage, WikiFootnoteDto,
  WikiOverrideDto, WikiOverrideOp, WikiPageOverriddenDto, WikiOverrodeKind, WikiJournalEntryOverrideDto,
  WikiAdoptMineResult,
  WikiPurgeDto, WikiPurgePageDto,
} from '../wiki';

// ─── subsystems ──────────────────────────────────────────────────────────────
export { useSubsystem, isSubsystemEnabled } from '../subsystems';

// ─── subsystems/registryCore ─────────────────────────────────────────────────
export { registerSubsystem, REVEAL_PANEL_EVENT, revealWorkspacePanel, revealContextPanel, SLOT_CONTEXT_KIND } from '../subsystems/registryCore';
export type { SubsystemManifest, RevealPanelDetail, RevealPanelOptions } from '../subsystems/registryCore';

// ─── chatContext (ADR-023): контракты вида контекста хода ────────────────────
export type {
  ChatContextDto, ChatContextItem, ChatContextPrimary, ChatContextRef, ContextKindApi, ContextKindCtx, KindState,
  ContextAction, ActionKind, LaunchParam, ExecutorListModel, ActionResult, ContextReturn, ActionPreset, ActionRun, ActionQuote, LaunchRequest, LaunchHandle,
} from '../chatContext/types';

export {
  useChatContext, getChatContextState, refreshChatContext, setPrimary, attachRef, detachRef, clearContext, releasePrimary, undoReleasePrimary, useReleaseOffer,
} from '../chatContext/store';
export { objectKey, pickDefaultAction, presetAction } from '../chatContext/actionMemory';
export { thumbOf } from '../chatContext/thumbs';
export { refOf, rolesFor } from '../chatContext/fill';
export type { ContextCandidate } from '../chatContext/fill';
export type { ContextRole, ContextUpload, ContextNote, ContextMenuItem } from '../chatContext/types';
export type {
  FormatModel, FormatEditModel, MediaFormatState, MediaFormatWish, MediaFormatCaps, FormatTierOption, FormatTierKey,
  ResolvedFormat, FormatMode, CropAnchor, FramesAnchor, FormatFramesModel, FormatLockAsk,
} from '../chatContext/formatTypes';
export { FORMAT_TEXT, TIER_NAME, generateChip, priceLabel, sizeLabel } from '../chatContext/formatText';
export { notifyKindChanged } from '../chatContext/actionRun';
export { ReportedError } from '../chatContext/errors';
export { etaTicker, etaFraction } from '../chatContext/etaProgress';
export type { EtaState } from '../chatContext/etaProgress';
export { setContextReturn, useContextReturn, clearContextReturn } from '../chatContext/contextReturn';

// ─── genPanelDismissed ───────────────────────────────────────────────────────
// Автооткрытие панели генерации по выбору картинки/звука, пока человек не закрыл
// её в этом чате (ADR-021 §3)
export { autoRevealGenerationPanel, isGenPanelKey, markGenPanelDismissed } from '../genPanelDismissed';

// ─── genPanelFollow / genDrafts / genPanelOpen ───────────────────────────────
// Панель генерации следует за выбором: клик по карточке, подсказка выбора агентом,
// черновики полей по ключу элемента
export { followSelection, isCardPick } from '../genPanelFollow';
export { noteGenDraft, clearGenDraft, useGenDraft } from '../genDrafts';
export { followPeeked } from '../genPanelOpen';

// ─── offline ─────────────────────────────────────────────────────────────────
// request и readStoredToken — низкоуровневый HTTP редактора картинок: его api.ts
// ходит по своим маршрутам в обход фасада api
export { OfflineError, RequestTimeoutError, request, readStoredToken } from '../offline';
// NoteConflictError — единый экземпляр класса для хоста (бросает) и remote «Заметок» (instanceof)
export { NoteConflictError } from '../offline';
// типы тела 409 (контракт К8)
export type { ConflictBody, FileConflictAuthor, FileConflictSource } from '../offline';

// ─── featureFlags ────────────────────────────────────────────────────────────
export { FLAGS, useFeature, getFlag } from '../featureFlags';

// ─── defaultPersona ──────────────────────────────────────────────────────────
export { useMe } from '../defaultPersona';

// ─── useSphereOf ─────────────────────────────────────────────────────────────
// Через кит, а не относительным импортом: lib/useSphereOf → useAllProjects → lib/api тянул
// в remote вторую копию lib/offline (стор ядра)
export { useSphereOf } from '../useSphereOf';
export { useAllProjects } from '../../features/projects/useAllProjects';

// ─── toast ───────────────────────────────────────────────────────────────────
export { showToast } from '../toast';

// ─── personas ────────────────────────────────────────────────────────────────
export { ensurePersonasLoaded, usePersonas, personaLabel } from '../personas';

// ─── breakpoints ─────────────────────────────────────────────────────────────
export { useIsMobile, useWindowWidth, MOBILE_MAX, TABLET_MAX, TABLET_WIDE_MIN } from '../breakpoints';

// ─── noAutofill ──────────────────────────────────────────────────────────────
export { NO_AUTOFILL } from '../noAutofill';

// ─── ai/busy ─────────────────────────────────────────────────────────────────
export { beginAiBusy, endAiBusy } from '../ai/busy';

// ─── ai/startChat ────────────────────────────────────────────────────────────
export { startChatFromPanel } from '../ai/startChat';

// ─── ai/annotationsPrompt ────────────────────────────────────────────────────
export { docAnnotationsPrompt, ANNOTATIONS_TOOL_KEY } from '../ai/annotationsPrompt';

// ─── aiJobStore ─────────────────────────────────────────────────────────────
export { useAiJob, runAiJob, patchAiJobResult, resetAiJob } from '../aiJobStore';

// ─── nav ─────────────────────────────────────────────────────────────────────
export { parseHash, navPush, navReplace, getNav, NAV_CHANGE_EVENT } from '../nav';
export type { NavSnapshot } from '../nav';

// ─── tasks ───────────────────────────────────────────────────────────────────
export { projectColor, useTasks, ensureTasksLoaded, openTaskInSection } from '../tasks';

// ─── themeMode ───────────────────────────────────────────────────────────────
export { getEffectiveTheme, subscribeThemeMode, useThemeMode } from '../themeMode';

// ─── gitFormat ───────────────────────────────────────────────────────────────
// Алиас: имя relTime в ките уже занято реализацией из features/home/WidgetCard
// (у неё отрицательная разница зажата в ноль). Сведение двух реализаций — вне
// выноса «Архитектуры», поэтому git-вариант едет под своим именем.
export { relTime as gitRelTime } from '../gitFormat';

// ─── useNow ──────────────────────────────────────────────────────────────────
export { useNow } from '../useNow';

// ─── expiry ──────────────────────────────────────────────────────────────────
export { EXPIRY_PRESETS, expiryOptionLabel } from '../expiry';

// ─── openChat ────────────────────────────────────────────────────────────────
export { openChatById } from '../openChat';

// ─── selectionScope ──────────────────────────────────────────────────────────
export { registerCopyDoc, copyMarkdown, copyRenderedHtml } from '../selectionScope';

// ─── listAutoFocus ───────────────────────────────────────────────────────────
export { useListAutoFocus } from '../listAutoFocus';

// ─── hooks/useOnline ─────────────────────────────────────────────────────────
export { useOnline } from '../../hooks/useOnline';

// ─── hooks/useContainerWidth ─────────────────────────────────────────────────
export { useContainerWidth } from '../../hooks/useContainerWidth';

// ─── components/ui ───────────────────────────────────────────────────────────
export {
  Button, IconButton, Badge, Modal, ConfirmDialog, BackButton,
  IslandScaffold, PanelHeaderSlot, useHasPanelHeader, MenuItem, MenuSep,
  SidebarSection, Toggle, PageCanvas, WaitingIndicator, Dot,
  Island, EmptyState, Field, TextField, TextArea, IconField, ModalActions, Menu, SegmentedControl, Checkbox,
  Chip, ChipX, ProgressBar, MetaChip, Select, InlineSegmented, Notice,
} from '../../components/ui';
export type { SelectOption } from '../../components/ui';

// ─── components/ui/icons ─────────────────────────────────────────────────────
export { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';

// ─── components/MarkdownViewer ───────────────────────────────────────────────
export { MarkdownViewer, stripFrontmatter, codeMask } from '../../components/MarkdownViewer';
export type { ResolvedNote } from '../../components/MarkdownViewer';

// ─── components/midi/MidiEditor ──────────────────────────────────────────────
// Ленивый — чтобы @tonejs/midi и нотная лента не ехали в чанк кита.
export const MidiEditor = lazy(() => import('../../components/midi/MidiEditor').then(m => ({ default: m.MidiEditor })));
export type { MidiEditorProps } from '../../components/midi/MidiEditor';

// ─── components/Toolbar ──────────────────────────────────────────────────────
export { PillSwitch, tbBtnPrimary, tbBtnGhost, Toolbar, ToolbarIconButton } from '../../components/Toolbar';

// ─── components/ToolbarOverflowMenu ──────────────────────────────────────────
export { ToolbarOverflowMenu } from '../../components/ToolbarOverflowMenu';
export type { OverflowItem } from '../../components/ToolbarOverflowMenu';

// ─── components/HubTabs ──────────────────────────────────────────────────────
export { subsystemTabValue } from '../../components/HubTabs';
export type { HubTabValue } from '../../components/hubTabsModel';

// ─── components/HubHeader ────────────────────────────────────────────────────
export { HubHeader } from '../../components/HubHeader';

// ─── components/chat/contexts ────────────────────────────────────────────────
export { ChatProjectContext } from '../../components/chat/contexts';

// ─── components/LocalGpuQueueLine ──────────────────────────────────────────────
// Строка ожидания локальной GPU-очереди в карточках трёх редакторов (ADR-029 §8):
// относительный импорт из features/ собрал бы вторую копию компонента в remote.
export { LocalGpuQueueLine } from '../../components/LocalGpuQueueLine';

// ─── cloudRescue ───────────────────────────────────────────────────────────────
// Общая логика «Сделать в облаке» трёх редакторов (ADR-029 §8): guard двойного клика,
// «стенд лежит», выбор облачного исполнителя, текст диалога и подпись кнопки.
export {
  makeLaunchGuard, isStandDown, cloudCandidate,
  CLOUD_DIALOG_TITLE, cloudDialogSubtitle, cloudButtonLabel,
} from '../cloudRescue';

// ─── features/personas/PersonaAvatar ─────────────────────────────────────────
export { PersonaAvatar } from '../../features/personas/PersonaAvatar';

// ─── features/home/WidgetCard ─────────────────────────────────────────────────
export { WidgetCard, WidgetAction, WidgetEmpty, relTime, MiniSegment } from '../../features/home/WidgetCard';

// ─── pages/workspace/PanelZone ───────────────────────────────────────────────
export { PanelZone } from '../../pages/workspace/PanelZone';

// ─── pages/workspace/panelCatalog ────────────────────────────────────────────
export { NOTES_KEYS, WIKI_PANEL_KEYS } from '../../pages/workspace/panelCatalog';

// ─── pages/workspace/panelStackState ─────────────────────────────────────────
export { notesPanels, zoneOf } from '../../pages/workspace/panelStackState';

// ─── chatFollow ──────────────────────────────────────────────────────────────
// Запуск по действию человека прокручивает ленту чата вниз, как своё сообщение
export { followChat } from '../chatFollow';

// ─── signalr ─────────────────────────────────────────────────────────────────
export { onMessage, onReconnected } from '../signalr';

// ─── features/modelsSpend ────────────────────────────────────────────────────
// Модалка ядра (её же открывает шапка хаба), а не код MF-модуля spend
export { ModelsSpendModal } from '../../features/modelsSpend/ModelsSpendModal';

// ─── components/generation ───────────────────────────────────────────────────
// Общий каркас панели генерации: «Картинки» и «Звук» видят хост только через кит (ADR-021 §3)
export { GenerationPanel, GEN_PANEL_W, useGenerationSheet } from '../../components/generation/GenerationPanel';
export type {
  GenerationFoot, GenerationFootProgress, GenerationFootResult, GenerationPanelView,
} from '../../components/generation/GenerationPanel';
export { ByClaude } from '../../components/generation/ByClaude';
// «В контекст ▾» (ADR-023, 2к-2): кнопка наполнения контекста с выбором роли
export { ContextAddButton } from '../../components/generation/ContextAddButton';
// Общий слой панелей: переключатель режима, меню выбора источника, «Вернуть» после снятия выбора
export { ReleaseNotice } from '../../components/generation/ReleaseNotice';
export { createReleaseUndo, RELEASE_UNDO_MS } from '../../components/generation/useReleaseUndo';
export type { ReleaseOffer, ReleaseUndoController } from '../../components/generation/useReleaseUndo';
// Список «Исполнитель» панели генерации (общий слой Г1): строки строит раздел сам
export { ExecutorList, ExecutorSummaryRow } from '../../components/generation/ExecutorList';
export type { ExecutorRow, ExecutorBadge } from '../../components/generation/ExecutorList';
export type { TabItem } from '../../components/ui';

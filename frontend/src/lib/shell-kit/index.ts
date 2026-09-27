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

// ─── design ──────────────────────────────────────────────────────────────────
export { FONT, FS, C, R, SP, SHADOW, ISLAND, Z, GROUP_COLORS, CHAT_MAX_W, TB } from '../design';

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

// ─── subsystems ──────────────────────────────────────────────────────────────
export { useSubsystem, isSubsystemEnabled } from '../subsystems';

// ─── subsystems/registryCore ─────────────────────────────────────────────────
export { registerSubsystem } from '../subsystems/registryCore';
export type { SubsystemManifest } from '../subsystems/registryCore';

// ─── offline ─────────────────────────────────────────────────────────────────
export { OfflineError } from '../offline';

// ─── toast ───────────────────────────────────────────────────────────────────
export { showToast } from '../toast';

// ─── personas ────────────────────────────────────────────────────────────────
export { ensurePersonasLoaded, usePersonas, personaLabel } from '../personas';

// ─── breakpoints ─────────────────────────────────────────────────────────────
export { useIsMobile, useWindowWidth } from '../breakpoints';

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
export { parseHash, navPush, navReplace, getNav } from '../nav';
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
  IslandScaffold, PanelHeaderSlot, useHasPanelHeader, MenuItem,
  SidebarSection, Toggle, PageCanvas, WaitingIndicator,
  EmptyState, MetaChip, IconField,
} from '../../components/ui';

// ─── components/ui/icons ─────────────────────────────────────────────────────
export { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';

// ─── components/MarkdownViewer ───────────────────────────────────────────────
export { MarkdownViewer, stripFrontmatter } from '../../components/MarkdownViewer';
export type { ResolvedNote } from '../../components/MarkdownViewer';

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

// ─── features/personas/PersonaAvatar ─────────────────────────────────────────
export { PersonaAvatar } from '../../features/personas/PersonaAvatar';

// ─── features/home/WidgetCard ─────────────────────────────────────────────────
export { WidgetCard, WidgetAction, WidgetEmpty, relTime } from '../../features/home/WidgetCard';

// ─── pages/workspace/PanelZone ───────────────────────────────────────────────
export { PanelZone } from '../../pages/workspace/PanelZone';

// ─── pages/workspace/panelCatalog ────────────────────────────────────────────
export { NOTES_KEYS } from '../../pages/workspace/panelCatalog';

// ─── pages/workspace/panelStackState ─────────────────────────────────────────
export { notesPanels, zoneOf } from '../../pages/workspace/panelStackState';

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
export { FONT, FS, C, R, SP, SHADOW, ISLAND, Z, GROUP_COLORS, CHAT_MAX_W, TB, CONTENT_MAX_W } from '../design';

// ─── api ─────────────────────────────────────────────────────────────────────
export { api } from '../api';

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
// request и readStoredToken — низкоуровневый HTTP редактора картинок: его api.ts
// ходит по своим маршрутам в обход фасада api
export { OfflineError, request, readStoredToken } from '../offline';

// ─── featureFlags ────────────────────────────────────────────────────────────
export { FLAGS, useFeature } from '../featureFlags';

// ─── defaultPersona ──────────────────────────────────────────────────────────
export { useMe } from '../defaultPersona';

// ─── toast ───────────────────────────────────────────────────────────────────
export { showToast } from '../toast';

// ─── personas ────────────────────────────────────────────────────────────────
export { ensurePersonasLoaded, usePersonas, personaLabel } from '../personas';

// ─── breakpoints ─────────────────────────────────────────────────────────────
export { useIsMobile, useWindowWidth, MOBILE_MAX, TABLET_MAX } from '../breakpoints';

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
export { projectColor } from '../tasks';

// ─── themeMode ───────────────────────────────────────────────────────────────
export { getEffectiveTheme, subscribeThemeMode } from '../themeMode';

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
  SidebarSection, Toggle, PageCanvas, WaitingIndicator, Dot,
  Island, EmptyState, Field, TextField, TextArea, IconField, ModalActions, Menu, SegmentedControl, Checkbox,
} from '../../components/ui';

// ─── components/ui/icons ─────────────────────────────────────────────────────
export { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';

// ─── components/MarkdownViewer ───────────────────────────────────────────────
export { MarkdownViewer, stripFrontmatter } from '../../components/MarkdownViewer';
export type { ResolvedNote } from '../../components/MarkdownViewer';

// ─── components/Toolbar ──────────────────────────────────────────────────────
export { PillSwitch, tbBtnPrimary, tbBtnGhost } from '../../components/Toolbar';

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
export { WidgetCard, WidgetAction, WidgetEmpty, relTime, MiniSegment } from '../../features/home/WidgetCard';

// ─── pages/workspace/PanelZone ───────────────────────────────────────────────
export { PanelZone } from '../../pages/workspace/PanelZone';

// ─── pages/workspace/panelCatalog ────────────────────────────────────────────
export { NOTES_KEYS } from '../../pages/workspace/panelCatalog';

// ─── pages/workspace/panelStackState ─────────────────────────────────────────
export { notesPanels, zoneOf } from '../../pages/workspace/panelStackState';

// ─── signalr ─────────────────────────────────────────────────────────────────
export { onMessage, onReconnected } from '../signalr';

// ─── features/modelsSpend ────────────────────────────────────────────────────
// Модалка ядра (её же открывает шапка хаба), а не код MF-модуля spend
export { ModelsSpendModal } from '../../features/modelsSpend/ModelsSpendModal';

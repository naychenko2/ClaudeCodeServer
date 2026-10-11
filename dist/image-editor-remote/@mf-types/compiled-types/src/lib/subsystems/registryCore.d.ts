import type { ComponentType, LazyExoticComponent, ReactNode } from 'react';
import type { AuthState, ChatItem, NoteDetail, Persona, Session } from '../../types';
import type { HubTabValue } from '../../components/hubTabsModel';
export interface SlotContribution<C = never, A = Record<string, unknown>> {
    name?: string;
    order?: number;
    render?: (ctx: C) => ReactNode;
    action?: A;
}
export interface SubsystemTabProps {
    auth: AuthState;
    onLogout: () => void;
    onHubTab: (t: HubTabValue) => void;
}
export interface SubsystemManifest {
    key: string;
    title: string;
    icon?: ReactNode;
    order: number;
    noPill?: boolean;
    core?: boolean;
    tab?: {
        component: LazyExoticComponent<ComponentType<SubsystemTabProps>>;
    };
    slots?: Record<string, SlotContribution[]>;
}
export interface FileViewerNoteViewCtx {
    noteId: string;
    existingTitles: Set<string>;
    onWikilink: (target: string) => void;
    onSelectNote: (id: string) => void;
    onDeleted: () => void;
    isMobile?: boolean;
    onBack?: () => void;
    onOpenFileRef?: (path: string) => void;
    extraToolbar?: ReactNode;
}
export interface FileViewerNoteEditorCtx {
    filePath: string;
    value: string;
    onChange: (v: string) => void;
    onWikilink: (target: string) => void;
}
export interface MarkdownEditorCtx {
    value: string;
    onChange: (v: string) => void;
    placeholder?: string;
    minHeight?: number;
}
export interface FileViewerNoteConnectionsCtx {
    note: NoteDetail;
    onOpenNote: (id: string, title: string) => void;
    onWikilink: (target: string) => void;
}
export interface FileViewerDocCommentsCtx {
    scope: string;
    docPath: string;
    content: string;
    isMobile?: boolean;
    onCounts?: (total: number, open: number) => void;
    panelTarget: HTMLElement | null;
    railSide?: 'left' | 'right';
    onDocLink?: (href: string) => void;
    resolveImageSrc: (src: string) => string | undefined;
}
export interface FileExplorerFolderIconCtx {
    size?: number;
}
export interface FileExplorerNoteDialogCtx {
    defaults: {
        source: string;
        folder?: string;
        file?: string;
    };
    onClose: () => void;
    onCreated: (id: string) => void;
}
export interface WorkspacePanelNotesCtx {
    projectId: string;
    activeFilePath?: string | null;
    onOpenFile: (path: string) => void;
}
export interface WorkspacePanelArchCtx {
    projectId: string;
    archOpen: boolean;
    onEnsureOpen: () => void;
    onCollapse: () => void;
}
export interface WorkspaceCenterDocCtx {
    projectId: string;
    projectName: string;
    isMobile: boolean;
    onClose: () => void;
    onShowFile: (path: string) => void;
}
export interface HomeWidgetNotesCtx {
    onHubTab: (t: HubTabValue) => void;
}
export type HomeWidgetSpendCtx = Record<string, never>;
export interface ChatHeaderBadgeCtx {
    sessionId: string;
    chatName?: string | null;
    resultCount: number;
    isMobile?: boolean;
}
export interface ActionButtonProps {
    icon: ReactNode;
    label: string;
    onClick: () => void;
    disabled?: boolean;
}
export interface QuickActionNoteCtx {
    ActionButton: ComponentType<ActionButtonProps>;
}
export interface ChatItemSaveNoteCtx {
    text: string;
    projectId?: string | null;
    online: boolean;
}
export interface ChatItemFileChangedApi {
    match: (path: string) => boolean;
    open: (path: string) => void;
    icon: ReactNode;
}
export interface ChatHeaderSummaryCtx {
    session: Session;
    hasMessages: boolean;
    online: boolean;
}
export interface ChatHeaderMenuItemCtx {
    run: () => void;
}
export interface PlanChipCtx {
    plan: string;
    projectId?: string;
}
export interface PlanButtonCtx {
    plan: string;
    online: boolean;
}
export interface GlobalSearchNoteApi {
    open: (id: string) => void;
}
export interface AiNoteOpenerApi {
    openNote: (id: string) => void;
}
export type ImageEditorOpenTarget = {
    kind: 'edit';
    path: string;
} | {
    kind: 'create';
    folder: string;
};
export interface ImageEditorOpenRequest {
    projectId: string;
    projectName: string;
    target: ImageEditorOpenTarget;
    onShowInFiles?: (path: string) => void;
}
export interface ImageEditorOpenerApi {
    isEditable: (path: string) => boolean;
    open: (req: ImageEditorOpenRequest) => void;
}
export declare const SLOT_CONTEXT_OPENER = "context-opener";
export interface ContextOpenerApi {
    isOpenable: (path: string) => boolean;
    toRef: (req: {
        projectId: string;
        sessionId: string;
        path: string;
    }) => Promise<{
        kind: string;
        ref: Record<string, unknown>;
    } | null>;
}
export interface FileViewerToolbarCtx {
    projectId: string;
    projectName: string;
    filePath: string;
    onOpenFile?: (path: string) => void;
}
export interface ChatItemToolCtx {
    item: ChatItem;
    online: boolean;
    projectId: string | null;
    sessionId: string | null;
    persona: Persona | null;
}
export interface ChatCardBadgeCtx {
    session: Session;
    isMobile: boolean;
}
export interface ChatCardBadgeApi {
    open?: (session: Session) => boolean;
}
export declare const SLOT_CONTEXT_KIND = "context-kind";
export declare const SLOT_COMPOSER_CHIP = "composer-chip";
export declare const SLOT_WORKSPACE_PANEL_DEF = "workspace-panel-def";
export interface ComposerShortcut {
    key: string;
    title: string;
    hint?: string;
    icon: ReactNode;
    onSelect: () => void;
}
export interface ComposerChipCtx {
    projectId: string | null;
    sessionId: string | null;
    isMobile: boolean;
    modeId?: string | null;
}
export interface ComposerChipApi {
    beforeSend?: (ctx: ComposerChipCtx) => Promise<File[]>;
}
export interface WorkspacePanelDefCtx {
    projectId: string | null;
    sessionId: string | null;
    isMobile: boolean;
    onClose: () => void;
}
export interface WorkspacePanelDefApi {
    title: string;
    icon: ReactNode;
    isAvailable?: (projectId: string | null) => boolean;
}
export declare const REVEAL_PANEL_EVENT = "cc-reveal-panel";
export interface RevealPanelDetail {
    key: string;
    tab?: string;
    sessionId?: string;
    target?: string;
    follow?: boolean;
    peek?: boolean;
}
export interface RevealPanelOptions {
    sessionId?: string;
    target?: string;
    ifOpen?: boolean;
}
export declare function revealWorkspacePanel(requested: string, tab?: string, opts?: RevealPanelOptions): boolean;
export declare function revealContextPanel(sessionId: string, opts?: {
    target?: string;
    ifOpen?: boolean;
}): boolean;
export declare function registerSubsystem(m: SubsystemManifest): void;
export declare function getRegisteredSubsystems(): SubsystemManifest[];
export declare function getSubsystem(key: string): SubsystemManifest | undefined;
export declare function getSubsystemTab(key: string): LazyExoticComponent<ComponentType<SubsystemTabProps>> | undefined;
export declare function getSlotContributions<C = never, A = Record<string, unknown>>(slot: string): SlotContribution<C, A>[];
export declare function getSlotItem<C = never, A = Record<string, unknown>>(slot: string, name: string): SlotContribution<C, A> | undefined;
export declare function getSlotAction<A = Record<string, unknown>>(slot: string, name: string): A | undefined;
export declare function subscribeRegistry(fn: () => void): () => void;
export declare function getRegistryVersion(): number;
export declare function useSlot<C = never, A = Record<string, unknown>>(slot: string): SlotContribution<C, A>[];
export declare function useSlotItem<C = never, A = Record<string, unknown>>(slot: string, name: string): SlotContribution<C, A> | undefined;
export declare function useRegisteredSubsystems(): SubsystemManifest[];

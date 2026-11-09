import { type LucideIcon } from 'lucide-react';
import type { BadgeTone } from '../../components/ui/CountBadge';
export type Zone = 'left' | 'right';
export declare const PANEL_KEYS: readonly ["chats", "files", "changes", "tasks", "docs", "dossiers", "knowledge", "notes", "graph", "arch", "team", "skills", "terminal", "preview", "plan", "agents", "context", "toc", "characters", "voices", "chatContext", "video", "notesList", "notesGraph", "knowledgeList", "personasList", "projectGroups"];
export type PanelKey = typeof PANEL_KEYS[number];
export interface HintLine {
    text: string;
    tone?: BadgeTone;
}
export type RailHint = string | readonly HintLine[];
export interface RailBadgeInfo {
    primary?: number;
    secondary?: number;
    primaryTone?: BadgeTone;
    secondaryTone?: BadgeTone;
    dot?: BadgeTone;
    hint?: RailHint;
}
export declare const PANEL_META: Record<PanelKey, {
    title: string;
    Icon: LucideIcon;
}>;
export declare const PANEL_HOME: Record<PanelKey, Zone>;
export declare const WORKSPACE_KEYS: readonly PanelKey[];
export declare const CHAT_KEYS: readonly PanelKey[];
export declare const NOTES_KEYS: readonly PanelKey[];
export declare const KNOWLEDGE_KEYS: readonly PanelKey[];
export declare const PERSONAS_KEYS: readonly PanelKey[];
export declare const PROJECTS_KEYS: readonly PanelKey[];
export declare const SESSION_KEYS: readonly PanelKey[];
export declare const CHAT_RIGHT_KEYS: readonly PanelKey[];
export declare const genPanelKeys: () => readonly PanelKey[];
export declare const CENTER_KEYS: readonly PanelKey[];
export declare const TOOLS_KEYS: readonly PanelKey[];
export declare const PROJECT_KEYS: readonly PanelKey[];
export declare const RAIL_GROUPS: readonly (readonly PanelKey[])[];
export declare function isPanelKey(v: unknown): v is PanelKey;
export declare function migrateLegacyKey(v: unknown): PanelKey | null;

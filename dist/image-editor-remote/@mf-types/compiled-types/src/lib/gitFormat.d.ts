import type { ChangedBySession } from '../types';
export declare function splitPath(p: string): [string, string];
export declare function relTime(iso: string): string;
export type FileChatBadge = {
    kind: 'single';
    mine: boolean;
    name?: string;
} | {
    kind: 'multi';
    mine: boolean;
    count: number;
    names: string[];
} | {
    kind: 'outside';
};
export declare function fileChatBadge(path: string, changedBy: Map<string, ChangedBySession[]> | undefined, sessionFiles: Set<string> | undefined): FileChatBadge | null;
export type ChatFilterMode = 'all' | 'mine' | 'others' | 'shared' | 'outside';
export declare function fileMatchesChatFilter(path: string, changedBy: Map<string, ChangedBySession[]> | undefined, sessionFiles: Set<string> | undefined, mode: ChatFilterMode): boolean;
export declare function countChatFilter(paths: string[], changedBy: Map<string, ChangedBySession[]> | undefined, sessionFiles: Set<string> | undefined): Record<ChatFilterMode, number>;

import type { ServerMessage, SessionContextEntry } from '../types';
export declare function loadChatContext(projectId: string, sessionId: string): Promise<void>;
export declare function saveChatContext(projectId: string, sessionId: string, list: Pick<SessionContextEntry, 'type' | 'id' | 'title'>[]): Promise<SessionContextEntry[]>;
export declare function applyContextUpdated(msg: ServerMessage): void;
export declare function getChatContext(sessionId: string): SessionContextEntry[] | undefined;
export declare function useChatContext(sessionId: string | null): SessionContextEntry[] | undefined;
export declare function useHasChatContext(sessionId: string | null): boolean;
export interface ActiveChat {
    projectId: string;
    sessionId: string;
}
export declare function setActiveChatForContext(v: ActiveChat | null): void;
export declare function useActiveChatForContext(): ActiveChat | null;
export declare function contextKey(type: SessionContextEntry['type'], id: string): string;
export declare function inChatContext(list: SessionContextEntry[] | undefined, type: SessionContextEntry['type'], id: string): boolean;
export declare function addToChatContext(projectId: string, sessionId: string, entry: Pick<SessionContextEntry, 'type' | 'id' | 'title'>): Promise<boolean>;
export declare function removeFromChatContext(projectId: string, sessionId: string, type: SessionContextEntry['type'], id: string): Promise<void>;
export declare function replaceChatContextEntry(projectId: string, sessionId: string, type: SessionContextEntry['type'], id: string, next: Pick<SessionContextEntry, 'type' | 'id' | 'title'>): Promise<void>;

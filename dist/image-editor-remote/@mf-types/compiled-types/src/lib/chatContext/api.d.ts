import type { ChatContextChangedEvent, ChatContextDto, ContextRefInput, SavedFileDto } from './types';
export declare const chatContextApi: {
    get: (sessionId: string) => Promise<ChatContextDto>;
    setPrimary: (sessionId: string, input: Pick<ContextRefInput, "kind" | "ref"> | null, revision: number) => Promise<ChatContextDto>;
    attachRef: (sessionId: string, input: ContextRefInput, revision: number) => Promise<ChatContextDto>;
    detachRef: (sessionId: string, itemId: string, revision: number) => Promise<ChatContextDto>;
    clear: (sessionId: string, revision: number) => Promise<ChatContextDto>;
    savedFiles: (sessionId: string) => Promise<SavedFileDto[]>;
    subscribe: (handler: (e: ChatContextChangedEvent) => void) => () => void;
};
export declare function conflictContext(e: unknown): ChatContextDto | null;

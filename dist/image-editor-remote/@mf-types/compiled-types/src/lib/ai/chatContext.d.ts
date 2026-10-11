export declare const AI_RECOMPUTE_EVENT = "cc-ai-recompute";
interface ChatCtx {
    active: boolean;
    hasMessages: boolean;
    tail?: string;
    personaId?: string;
}
export declare function setChatContext(active: boolean, hasMessages: boolean, tail?: string, personaId?: string): void;
export declare function getChatContext(): ChatCtx;
export declare function useChatPersonaId(): string | undefined;
export {};

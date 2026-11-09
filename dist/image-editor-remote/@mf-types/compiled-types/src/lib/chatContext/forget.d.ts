export declare function forgetChatContextSession(sessionId: string): void;
export declare function resetChatContextMemory(): void;
export declare function forgetChatContextOnDeleted(msg: {
    type: string;
    sessionId?: string;
}): void;

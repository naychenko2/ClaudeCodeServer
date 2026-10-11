export interface AgentResultTail {
    agentId?: string;
    tokens?: number;
    toolUses?: number;
    durationMs?: number;
}
export declare function splitAgentResultTail(result: string): {
    body: string;
    tail: AgentResultTail | null;
};
export declare function isAsyncLaunchAck(result: string | null | undefined): boolean;
export declare function asyncLaunchAckNote(bgAborted: boolean | undefined): string;
export declare function bgEmptyAnswerNote(opts: {
    settledNoText: boolean;
    bgAborted: boolean | undefined;
    hasToolActivity: boolean;
}): string | undefined;
export declare function isBgLaunchResult(result: string | null | undefined): boolean;
export declare function formatTailTokens(n: number): string;
export declare function formatTailDuration(ms: number): string;

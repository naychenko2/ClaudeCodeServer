import type { ChatItem, ServerMessage, RateLimitInfo, WorkLoopState, TeamImplementState, TeamWavePulse, SessionTeamImplement } from '../types';
export declare function teamImplementSnapshot(ti: SessionTeamImplement | null): TeamImplementState;
export interface ChatState {
    items: ChatItem[];
    isWaiting: boolean;
    rateLimits: Record<string, RateLimitInfo>;
    isCompacting: boolean;
    compactNote?: string;
    workLoop?: WorkLoopState;
    teamImplement?: TeamImplementState;
    teamPlanning?: {
        startedAt: number;
        personaId?: string | null;
    } | null;
    teamWavePulse?: TeamWavePulse;
    promptSuggestion: string | null;
    pending: PendingChatMessage[];
    composerRestore: ComposerRestore | null;
}
export interface PendingChatMessage {
    id: string;
    text: string;
    senderPersonaId?: string;
    senderOrigin?: string;
    senderChatName?: string;
    enqueuedAt: string;
    kind?: 'user' | 'agent';
    attachedPaths?: string[];
    mode?: string | null;
    waitingForDevice?: boolean;
}
export interface ComposerRestore {
    text: string | null;
    attachedPaths: string[] | null;
    mode: string | null;
    seq: number;
}
export declare function initialChatState(): ChatState;
export declare function consumeComposerRestore<S extends ChatState>(prev: S): S;
export declare function normalizeHistory(raw: unknown[], opts?: {
    deriveSpeakers?: boolean;
}): ChatItem[];
export declare const PERSISTED_KINDS: Set<"result" | "text" | "error" | "thinking" | "tool_use" | "session_started" | "user_message" | "permission_request" | "ask_question" | "plan_review" | "file_changed" | "fal_cost" | "glif_cost" | "rate_limit" | "compact_boundary" | "context_pruned" | "image_launch" | "image_file_moved" | "module_record" | "truncated" | "redacted_thinking" | "git_turn_commit" | "provider_switched" | "provider_limit" | "work_loop_stopped" | "team_plan" | "team_escalation" | "hands_notice" | "team_planning_done" | "interrupted" | "resumed" | "session_ended" | "companion_switched" | "model_switched" | "branched_from" | "error_group">;
export declare function serverHistoryNewer(serverItems: ChatItem[], prevItems: ChatItem[]): boolean;
export declare function resolveCardsFromHistory(serverItems: ChatItem[], prevItems: ChatItem[]): ChatItem[] | null;
export declare function turnAlreadyEnded(items: ChatItem[]): boolean;
export declare function retryableInterruptedIndex(items: ChatItem[]): number;
export declare function applyServerMessage<S extends ChatState>(prev: S, msg: ServerMessage): S;

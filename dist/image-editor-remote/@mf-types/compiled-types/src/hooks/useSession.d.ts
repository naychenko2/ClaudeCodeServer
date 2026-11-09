import type { ChatItem, Session, RateLimitInfo, WorkLoopState, TeamImplementState, TeamPlanDecision, TeamWavePulse } from '../types';
import { type PendingChatMessage, type ComposerRestore } from '../lib/chatReducer';
export declare const waitDiag: {
    mismatches: number;
    last: {
        sid: string;
        client: boolean;
        server: string;
        at: string;
    } | null;
};
export declare function warmSession(sid: string, projectId?: string, isGroup?: boolean): void;
export declare function useHoverWarm(enabled?: boolean): (chat: Session | null) => void;
export declare function useSession(sessionId: string | null, projectId?: string, isGroup?: boolean): {
    items: ChatItem[];
    isWaiting: boolean;
    isJoined: boolean;
    isHistoryLoading: boolean;
    rateLimits: Record<string, RateLimitInfo>;
    isCompacting: boolean;
    compactNote: string | undefined;
    workLoop: WorkLoopState | undefined;
    teamImplement: TeamImplementState | undefined;
    teamPlanning: {
        startedAt: number;
        personaId?: string | null;
    } | null | undefined;
    teamWavePulse: TeamWavePulse | undefined;
    promptSuggestion: string | null;
    pending: PendingChatMessage[];
    composerRestore: ComposerRestore | null;
    consumeRestore: () => void;
    send: (text: string, attachedPaths?: string[], mode?: string, opts?: {
        auto?: boolean;
    }) => Promise<void>;
    allowPermission: (requestId: string) => Promise<void>;
    denyPermission: (requestId: string) => Promise<void>;
    allowAlways: (requestId: string) => Promise<void>;
    answerQuestion: (toolUseId: string, answerText: string) => Promise<void>;
    respondPlan: (requestId: string, approve: boolean, feedback?: string) => Promise<void>;
    respondTeamPlan: (planId: string, decision: TeamPlanDecision, subtaskId?: string, executorPersonaId?: string, feedback?: string) => Promise<void>;
    respondTeamEscalation: (escalationId: string, actionId?: string, comment?: string) => Promise<void>;
    interrupt: () => void;
    compact: () => Promise<void>;
    toggleThinking: (index: number) => void;
    noteCompanionSwitch: (label: string, prevPersonaId?: string | null) => void;
    changeMode: (mode: string) => void;
    cancelPending: (messageId: string) => Promise<void>;
    preemptForPending: () => Promise<void>;
};

import type { Persona, Task, TeamImplementBudget, TeamPlanDecision } from '../../types';
export declare const ChatProjectContext: import("react").Context<{
    id: string;
    rootPath: string;
    transcriptReason?: string | null;
    local?: boolean;
    devicePlatform?: string | null;
} | null>;
export declare const ChatTreePathContext: import("react").Context<string | null>;
export declare const ChatOpenFileContext: import("react").Context<((path: string) => void) | null>;
export declare const ChatOpenReaderContext: import("react").Context<((url: string) => void) | null>;
export declare const ChatOpenTaskContext: import("react").Context<((task: Task) => void) | null>;
export declare const ChatSessionContext: import("react").Context<string | null>;
export declare const PersonaContext: import("react").Context<Persona | null>;
export declare const AssistantNameContext: import("react").Context<string>;
export declare function useAssistantName(): string;
export interface TeamPlanChatContext {
    autoWaves: boolean;
    waveNumber: number;
    planCardId: string | null;
    executorPersonaIds: string[];
    budget: TeamImplementBudget | null;
    onRespond: (planId: string, decision: TeamPlanDecision, subtaskId?: string, executorPersonaId?: string, feedback?: string) => void;
}
export declare const TeamPlanContext: import("react").Context<TeamPlanChatContext | null>;
export interface TeamEscalationChatContext {
    onRespond: (escalationId: string, actionId?: string, comment?: string) => void;
    coordinatorTurnActive?: boolean;
}
export declare const TeamEscalationContext: import("react").Context<TeamEscalationChatContext | null>;
export declare const FalCostContext: import("react").Context<Map<string, number>>;
export declare const GlifCostContext: import("react").Context<Map<string, number>>;
export declare const MediaVisibilityContext: import("react").Context<Map<string, import("./MediaBlock").MediaItem[]> | null>;
export declare const SpeakingItemContext: import("react").Context<{
    index: number;
    color: string;
} | null>;
export declare const ToolLivenessContext: import("react").Context<import("../../lib/toolTiming").ToolLiveness | null>;

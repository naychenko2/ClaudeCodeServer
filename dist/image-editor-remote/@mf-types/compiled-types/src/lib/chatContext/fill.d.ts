import type { ChatContextDto, ChatContextRef, ContextKindCtx, ContextRole } from './types';
export interface ContextCandidate {
    kind: string;
    ref: Record<string, unknown>;
}
export declare const refOf: (state: ChatContextDto, candidate: ContextCandidate) => ChatContextRef | null;
export declare function rolesFor(ctx: ContextKindCtx, state: ChatContextDto, candidateKind: string): readonly ContextRole[];
export declare const unusedBy: (r: {
    usedBy: readonly string[];
}, op?: string | null) => boolean;

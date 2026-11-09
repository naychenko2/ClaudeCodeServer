import type { ActionPreset, ChatContextItem, ContextAction, ContextActor } from './types';
export declare const subscribeActionMemory: (fn: () => void) => () => void;
export declare const getActionMemoryVersion: () => number;
export declare function useActionMemoryVersion(): number;
export declare const objectKey: (item: Pick<ChatContextItem, "kind" | "ref">) => string;
export declare function pickDefaultAction(actions: readonly ContextAction[], by: ContextActor): string | null;
export declare function rememberAction(sessionId: string, key: string, actionId: string | null): void;
export declare function presetAction(sessionId: string, key: string, preset: ActionPreset): void;
export declare const noteRunStarted: (sessionId: string, fromKey: string, actionId: string) => void;
export declare const clearRunCarry: (sessionId: string) => void;
export interface ResolvedAction {
    actionId: string | null;
    preset?: ActionPreset;
}
export declare function resolveAction(sessionId: string, key: string, by: ContextActor, actions: readonly ContextAction[], consume?: boolean): ResolvedAction;
export declare function takePreset(sessionId: string, key: string, by: ContextActor, actions: readonly ContextAction[]): ActionPreset | null;
export declare function forgetActionMemory(sessionId: string): void;
export declare function resetActionMemory(): void;

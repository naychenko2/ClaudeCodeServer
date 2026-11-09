import type { ActionQuote, ActionResult, ActionRun, ChatContextPrimary, ChatContextRef, ContextAction, ContextKindApi, ContextKindCtx, LaunchParam, LaunchParams, QuoteRequest, RunState } from './types';
interface QuoteSlot {
    key: string;
    value: ActionQuote | null;
}
export interface RunEntry {
    scope: string;
    text: string;
    values: Record<string, number | string>;
    state: RunState;
    progress: number | null;
    result: ActionResult | null;
    quote: QuoteSlot | null;
    quoting: string | null;
}
export declare const subscribeRun: (fn: () => void) => () => void;
export declare const getRunVersion: () => number;
export declare const notifyKindChanged: () => void;
export declare function runEntry(sessionId: string, scope: string): RunEntry;
export declare function setRunText(sessionId: string, scope: string, text: string): void;
export declare function setRunParam(sessionId: string, scope: string, name: string, value: number | string): void;
export declare function mergeParams(base: readonly LaunchParam[], values: Readonly<Record<string, number | string>>): LaunchParam[];
export declare function launchParams(action: ContextAction, params: readonly LaunchParam[], values: Readonly<Record<string, number | string>>): LaunchParams;
export declare const questionValue: (action: ContextAction, values: Readonly<Record<string, number | string>>) => string | null;
export declare function runLabelParts(o: {
    action: ContextAction | null;
    params: readonly LaunchParam[];
    price: string | null;
    mobile: boolean;
    state: RunState;
    progress: number | null;
    objectLabel?: string | null;
}): {
    name: string;
    tail: string;
};
export declare function runLabel(o: Parameters<typeof runLabelParts>[0]): string;
export declare function runBlockReason(action: ContextAction | null, text: string): string | null;
export declare function ensureQuote(sessionId: string, scope: string, api: ContextKindApi, ctx: ContextKindCtx, req: {
    op: string;
    text: string;
    params: LaunchParams;
    contextRevision: number;
}): Promise<void>;
export interface RunDeps {
    sessionId: string;
    api: ContextKindApi;
    ctx: ContextKindCtx;
    primary: ChatContextPrimary;
    action: ContextAction;
    scope: string;
    params: LaunchParams;
    revision: number;
}
export declare function launchAction(d: RunDeps, text: string): Promise<void>;
export declare function resetRun(sessionId: string): void;
export declare function resetAllRuns(): void;
export declare function buildActionRun(o: {
    sessionId: string;
    api: ContextKindApi | null;
    ctx: ContextKindCtx;
    primary: ChatContextPrimary | null;
    refs: readonly ChatContextRef[];
    revision: number;
}): ActionRun & {
    scope: string;
    req: QuoteRequest | null;
};
export {};

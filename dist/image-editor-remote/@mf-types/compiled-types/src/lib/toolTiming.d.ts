import type { ToolProgress, ToolRunTotals, ToolStage } from '../types';
export declare function toolProgressText(p: ToolProgress | null | undefined, meter?: boolean): string | null;
export declare const ETA_MIN_PERCENT = 10;
export declare const ETA_MIN_STAGE_MS = 15000;
export declare const ETA_TAIL_PERCENT = 98;
export declare function meterText(p: ToolProgress | null | undefined, stageMs: number | null, sinceMs: number | null): string | null;
export declare function meterParts(p: ToolProgress | null | undefined, stageMs: number | null, sinceMs: number | null): {
    percent: string;
    left: string | null;
    almost: boolean;
} | null;
export declare function toolProgressPercent(p: ToolProgress | null | undefined): number | null;
export declare function isQueued(p: ToolProgress | null | undefined): boolean;
export declare const TOOL_TIMER_MIN_MS = 2000;
export declare const QUEUE_STAGE_MIN_MS = 2000;
export interface StageView {
    key: string;
    label: string;
    ms: number | null;
    state: 'done' | 'current' | 'failed';
}
export declare function stageViews(raw: readonly ToolStage[] | null | undefined, opts: {
    running: boolean;
    aborted: boolean;
    now: number | null;
    endAt: number | null;
}): StageView[];
export declare function isFinalStages(stages: readonly ToolStage[] | null | undefined, totals: ToolRunTotals | null | undefined): boolean;
export declare function stageCaptionOf(p: ToolProgress | null | undefined, toolName?: string): string | null;
export declare function totalsText(t: ToolRunTotals | null | undefined): string | null;
export declare function formatClock(ms: number): string;
export declare function toolElapsedMs(item: {
    startedAt?: number | null;
    finishedAt?: number | null;
    result?: string | null;
}, now: number): number | null;
export declare function awaitsToolStart(item: {
    name: string;
    started?: boolean;
    stages?: readonly unknown[] | null;
}): boolean;
export interface ShownClock {
    startedAt: number;
    value: number;
}
export declare function tickShownClock(prev: ShownClock | null, startedAt: number, now: number): ShownClock;
export declare function shownFor(clock: ShownClock | null, startedAt: number | null | undefined): number | null;
export declare function toolClockMs(item: {
    name: string;
    started?: boolean;
    startedAt?: number | null;
    finishedAt?: number | null;
    result?: string | null;
}, running: boolean, shown: number | null, abortedAt?: number | null): number | null;
export interface ToolLiveness {
    live: ReadonlySet<string>;
    dead: ReadonlySet<string>;
    abortedAt?: ReadonlyMap<string, number>;
}
type LivenessItem = {
    kind: string;
    id?: string;
    result?: string | null;
    parentToolUseId?: string | null;
    bgDone?: boolean;
    bgAborted?: boolean;
    workflowDone?: boolean;
    ts?: number;
};
export declare function toolLiveness(items: readonly LivenessItem[], busy: boolean): ToolLiveness;
type ActiveToolItem = {
    kind: string;
    id?: string;
    name?: string;
    input?: unknown;
    parentToolUseId?: string | null;
    streamingArg?: string | null;
    startedAt?: number | null;
    started?: boolean;
    stages?: readonly unknown[] | null;
};
export declare function pickActiveTool<T extends ActiveToolItem>(items: readonly T[], live: ReadonlySet<string>): T | null;
export declare function activeToolLabel(item: {
    name: string;
    input?: unknown;
}): string;
export declare const FAILED_RE: RegExp;
export type TypewriterPhase = 'typing' | 'hold' | 'erasing' | 'pause' | 'done';
export interface TypewriterState {
    phrase: number;
    shown: number;
    phase: TypewriterPhase;
}
export declare const TYPEWRITER_MS: {
    readonly type: 35;
    readonly hold: 2600;
    readonly erase: 18;
    readonly pause: 200;
};
export declare const TYPEWRITER_START: TypewriterState;
export declare const TYPEWRITER_AT_REST: TypewriterState;
export declare function nextTypewriterStep(s: TypewriterState, phrases: readonly string[]): TypewriterState;
export declare function typewriterDelay(s: TypewriterState): number | null;
export declare function typewriterText(s: TypewriterState, phrases: readonly string[]): string;
export declare function formatWaitClock(ms: number): string;
export declare function waitingToolCaption(label: string | null | undefined, elapsed: number | null, timer: boolean, awaitingResponse: boolean, leadMs?: number): {
    label: string;
    clock: string | null;
} | null;
export declare function captionLeadMs(startedAt: number | null | undefined, appearedAt: number | null | undefined): number;
export declare function isToolGroupDone(opts: {
    entries: readonly LivenessItem[];
    liveness: ToolLiveness | null;
    hasVisibleAfter: boolean;
    busy: boolean;
    awaitingPermission: boolean;
}): boolean;
export {};

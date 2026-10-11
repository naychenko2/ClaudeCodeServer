import type { RateLimitInfo, UsageSnapshot } from '../types';
export interface RateWindow extends RateLimitInfo {
    pct: number;
    hasUtil: boolean;
    level: 'normal' | 'warn' | 'danger';
    stale?: boolean;
}
export declare function windowLabel(type: string): string;
export declare function overageLabel(status?: string): string;
export declare const RATE_COLORS: Record<RateWindow['level'], {
    fill: string;
    text: string;
    bg: string;
    border: string;
}>;
export declare function toRateWindows(rateLimits: Record<string, RateLimitInfo>): RateWindow[];
export declare function worstWindow(windows: RateWindow[]): RateWindow | undefined;
export declare function latestPerWindow(snapshots: UsageSnapshot[]): Array<RateWindow & {
    timestamp?: string;
}>;
export declare function withAccountFallback(chat: RateLimitInfo[], accountSnapshots: UsageSnapshot[], now?: string): RateWindow[];
export declare function shortWindowLabel(type: string): string;
export interface RatePillSegment {
    limitType: string;
    label: string;
    text: string;
    pct: number | null;
    level: RateWindow['level'];
    stale?: boolean;
}
export declare function ratePillSegments(windows: RateWindow[]): RatePillSegment[];
export declare const PILL_MAX_WINDOWS = 3;
export declare function ratePillVisible(windows: RateWindow[], max?: number): {
    segments: RatePillSegment[];
    more: number;
};
export declare const RING_WINDOWS = 2;
export interface RingPillCandidate {
    label: string;
    text: string;
    pct: number | null;
    level: RateWindow['level'];
    stale?: boolean;
}
export interface RingPillHead {
    label: string;
    text: string;
    level: RateWindow['level'];
    kind: 'value' | 'fresh' | 'calm' | 'unknown';
}
export declare function contextCandidate(estimate: {
    pct?: number;
    level: RateWindow['level'];
}): RingPillCandidate;
export declare function windowCandidates(windows: RateWindow[]): RingPillCandidate[];
export declare function worstRingCandidate(candidates: RingPillCandidate[]): RingPillCandidate | undefined;
export declare function ringPillHead(ctx: {
    pct?: number;
    level: RateWindow['level'];
    fresh?: boolean;
} | null, others: RingPillCandidate[], idle?: RingPillCandidate): RingPillHead | null;
export declare function ringArcLength(pct: number | null, circumference: number): number;
export declare function seriesByWindow(snapshots: UsageSnapshot[]): Record<string, {
    t: number;
    u: number;
}[]>;
export declare function latestWithUtilization(snapshots: UsageSnapshot[]): UsageSnapshot | null;
export declare function snapshotFreshnessLabel(source: UsageSnapshot['source'], timestamp: string, now?: number): string | null;
export declare function fmtReset(resetsAt?: string): string;

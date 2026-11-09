import type { ReactNode } from 'react';
import { type QuotaWindowView } from './QuotaWindow';
export interface PillSpec {
    label: string;
    tone: 'plain' | 'danger' | 'free' | 'warn';
}
export interface FreshnessSpec {
    dot: string;
    text: string;
    textTone?: string;
}
export interface UnavailableModelRow {
    model: string;
    text: string;
}
export interface HealthSpec {
    requests24h?: number | null;
    successRate?: number | null;
    platformsAlive?: number | null;
    platformsTotal?: number | null;
}
export interface ProviderCardData {
    key: string;
    name: string;
    color: string;
    state: 'loading' | 'error' | 'ready' | 'unavailable';
    isFree: boolean;
    dim?: boolean;
    retryAt?: number | null;
    onRetry: () => void;
    windows: QuotaWindowView[];
    labelWidth?: number;
    pills: PillSpec[];
    expandedPills?: PillSpec[];
    routingBadge?: {
        tone: 'ok' | 'warn';
        label: string;
    };
    freshness?: FreshnessSpec;
    hint?: ReactNode;
    health?: HealthSpec | null;
    hasExhausted: boolean;
    exhaustedResetAt?: string | null;
    unavailableModels?: UnavailableModelRow[];
    onRecheckModel?: (model: string) => Promise<void>;
    expandable: boolean;
    countWindows?: QuotaWindowView[];
    trend?: {
        t: number;
        u: number;
    }[];
    cabinetUrl?: string;
    tier?: string | null;
    thresholdNote?: string;
    freshnessDetail?: ReactNode;
    copyCommand?: string | null;
    loginCommand?: string | null;
}
export declare function ProviderCard({ data }: {
    data: ProviderCardData;
}): import("react").JSX.Element;

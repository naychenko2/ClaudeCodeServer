import type { ProviderBalanceInfo, ProviderFallbackOption } from '../types';
export interface FallbackSections {
    subscriptions: ProviderFallbackOption[];
    providers: ProviderFallbackOption[];
}
export declare function splitFallbackOptions(options: ProviderFallbackOption[]): FallbackSections;
export declare function formatSubscriptionMeta(option: ProviderFallbackOption): string;
export declare function providerSwitchReasonLabel(reason: string | undefined, fallback: string | undefined): string | undefined;
export declare function modelSwitchHeadline(reason: string | undefined, previousLabel: string, modelLabel: string): string;
export declare function modelUnavailableReason(reason: string | undefined): string;
export declare function unavailableModelName(model: string, resolve: (value: string) => string): string;
export declare function fmtRecheckTime(until: string, now?: number): string;
export declare function modelUnavailableText(name: string, reason: string | undefined, until: string | undefined, now?: number): string;
export interface ProviderAvailabilityVerdict {
    available: boolean;
    resetsAt: string | null;
}
export declare function providerAvailabilityFromBalance(balance: ProviderBalanceInfo | null | undefined, now?: number): ProviderAvailabilityVerdict;
export interface HiddenProvider {
    option: ProviderFallbackOption;
    resetsAt: string | null;
}
export declare function invalidateExhaustedVerdict<T extends Record<string, ProviderAvailabilityVerdict | undefined>>(verdicts: T, key: string, resetsAt?: string | null): T;
export declare function splitByAvailability(providers: ProviderFallbackOption[], verdicts: Record<string, ProviderAvailabilityVerdict | undefined>): {
    available: ProviderFallbackOption[];
    hidden: HiddenProvider[];
};
export declare function nearestReturn(hidden: HiddenProvider[]): HiddenProvider | null;
export declare function providersPlural(n: number): string;
export declare function fmtReturnTime(resetsAt: string, now?: number): string;

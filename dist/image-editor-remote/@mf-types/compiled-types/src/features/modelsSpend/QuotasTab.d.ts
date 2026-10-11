import type { SubscriptionUsage } from '../../types';
import type { PillSpec, ProviderCardData } from './ProviderCard';
import { type BalanceChipData } from './BalanceChip';
interface SubCtx {
    rotationThreshold: number;
    weeklyThreshold: number;
    routingTarget?: string;
    pollStatuses: Record<string, string>;
    freeAvailable: boolean;
    subs: Record<string, SubscriptionUsage>;
    usageError: boolean;
    onRecheckModel?: (key: string, model: string) => Promise<void>;
}
export declare function subscriptionPills(sub: SubscriptionUsage): PillSpec[];
export declare function subscriptionExpandedPills(sub: SubscriptionUsage): PillSpec[];
export declare function buildSubscriptionCard(key: string, sub: SubscriptionUsage, ctx: SubCtx): ProviderCardData;
export declare function QuotasTab({ balances, onClose }: {
    balances?: BalanceChipData[];
    onClose: () => void;
}): import("react").JSX.Element;
export {};

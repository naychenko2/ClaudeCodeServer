import type { BalanceChipData } from './BalanceChip';
export type ModelsSpendTab = 'quotas' | 'slots' | 'apply' | 'chains';
export declare function ModelsSpendModal({ balances, initialTab, onClose }: {
    balances?: BalanceChipData[];
    initialTab?: ModelsSpendTab;
    onClose: () => void;
}): import("react").JSX.Element;

export declare const LOW_BALANCE = 5;
export declare const LOW_BALANCE_RUB = 300;
export interface BalanceChipData {
    key: string;
    label: string;
    value: number;
    credits?: boolean;
    rub?: boolean;
}
export declare function BalanceChip({ b }: {
    b: BalanceChipData;
}): import("react").JSX.Element;

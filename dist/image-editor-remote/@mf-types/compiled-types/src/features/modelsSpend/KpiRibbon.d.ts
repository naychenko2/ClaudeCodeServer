export interface KpiHot {
    usedPct: number;
    label: string;
}
export interface KpiMoney {
    amount: number;
    accounts: number;
}
export interface KpiCost {
    amount: number;
}
export interface KpiFree {
    alive: number;
    total: number;
    successRate: number;
}
export interface KpiRibbonProps {
    hot: KpiHot | null;
    money: KpiMoney | null;
    cost: KpiCost | null;
    free: KpiFree | null;
    loading?: boolean;
    isMobile: boolean;
}
export declare function KpiRibbon({ hot, money, cost, free, loading, isMobile }: KpiRibbonProps): import("react").JSX.Element | null;

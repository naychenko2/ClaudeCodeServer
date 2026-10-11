export type SpendDim = 'user' | 'project' | 'chat' | 'task' | 'persona' | 'provider' | 'model' | 'source';
export interface SpendFilter {
    dim: SpendDim;
    val: string;
    label: string;
}
export interface SpendOpenContext {
    screen?: 'overview' | 'analysis';
    filters?: SpendFilter[];
    day?: string;
    preset?: string;
    pivotDim?: SpendDim;
    turnId?: string;
}
export declare const OPEN_SPEND_EVENT = "cc-open-spend";
export declare function openSpend(ctx?: SpendOpenContext): void;
export declare function stashSpendContext(ctx: SpendOpenContext): void;
export declare function consumeSpendContext(): SpendOpenContext | null;
export declare const todayUtc: () => string;
export declare const addDaysUtc: (date: string, n: number) => string;
export declare function spendQuery(opts: {
    from?: string;
    to?: string;
    scope?: 'mine' | 'all';
    filters?: SpendFilter[];
    extra?: Record<string, string | number | undefined>;
}): string;

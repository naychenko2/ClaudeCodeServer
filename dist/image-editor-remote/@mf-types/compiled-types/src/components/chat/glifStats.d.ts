import type { ChatItem } from '../../types';
export interface GlifGenStats {
    count: number;
    credits: number;
    hasCredits: boolean;
    byType: Map<string, number>;
}
export declare function computeGlifGenStats(items: ChatItem[]): GlifGenStats;
export declare const fmtCredits: (n: number) => string;

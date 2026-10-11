export declare const FLAGS: {
    readonly workspaceDestructive: "workspace-destructive";
    readonly changeDossiersRecall: "change-dossiers-recall";
    readonly spheres: "spheres";
};
export type FlagKey = (typeof FLAGS)[keyof typeof FLAGS];
export declare function setAllFlags(flags: Record<string, boolean>): void;
export declare function mergeFlags(flags: Record<string, boolean> | null | undefined): void;
export declare function setFlagLocal(key: string, value: boolean): void;
export declare function getFlag(key: string): boolean;
export declare function getAllFlags(): Record<string, boolean>;
export declare function subscribeFlags(fn: () => void): () => void;
export declare function useFeature(key: FlagKey): boolean;
export declare const FLAGS_REFRESH_MIN_INTERVAL_MS = 45000;
export declare function createFlagsRefresher(fetchFlags: () => Promise<Record<string, boolean> | null | undefined>, now?: () => number, minIntervalMs?: number): ((force?: boolean) => Promise<boolean>) & {
    markFresh(): void;
    cancel(): void;
};
export declare function useFeatureFlagsRefresh(enabled: boolean): void;

export declare function beginAiBusy(): void;
export declare function endAiBusy(): void;
export declare function withAiBusy<T>(fn: () => Promise<T>): Promise<T>;
export declare function useAiBusy(): boolean;

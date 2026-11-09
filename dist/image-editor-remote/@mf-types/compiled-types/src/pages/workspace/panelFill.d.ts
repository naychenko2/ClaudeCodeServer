export declare const PanelFillContext: import("react").Context<((need: boolean) => void) | null>;
export declare function useRequestPanelFill(need: boolean): void;
export declare function usePanelFillRequests<K extends string>(): [
    Partial<Record<K, boolean>>,
    (k: K) => (need: boolean) => void
];

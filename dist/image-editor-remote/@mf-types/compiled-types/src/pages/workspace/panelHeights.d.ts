export declare function usePanelHeights<K extends string>(): [
    (k: K) => (el: HTMLElement | null) => void,
    Partial<Record<K, number>>,
    (k: K) => number | null
];

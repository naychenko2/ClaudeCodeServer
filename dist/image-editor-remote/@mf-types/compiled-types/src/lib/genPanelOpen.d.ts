export type GenPanelOpenView = 'column' | 'sheet' | 'peek';
export interface GenPanelOpen {
    key: string;
    view: GenPanelOpenView;
}
export declare function holdGenPanelOpen(key: string, view: GenPanelOpenView): () => void;
export declare function openGenPanel(): GenPanelOpen | null;
export declare function noteReveal(key: string, peek: boolean): void;
export declare function followPeeked(key: string): boolean;
export declare function __resetGenPanelOpen(): void;

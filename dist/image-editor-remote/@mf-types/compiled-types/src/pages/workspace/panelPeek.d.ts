import type { PanelKey } from './panelCatalog';
export interface PanelPeek {
    key: PanelKey | null;
    show: (k: PanelKey) => void;
    hide: () => void;
    hold: () => void;
    clear: () => void;
}
export declare function usePanelPeek(): PanelPeek;

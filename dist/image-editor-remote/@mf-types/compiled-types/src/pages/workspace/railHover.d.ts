import type { PanelKey } from './panelCatalog';
export interface RailHover {
    key: PanelKey | null;
    enter: (k: PanelKey) => void;
    leave: () => void;
    clear: () => void;
}
export declare function useRailHover(): RailHover;

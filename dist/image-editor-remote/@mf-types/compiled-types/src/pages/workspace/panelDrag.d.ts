import type { PanelKey, Zone } from './panelCatalog';
export interface PanelDragState {
    from: PanelKey | null;
    fromZone: Zone | null;
    over: {
        zone: Zone;
        tag: string;
    } | null;
    fromTucked: boolean;
    moved: PanelKey | null;
}
export declare function startPanelDrag(from: PanelKey, fromZone: Zone, fromTucked?: boolean): void;
export declare function markPanelMoved(k: PanelKey): void;
export declare function setPanelDragOver(zone: Zone, tag: string | null): void;
export declare function clearPanelDragOver(zone: Zone, tag: string): void;
export declare function endPanelDrag(): void;
export declare function usePanelDragState(): PanelDragState;

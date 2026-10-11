import { type PanelKey } from './panelCatalog';
export declare const GEN_STACK_W = 320;
export declare const genPanelInZone: (k: PanelKey, compact: boolean, windowWidth: number) => boolean;
export declare function compactStack(windowWidth: number, zoneWidth: number, keys: readonly PanelKey[]): {
    inline: boolean;
    width: number;
};

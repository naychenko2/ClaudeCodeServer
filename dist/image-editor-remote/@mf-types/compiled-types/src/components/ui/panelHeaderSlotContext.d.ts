export interface PanelHeaderSlotValue {
    hasHeader: boolean;
    el: HTMLElement | null;
    elLeft: HTMLElement | null;
    elPinned: HTMLElement | null;
    hold: (held: boolean) => void;
}
export declare const PanelHeaderSlotContext: import("react").Context<PanelHeaderSlotValue>;
export declare function useHasPanelHeader(): boolean;
export declare function usePanelHeaderHold(active: boolean): void;

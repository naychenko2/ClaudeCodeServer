export declare function computeCenterShift(m: {
    rootLeft: number;
    rootRight: number;
    centerLeft: number;
    centerRight: number;
    contentWidth: number;
}): number;
export interface CenterOffset {
    rootRef: (el: HTMLElement | null) => void;
    centerRef: (el: HTMLElement | null) => void;
}
export declare function useCenterOffset(contentWidth?: number): CenterOffset;

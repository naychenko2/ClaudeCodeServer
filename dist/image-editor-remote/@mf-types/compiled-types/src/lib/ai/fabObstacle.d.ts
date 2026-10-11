type Listener = () => void;
export type FabObstacleSlot = 'main' | 'scroll-down' | 'intro-card' | 'notice';
export interface FabObstacleOptions {
    noRaise?: boolean;
}
export declare function setFabObstacle(el: HTMLElement, slot?: FabObstacleSlot, opts?: FabObstacleOptions): () => void;
export declare function getFabObstacles(): HTMLElement[];
export declare function isFabNoRaise(el: HTMLElement): boolean;
export declare function subscribeFabObstacle(f: Listener): () => void;
export declare const fabObstacleTestHooks: {
    stack: (slot?: FabObstacleSlot) => HTMLElement[];
    reset: () => void;
};
export declare const FAB_FULL = 54;
export declare const FAB_SMALL = 36;
export declare const FAB_CLEARANCE = 10;
export declare const FAB_EDGE_INSET = 6;
export declare const FAB_MOBILE_RIGHT: number;
export interface FabPlacement {
    small: boolean;
    edge: boolean;
    raise: number;
    hidden: boolean;
}
export declare const PLACE_CORNER: FabPlacement;
export interface Box {
    left: number;
    top: number;
    right: number;
    bottom: number;
}
export interface PlaceFabOptions {
    noRaise?: boolean;
    controlsAt?: (b: Box) => readonly Box[];
}
export declare function unionBox(boxes: readonly Box[]): Box | null;
export declare function placeFab(o: Box | null, right: number, bottom: number, viewportW: number, opts?: PlaceFabOptions): FabPlacement;
export declare const PLACE_HIDDEN: FabPlacement;
export declare function samePlacement(a: FabPlacement, b: FabPlacement): boolean;
export type FabBlocker = 'obstacle' | 'control' | null;
export declare function fabSpotBlocker(p: FabPlacement, o: Box | null, right: number, bottom: number, viewportW: number, opts?: PlaceFabOptions): FabBlocker;
export declare const FAB_SETTLE_MS = 250;
export declare const FAB_CONFIRM_MS = 150;
export declare const FAB_MOUNT_SETTLE_MS = 600;
export interface FabSettle {
    shown: FabPlacement;
    pending: FabPlacement | null;
    since: number;
    settleMs: number;
}
export declare const initialFabSettle: () => FabSettle;
export declare function stepFabSettle(s: FabSettle, measured: FabPlacement, blocker: FabBlocker, now: number): {
    next: FabSettle;
    recheckIn: number | null;
};
export {};

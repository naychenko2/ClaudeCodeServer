export declare const LONG_PRESS_MS = 500;
export interface LongPressPoint {
    x: number;
    y: number;
}
export declare function useLongPress(enabled: boolean, ms?: number): {
    pressingKey: string | null;
    pressProps: (key: string, onLong: (point: LongPressPoint) => void) => {
        onTouchStart?: undefined;
        onTouchEnd?: undefined;
        onTouchMove?: undefined;
        onTouchCancel?: undefined;
    } | {
        onTouchStart: (e: React.TouchEvent) => void;
        onTouchEnd: (e: React.TouchEvent) => void;
        onTouchMove: () => void;
        onTouchCancel: () => void;
    };
};

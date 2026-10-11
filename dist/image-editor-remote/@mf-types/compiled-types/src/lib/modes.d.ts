export type Mode = 'default' | 'acceptEdits' | 'plan' | 'auto' | 'dontAsk' | 'bypass';
export declare const MODES: Mode[];
export declare const MODE_META: Record<Mode, {
    label: string;
    desc: string;
    danger?: boolean;
}>;
export declare function ModeIcon({ mode, size }: {
    mode: Mode;
    size?: number;
}): import("react").JSX.Element;
export declare function isDangerMode(mode: Mode): boolean;

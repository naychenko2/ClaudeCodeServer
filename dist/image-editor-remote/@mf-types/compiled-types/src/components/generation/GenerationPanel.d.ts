import type { CSSProperties, ReactNode } from 'react';
import type { TabItem } from '../ui';
export declare const GEN_PANEL_W: {
    readonly default: 380;
    readonly min: 340;
    readonly max: 520;
    readonly spine: 44;
};
export interface GenerationFootProgress {
    label: string;
    p?: number;
    onCancel?: () => void;
}
export interface GenerationFootResult {
    file: string;
    actions: {
        label: string;
        onClick: () => void;
    }[];
}
export type GenerationFootCount = {
    count?: undefined;
    maxCount?: undefined;
    onCountChange?: undefined;
} | ({
    count: number;
    maxCountHint?: string;
} & ({
    maxCount: 1;
    onCountChange?: (n: number) => void;
} | {
    maxCount: number;
    onCountChange: (n: number) => void;
}));
export type GenerationFootState = {
    progress?: undefined;
    result?: undefined;
} | {
    progress: GenerationFootProgress;
    result?: undefined;
} | {
    progress?: undefined;
    result: GenerationFootResult;
};
export type GenerationFoot = {
    reason?: string;
    queue?: string;
    price?: [string, string];
    runLabel: string;
    runDisabled?: boolean;
    runIcon?: ReactNode;
    noCount?: boolean;
    stale?: string[];
    onRun: () => void;
} & GenerationFootCount & GenerationFootState;
export declare const useGenerationSheet: () => boolean;
export type GenerationPanelView = 'column' | 'spine' | 'sheet' | 'peek';
interface Props<T extends string> {
    title: string;
    subtitle?: string;
    icon: ReactNode;
    tabs?: TabItem<T>[];
    tab?: T;
    onTabChange?: (t: T) => void;
    context?: ReactNode;
    contextAction?: ReactNode;
    panelKey?: string;
    returnLink?: {
        label: string;
        onClick: () => void;
    };
    draftKey?: string | null;
    children: ReactNode;
    foot?: GenerationFoot;
    footContent?: ReactNode;
    peekSummary?: ReactNode;
    onClose?: () => void;
    collapsed?: boolean;
    onCollapsedChange?: (v: boolean) => void;
    peeked?: boolean;
    onPeekedChange?: (v: boolean) => void;
    layout?: 'auto' | 'column' | 'sheet';
    width?: number;
    onWidthChange?: (w: number) => void;
    minWidth?: number;
    maxWidth?: number;
    contained?: boolean;
    style?: CSSProperties;
}
export declare function GenerationPanel<T extends string = string>(p: Props<T>): import("react").JSX.Element;
export declare function GenerationFootView({ foot: f, touch }: {
    foot: GenerationFoot;
    touch?: boolean;
}): import("react").JSX.Element;
export {};

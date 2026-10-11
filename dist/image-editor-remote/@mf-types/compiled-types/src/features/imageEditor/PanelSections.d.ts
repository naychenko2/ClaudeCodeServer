import { type OutpaintRatio, type QuickAction, type QuickRoute } from './editorInputs';
export declare function QuickActions({ actions, blockReason, fallback, ratio, onRatio, onRun }: {
    actions?: QuickAction[];
    blockReason: (a: QuickAction) => string;
    fallback?: (a: QuickAction) => QuickRoute | null;
    ratio: OutpaintRatio;
    onRatio: (r: OutpaintRatio) => void;
    onRun: (a: QuickAction, provider?: string) => void;
}): import("react").JSX.Element;

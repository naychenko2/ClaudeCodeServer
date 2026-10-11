import { type EditMode, type ImageEditEstimate, type ImageEditModel, type ImageEditOp } from '../api';
import type { OutpaintRatio, QuickAction } from '../editorInputs';
import { type ProjectPrefs } from '../thread/prefs';
import type { ImageMode } from '../thread/modeState';
export type PanelOp = 'auto' | ImageEditOp;
interface OpInfo {
    op: PanelOp;
    run: string;
    needImage?: boolean;
    needMask?: boolean;
    noPrompt?: boolean;
    one?: boolean;
}
export declare const PANEL_OPS: OpInfo[];
export declare function quickOf(op: ImageEditOp): QuickAction | null;
export declare const isOneVariant: (op: ImageEditOp) => boolean;
export declare const runVerb: (op: ImageEditOp) => string;
export declare function opBlockReason(op: PanelOp, hasImage: boolean, hasMask: boolean): string;
export declare const effectiveMode: (model: ImageEditModel | null, mode: EditMode) => EditMode;
export declare function footPrice(est: Pick<ImageEditEstimate, 'amount' | 'unit' | 'approx' | 'etaSeconds' | 'queueLength'> | null, count: number): [string, string];
export declare function queueText(est: Pick<ImageEditEstimate, 'unit' | 'queueLength'> | null): string | undefined;
export interface PanelChoice {
    op: PanelOp;
    mode: EditMode;
    ratio: OutpaintRatio;
}
export declare const DEFAULT_CHOICE: PanelChoice;
export declare function editChoice(prefs: ProjectPrefs): PanelChoice;
export declare function activeChoice(projectId: string, mode: ImageMode): PanelChoice;
export declare function modeOp(op: PanelOp, hasImage: boolean, hasMask: boolean): {
    op: ImageEditOp;
    reason: string;
};
export declare function usePanelChoiceVersion(): number;
export declare function __resetPanelChoice(): void;
export declare const launchMarks: <M extends {
    type: string;
}>(marks: M[], whole: boolean) => M[];
export {};

import type { ExecutorRow } from 'aihome_shell/kit';
import { type ImageEditCatalog, type ImageEditModel, type ImageEditOp, type ImageEditProvider } from '../api';
import { type QuickAction } from '../editorInputs';
import type { ImageThreadSettings } from '../thread/threadsApi';
export declare const AUTO_EXECUTOR = "auto";
export declare function executorValue(catalog: ImageEditCatalog, s: Pick<ImageThreadSettings, 'provider' | 'model'>): string;
export declare function executorSettings(id: string): Pick<ImageThreadSettings, 'provider' | 'model'>;
export declare function rowPrice(pv: ImageEditProvider, m: ImageEditModel | null): string;
export declare function rowFree(pv: ImageEditProvider, m: ImageEditModel | null): boolean;
export interface ExecutorTask {
    op: ImageEditOp;
    hasImage: boolean;
    hasMask: boolean;
    quick: QuickAction | null;
}
export declare function executorRows(catalog: ImageEditCatalog, task: ExecutorTask): ExecutorRow[];
export declare function executorSummary(catalog: ImageEditCatalog, s: Pick<ImageThreadSettings, 'provider' | 'model'>, done: {
    provider: string;
    model: string;
} | null): {
    name: string;
    parts: string[];
};

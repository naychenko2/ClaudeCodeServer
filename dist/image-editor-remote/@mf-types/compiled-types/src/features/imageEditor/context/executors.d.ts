import type { ExecutorListModel, ExecutorRow } from 'aihome_shell/kit';
import type { ImageEditCatalog, ImageEditOp } from '../api';
import { AUTO_EXECUTOR } from '../panel/executorRows';
import { type ImageThread, type ImageThreadSettings } from '../thread/threadsApi';
export { AUTO_EXECUTOR };
export declare const settingsMode: (op: ImageEditOp) => "create" | "edit";
export declare const settingsOf: (scope: string, thread: ImageThread, op: ImageEditOp) => ImageThreadSettings;
export declare function rowsForOp(catalog: ImageEditCatalog, op: ImageEditOp, hasImage: boolean, hasMask: boolean): ExecutorRow[];
export declare function chooseExecutor(scope: string, sessionId: string, thread: ImageThread, op: ImageEditOp, id: string): void;
export declare function executorModel(o: {
    scope: string;
    sessionId: string;
    thread: ImageThread;
    op: ImageEditOp;
    catalog: ImageEditCatalog;
    hasImage: boolean;
    hasMask: boolean;
}): ExecutorListModel;
export declare const __resetExecutorCache: () => void;

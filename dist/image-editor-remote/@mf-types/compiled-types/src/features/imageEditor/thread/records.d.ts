import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { ChatItem } from '../../../types';
export declare const MODULE = "imageeditor";
export declare const recordKey: (recordType: string) => string;
export interface ModuleRecordView {
    module: string;
    recordType: string;
    data: unknown;
    fallback: string | null;
}
export declare function recordOf(item: ChatItem): ModuleRecordView | null;
export declare function ThreadSysLine({ ctx }: {
    ctx: ChatItemToolCtx;
}): import("react").JSX.Element | null;

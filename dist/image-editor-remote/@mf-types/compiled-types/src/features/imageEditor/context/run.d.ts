import { type ImageEditCatalog, type ImageEditOp } from '../api';
import type { ActionQuote, ContextKindCtx, LaunchHandle, LaunchParam, LaunchRequest } from 'aihome_shell/kit';
export declare function paramsFor(catalog: ImageEditCatalog | null, settings: {
    provider: string | null;
    model: string | null;
    count: number;
}, op: ImageEditOp): LaunchParam[];
export declare function quoteAction(ctx: ContextKindCtx, req: LaunchRequest): Promise<ActionQuote>;
export declare function launchAction(ctx: ContextKindCtx, req: LaunchRequest): Promise<LaunchHandle>;

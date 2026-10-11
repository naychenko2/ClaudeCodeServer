import type { ChatContextPrimary, ContextAction, ContextKindCtx, ChatContextRef } from 'aihome_shell/kit';
import type { ImageThread } from '../thread/threadsApi';
import { type ImageActionInput } from './actions';
export declare const IMAGE_KIND = "image";
export declare function threadOfPrimary(sessionId: string, primary: ChatContextPrimary | null): ImageThread | null;
export declare function inputOf(ctx: ContextKindCtx, thread: ImageThread): ImageActionInput;
export declare function imageActions(ctx: ContextKindCtx, s: {
    primary: ChatContextPrimary;
    refs: readonly ChatContextRef[];
}): readonly ContextAction[];
export declare function actionOf(ctx: ContextKindCtx, actionId: string): {
    action: ContextAction;
    thread: ImageThread;
    input: ImageActionInput;
    scope: string;
} | null;

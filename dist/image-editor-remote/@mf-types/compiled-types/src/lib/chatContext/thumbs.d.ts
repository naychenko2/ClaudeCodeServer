import type { ChatContextItem, ChatContextPrimary, ChatContextRef, ContextKindCtx } from './types';
export declare function thumbOf(ctx: ContextKindCtx, item: ChatContextItem): string | null;
export declare const withThumb: <T extends ChatContextPrimary | ChatContextRef>(ctx: ContextKindCtx, item: T) => T;

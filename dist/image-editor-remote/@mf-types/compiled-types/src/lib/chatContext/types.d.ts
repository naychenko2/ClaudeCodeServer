import type { ReactNode } from 'react';
import type { ExecutorRow } from '../../components/generation/ExecutorList';
import { CHAT_CONTEXT_EVENT } from './errors';
import type { FormatModel } from './formatTypes';
export type ContextActor = 'human' | 'agent';
interface ChatContextItemBase {
    id: string;
    kind: string;
    ref: Record<string, unknown>;
    by: ContextActor;
    addedAt: string;
    label: string;
    version: string | null;
    thumb: string | null;
    missing: boolean;
}
export interface ChatContextPrimary extends ChatContextItemBase {
    role: null;
}
export interface ChatContextRef extends ChatContextItemBase {
    role: string | null;
    usedBy: string[];
}
export type ChatContextItem = ChatContextPrimary | ChatContextRef;
export interface ChatContextDto {
    revision: number;
    primary: ChatContextPrimary | null;
    refs: ChatContextRef[];
}
export declare const EMPTY_CONTEXT: ChatContextDto;
export interface ChatContextConflictDto {
    error: string;
    context: ChatContextDto;
}
export interface ChatContextChangedEvent {
    type: typeof CHAT_CONTEXT_EVENT;
    sessionId: string;
    context: ChatContextDto;
}
export interface SavedFileDto {
    path: string;
    threadKind: string;
    savedAt: string;
}
export interface ContextRefInput {
    kind: string;
    ref: Record<string, unknown>;
    role?: string | null;
}
export interface ContextKindCtx {
    projectId: string | null;
    sessionId: string;
    isMobile: boolean;
}
export interface KindState {
    primary: ChatContextPrimary;
    refs: readonly ChatContextRef[];
    local?: Readonly<Record<string, unknown>>;
}
export type ActionKind = 'run' | 'editor' | 'menu';
export interface ContextMenuItem {
    id: string;
    label: string;
    disabledReason?: string;
    run: () => void;
}
export type ContextActionIcon = 'spark' | 'scissors' | 'maximize' | 'expand' | 'brush' | 'image' | 'mic' | 'users' | 'activity' | 'layers' | 'music' | 'link' | 'sliders' | 'film';
export interface ContextAction {
    id: string;
    kind: ActionKind;
    label: string;
    icon?: ContextActionIcon;
    verb?: string;
    hint: string;
    op?: string;
    text?: 'required' | 'optional' | 'none';
    placeholder?: string;
    question?: {
        param: string;
        title: string;
        options: readonly {
            value: string;
            label: string;
        }[];
    };
    disabledReason?: string;
    blockReason?: string;
    open?: () => void;
    items?: () => readonly ContextMenuItem[];
    thumb?: string | null;
}
export type LaunchParam = {
    kind: 'variants';
    min: 1;
    max: number;
    value: number;
} | {
    kind: 'duration';
    options: readonly number[];
    value: number;
} | {
    kind: 'aspect';
    options: readonly string[];
    value: string;
} | {
    kind: 'fromQuestion';
    label: string;
};
export interface ExecutorListModel {
    rows: readonly ExecutorRow[];
    value: string;
    onChange: (id: string) => void;
}
export interface ContextRole {
    role: string;
    label: string;
}
export interface ContextUpload {
    kind: string;
    accept: string;
    hint: string;
    roles: readonly ContextRole[];
    send: (file: File) => Promise<Record<string, unknown>>;
}
export interface ContextNote {
    label: string;
    hint: string;
    clear: () => void;
}
export interface ContextKindApi {
    kinds: readonly string[];
    icon: (kind: string) => ReactNode;
    actions: (ctx: ContextKindCtx, s: KindState) => readonly ContextAction[];
    preview: (ctx: ContextKindCtx, item: ChatContextItem) => ReactNode;
    thumb?: (ctx: ContextKindCtx, item: ChatContextItem) => string | null;
    sub?: (ctx: ContextKindCtx, item: ChatContextItem) => string | null;
    editor?: (ctx: ContextKindCtx, item: ChatContextItem) => {
        label: string;
        hint: string;
        open: () => void;
    } | null;
    step?: (ctx: ContextKindCtx, item: ChatContextItem) => {
        prev: (() => void) | null;
        next: (() => void) | null;
    } | null;
    executors?: (ctx: ContextKindCtx, actionId: string, answer?: string | null) => ExecutorListModel | null;
    params?: (ctx: ContextKindCtx, actionId: string) => readonly LaunchParam[];
    format?: (ctx: ContextKindCtx, actionId: string | null) => FormatModel | null;
    refRoles?: (ctx: ContextKindCtx, primary: ChatContextPrimary, candidateKind: string) => readonly ContextRole[];
    upload?: (ctx: ContextKindCtx, primary: ChatContextPrimary) => ContextUpload | null;
    note?: (ctx: ContextKindCtx, primary: ChatContextPrimary) => ContextNote | null;
    feed?: (ctx: ContextKindCtx) => readonly {
        id: string;
        label: string;
        hint?: string;
        candidate: {
            kind: string;
            ref: Record<string, unknown>;
        };
    }[];
    create?: {
        title: string;
        hint?: string;
        icon: ReactNode;
        run: (ctx: ContextKindCtx) => void;
    };
    launch?: (ctx: ContextKindCtx, req: LaunchRequest) => Promise<LaunchHandle>;
    priceSalt?: (ctx: ContextKindCtx, actionId: string) => string;
    quote?: (ctx: ContextKindCtx, req: QuoteRequest) => Promise<ActionQuote>;
}
export type LaunchParams = Readonly<Record<string, string | number | boolean>>;
export interface QuoteRequest {
    op: string;
    text: string;
    params: LaunchParams;
    contextRevision: number;
    salt?: string;
}
export type LaunchRequest = QuoteRequest;
export interface ActionQuote {
    price: string;
    detail?: string;
}
export interface LaunchHandle {
    id: string;
    watch: (on: (e: {
        progress?: number;
        result?: ActionResult;
        error?: string;
        cancelled?: boolean;
    }) => void) => () => void;
    cancel?: () => void | Promise<void>;
}
export interface ActionResult {
    summary: string;
    open?: () => void;
}
export interface ContextReturn {
    prev: ChatContextItem;
    label: string;
}
export interface ActionPreset {
    actionId: string;
    prefill?: string;
    params?: LaunchParams;
}
export type RunState = 'idle' | 'running' | 'done' | 'error';
export interface ActionRun {
    action: ContextAction | null;
    label: string;
    labelParts: {
        name: string;
        tail: string;
    };
    quote: ActionQuote | null;
    state: RunState;
    progress: number | null;
    result: ActionResult | null;
    run: (text: string) => Promise<void>;
    stop: (() => void) | null;
    text: string;
    setText: (text: string) => void;
    answer: string | null;
    blocked: string | null;
    params: readonly LaunchParam[];
    setParam: (name: string, value: number | string) => void;
}
export {};

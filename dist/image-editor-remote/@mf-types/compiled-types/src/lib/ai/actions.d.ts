import type { ReactNode } from 'react';
import type { NavSnapshot } from '../nav';
export declare const AI_RUN_EVENT = "cc-ai-run";
export declare function dispatchAiRun(action: string): void;
export declare const OPEN_GLOBAL_SEARCH_EVENT = "cc-open-global-search";
export type AiSection = 'notes' | 'tasks' | 'chat' | 'global' | 'personas' | 'knowledge' | 'project' | 'wall';
export interface AiActionCtx {
    nav: NavSnapshot | null;
    online: boolean;
    flag: (key: string) => boolean;
    subsystem?: (key: string) => boolean;
    caps: {
        semantic: boolean;
    };
    chat: {
        active: boolean;
        hasMessages: boolean;
        tail?: string;
    };
    git?: {
        isRepo: boolean;
    };
}
export interface AiAction {
    id: string;
    title: string;
    hint: string;
    section: AiSection;
    sectionLabel: string;
    icon: ReactNode;
    when: (ctx: AiActionCtx) => boolean;
    contextual?: (ctx: AiActionCtx) => boolean;
    run: (ctx: AiActionCtx) => void | Promise<void>;
}
export declare const AI_ACTIONS: AiAction[];
export declare function runActionById(id: string, ctx: AiActionCtx): void;
export declare function rankedActions(ctx: AiActionCtx, query?: string): {
    action: AiAction;
    contextual: boolean;
}[];

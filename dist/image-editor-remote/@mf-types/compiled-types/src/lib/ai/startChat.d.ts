import type { AiActionCtx } from './actions';
import type { Project } from '../../types';
export declare function prefillComposer(text: string): void;
export declare function startChatFromPanel(text: string, opts?: {
    requiredTool?: string;
}): Promise<void>;
export declare function startChatInProject(text: string, project: Project, openProject: (p: Project) => void): Promise<boolean>;
export declare function startChatWithPrompt(text: string, ctx: AiActionCtx, opts?: {
    requiredTool?: string;
}): Promise<void>;

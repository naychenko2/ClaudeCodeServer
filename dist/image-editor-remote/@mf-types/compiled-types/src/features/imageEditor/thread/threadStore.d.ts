import type { Sample } from '../editorInputs';
import type { Mark, Tool } from '../marks';
import { type ImageThread, type ImageThreadsState } from './threadsApi';
export declare const imageDraftKey: (threadId: string) => string;
export type EditorTool = Tool | 'crop' | 'rotate' | 'resize';
declare function subscribe(fn: () => void): () => void;
export declare const subscribeThreadStore: typeof subscribe;
export declare function ensureThreads(projectId: string, sessionId: string): Promise<void>;
export declare function getThreadsState(sessionId: string | null): ImageThreadsState;
export declare function getFocusedThread(sessionId: string | null): ImageThread | null;
export declare function useThreads(projectId: string | null, sessionId: string | null): ImageThreadsState;
export declare function useThreadStoreVersion(): number;
export declare function mutate(projectId: string, sessionId: string, run: (revision: number) => Promise<ImageThreadsState>): Promise<boolean>;
export declare const focusThread: (projectId: string, sessionId: string, threadId: string | null) => Promise<boolean>;
export declare function getThreadMarks(threadId: string | null): {
    marks: Mark[];
    size: {
        w: number;
        h: number;
    } | null;
};
export declare function setThreadMarks(threadId: string, marks: Mark[], size: {
    w: number;
    h: number;
} | null): void;
export declare const isWholeImage: (threadId: string | null) => boolean;
export declare function setWholeImage(threadId: string, whole: boolean): void;
export declare function getEditor(): {
    sessionId: string;
    threadId: string;
    versionId: string | null;
    tool?: EditorTool;
} | null;
export declare function openEditor(sessionId: string, threadId: string, versionId?: string | null, opts?: {
    tool?: EditorTool;
}): void;
export declare function showEditorVersion(versionId: string | null): void;
export declare function closeEditor(): void;
export declare function getSamples(projectId: string | null): Sample[];
export declare function setSamples(projectId: string, samples: Sample[]): void;
export declare function __resetThreadStore(): void;
export declare function __applyThreads(sessionId: string, projectId: string, state: ImageThreadsState): void;
export {};

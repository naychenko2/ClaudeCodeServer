import { type ImageEncodeFormat } from '../api';
import { type ImageThread, type ImageThreadTake, type ImageThreadVersion } from './threadsApi';
export declare const savedStepOf: (threadId: string) => string | null;
export declare const isSavedStep: (stepId: string | null) => boolean;
export declare function versionSaved(t: ImageThread, v: ImageThreadVersion): boolean;
export declare function activeStepOf(t: ImageThread): string | null;
export type RevealMode = 'auto' | 'none';
export declare const continueFrom: (projectId: string, sessionId: string, t: ImageThread, versionId: string) => Promise<boolean>;
export declare const applyStep: (projectId: string, sessionId: string, t: ImageThread, stepId: string) => Promise<boolean>;
export declare function takeVariant(projectId: string, sessionId: string, t: ImageThread, what: ImageThreadTake): Promise<boolean>;
export declare const dismissJob: (projectId: string, sessionId: string, t: ImageThread, jobId: string) => Promise<boolean>;
export declare function rollbackTo(projectId: string, sessionId: string, t: ImageThread, stepId: string | null, label: string): Promise<boolean>;
export declare const workWithFile: (projectId: string, sessionId: string, file: string, how?: RevealMode) => Promise<boolean>;
export declare function createDraft(projectId: string, sessionId: string, folder: string, how?: RevealMode): Promise<boolean>;
export declare function saveToProject(projectId: string, sessionId: string, t: ImageThread, stepId?: string | null): Promise<string | null>;
export declare function saveAsInThread(projectId: string, sessionId: string, t: ImageThread, v: {
    folder: string;
    fileName: string;
}, format?: ImageEncodeFormat, stepId?: string | null): Promise<void>;

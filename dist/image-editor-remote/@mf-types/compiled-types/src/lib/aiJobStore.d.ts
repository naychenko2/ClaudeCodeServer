export type AiJobStatus = 'idle' | 'running' | 'done' | 'error';
export interface AiJobState<T> {
    status: AiJobStatus;
    result?: T;
    error?: string;
}
export declare function getAiJob<T>(key: string): AiJobState<T>;
export declare function useAiJob<T>(key: string): AiJobState<T>;
export declare function runAiJob<T>(key: string, fn: () => Promise<T>): void;
export declare function patchAiJobResult<T>(key: string, updater: (prev: T) => T): void;
export declare function resetAiJob(key: string): void;

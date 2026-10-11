import { type EditCost } from '../api';
export type JobPhase = 'run' | 'done' | 'cancel' | 'error' | 'lost';
export interface JobStatus {
    phase: JobPhase;
    variants: number[];
    count: number;
    cost: EditCost | null;
    charged: boolean | null;
    error: string | null;
    model: string | null;
    createdAt: number | null;
    run: number | null;
    runs: number | null;
    eta: number | null;
    runStartedAt: number | null;
    queuePosition: number | null;
    fallbackEta: number | null;
}
export declare function useJobStatus(projectId: string, jobId: string | null): {
    status: JobStatus | null;
    cancel: () => Promise<void>;
};
export declare const queueText: (position: number | null) => string;
export declare function progressPercent({ run, runs, etaSeconds, elapsed, fallbackEta, queued }: {
    run: number | null;
    runs: number | null;
    etaSeconds: number | null;
    elapsed: number;
    fallbackEta?: number | null;
    queued?: boolean;
}): number;
export declare function useProgress(status: JobStatus | null, running: boolean, fallbackEta?: number | null): {
    percent: number;
    queuePosition: number | null;
};
export declare function jobPercent(status: JobStatus | null, now: number, mounted: number, fallbackEta?: number | null, queued?: boolean): number;

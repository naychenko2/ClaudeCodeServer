import type { CreateTaskDto, Task, UpdateTaskDto } from '../types';
export type OutboxKind = 'create' | 'update' | 'delete';
export interface OutboxOp {
    taskId: string;
    seq: number;
    kind: OutboxKind;
    projectId?: string | null;
    dto: CreateTaskDto | UpdateTaskDto;
    baseUpdatedAt?: string;
    attempts: number;
    nextAttemptAt?: number;
    enqueuedAt: number;
}
interface StoreHooks {
    upsert: (t: Task) => void;
    remove: (id: string) => void;
    reload: () => Promise<void>;
}
export declare function configureOutbox(hooks: StoreHooks): void;
export declare function outboxHasPending(taskId: string): boolean;
type MergeResult = {
    kind: OutboxKind;
    dto: CreateTaskDto | UpdateTaskDto;
    projectId?: string | null;
} | 'drop';
export declare function mergeOps(prev: OutboxOp, incoming: {
    kind: OutboxKind;
    dto: CreateTaskDto | UpdateTaskDto;
}): MergeResult;
export declare function enqueue(taskId: string, kind: OutboxKind, dto: CreateTaskDto | UpdateTaskDto, opts?: {
    projectId?: string | null;
    baseUpdatedAt?: string;
}): Promise<void>;
export declare function drainTaskOutbox(): Promise<void>;
export declare function mergePending(serverList: Task[], current: Task[]): Promise<Task[]>;
export declare function buildLocalTask(id: string, projectId: string | null, dto: CreateTaskDto, order: number): Task;
export declare function applyUpdateLocally(task: Task, dto: UpdateTaskDto): Task;
export {};

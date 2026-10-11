import type { FileEntry, SyncMark } from '../types';
import './tasks';
interface SyncProgress {
    active: boolean;
    done: number;
    total: number;
}
export declare function getSyncProgress(): SyncProgress;
export declare function subscribeSyncProgress(fn: () => void): () => void;
export declare function syncCount(p: SyncProgress): string;
export declare function syncLabel(p: SyncProgress): string;
export declare function getMarks(projectId: string): SyncMark[];
export declare function isSyncing(projectId: string, path: string): boolean;
export declare function isDownloaded(projectId: string, path: string): boolean;
export declare function loadDownloadedSet(projectId: string): Promise<void>;
export declare function loadSyncMarks(projectId: string): Promise<void>;
export declare function useSyncMarks(projectId: string): SyncMark[];
export declare function computeSyncState(marks: SyncMark[], path: string): 'direct' | 'inherited' | null;
export declare function drainOfflineQueues(): Promise<void>;
export declare function runOfflineSnapshot(priorityProjectId?: string): Promise<void>;
export declare function syncProjectFiles(projectId: string): Promise<void>;
export declare function toggleSyncMark(projectId: string, entry: FileEntry): Promise<void>;
export {};

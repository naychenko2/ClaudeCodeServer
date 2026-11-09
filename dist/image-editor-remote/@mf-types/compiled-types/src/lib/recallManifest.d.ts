import type { RecallItem } from '../types';
export declare function setRecallManifest(sessionId: string, items: RecallItem[]): void;
export declare function useRecallManifest(sessionId: string | null): RecallItem[];

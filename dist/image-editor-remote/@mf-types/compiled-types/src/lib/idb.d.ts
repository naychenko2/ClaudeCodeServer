export interface CacheEntry<T = unknown> {
    data: T;
    savedAt: number;
}
export declare function idbGet<T = unknown>(key: string): Promise<CacheEntry<T> | undefined>;
export declare function idbSet<T = unknown>(key: string, entry: CacheEntry<T>): Promise<IDBValidKey>;
export declare function idbDelete(key: string): Promise<undefined>;
export declare function idbKeys(): Promise<string[]>;
export declare function outboxPut<T extends {
    taskId: string;
}>(op: T): Promise<IDBValidKey>;
export declare function outboxGet<T = unknown>(taskId: string): Promise<T | undefined>;
export declare function outboxDelete(taskId: string): Promise<undefined>;
export declare function outboxAll<T = unknown>(): Promise<T[]>;
export declare function noteContentPut<T extends {
    localKey: string;
}>(rec: T): Promise<IDBValidKey>;
export declare function noteContentGet<T = unknown>(localKey: string): Promise<T | undefined>;
export declare function noteContentDelete(localKey: string): Promise<undefined>;
export declare function noteContentAll<T = unknown>(): Promise<T[]>;
export declare function notesOutboxPut<T extends {
    opId: number;
}>(op: T): Promise<IDBValidKey>;
export declare function notesOutboxDelete(opId: number): Promise<undefined>;
export declare function notesOutboxAll<T = unknown>(): Promise<T[]>;
export declare function idbClear(): Promise<void>;

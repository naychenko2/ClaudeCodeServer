export declare const IMAGE_CONCURRENCY = 3;
export declare function createSemaphore(limit: number): <T>(fn: () => Promise<T>) => Promise<T>;
export declare const withImageLimit: <T>(fn: () => Promise<T>) => Promise<T>;

export declare function readStoredToken(): string | null;
export declare function isOnline(): boolean;
export declare function subscribeOnline(fn: () => void): () => void;
export type ConnectionDisplayState = 'online' | 'unstable' | 'offline';
export declare function getConnectionDisplayState(): ConnectionDisplayState;
export declare function subscribeConnectionDisplayState(fn: () => void): () => void;
export declare function setOnline(value: boolean): void;
export declare function setConnectionState(value: 'online' | 'offline'): void;
export declare function confirmOffline(): Promise<void>;
export declare class OfflineError extends Error {
    constructor(message?: string);
}
export declare class RequestTimeoutError extends Error {
    readonly timeoutMs: number;
    constructor(timeoutMs: number);
}
export declare function request<T>(url: string, options?: RequestInit & {
    timeoutMs?: number;
    parse?: 'json';
    live?: boolean;
}): Promise<T>;
export declare function request(url: string, options: RequestInit & {
    timeoutMs?: number;
    parse: 'blob';
    live?: boolean;
}): Promise<Blob>;
export declare function initConnectivity(): void;

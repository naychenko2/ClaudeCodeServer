import type { ContextReturn } from './types';
export declare function setContextReturn(sessionId: string, r: ContextReturn): void;
export declare function clearContextReturn(sessionId: string): void;
export declare const getContextReturn: (sessionId: string | null) => ContextReturn | null;
export declare function useContextReturn(sessionId: string | null): ContextReturn | null;
export declare function resetContextReturns(): void;

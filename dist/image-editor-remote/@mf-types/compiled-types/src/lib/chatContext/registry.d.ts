import type { ContextKindApi } from './types';
export declare function buildKindRegistry(contributions: readonly {
    name?: string;
    action?: ContextKindApi;
}[]): ReadonlyMap<string, ContextKindApi>;
export declare function getKindRegistry(): ReadonlyMap<string, ContextKindApi>;
export declare const getKindApi: (kind: string) => ContextKindApi | null;

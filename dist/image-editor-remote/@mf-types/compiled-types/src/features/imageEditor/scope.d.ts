export declare const PERSONAL_SCOPE = "personal";
export declare const imageScope: (projectId: string | null) => string;
export declare const isPersonalScope: (scope: string | null | undefined) => scope is "personal";
export declare function imageBase(scope: string, sessionId: string | null): string;
export declare const threadsBase: (scope: string, sessionId: string) => string;
export declare function enterScope(projectId: string | null, sessionId: string | null): string;
export declare const scopeBase: (scope: string) => string;
export declare function projectBase(scope: string): string;
export declare function __resetScope(): void;

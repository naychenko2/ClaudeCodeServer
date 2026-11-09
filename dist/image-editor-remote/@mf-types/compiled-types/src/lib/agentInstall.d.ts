export type AgentOs = 'windows' | 'linux';
export declare const INSECURE_AGENT_INSTALL_TEXT = "\u0423\u0441\u0442\u0430\u043D\u043E\u0432\u043A\u0430 \u0430\u0433\u0435\u043D\u0442\u0430 \u0434\u043E\u0441\u0442\u0443\u043F\u043D\u0430 \u0442\u043E\u043B\u044C\u043A\u043E \u043F\u043E HTTPS \u2014 \u043E\u0442\u043A\u0440\u043E\u0439\u0442\u0435 \u0432\u0435\u0431-\u0438\u043D\u0442\u0435\u0440\u0444\u0435\u0439\u0441 \u043F\u043E https-\u0430\u0434\u0440\u0435\u0441\u0443";
export declare function isSecureAgentOrigin(origin: string): boolean;
export declare function agentInstallCommand(os: AgentOs, origin: string, code: string): string;
export declare function guessAgentOs(userAgent?: string): AgentOs;
export type AgentManifestState = {
    kind: 'loading';
} | {
    kind: 'served';
    version: string;
} | {
    kind: 'unavailable';
    reason: string;
};
export declare function fetchAgentManifest(): Promise<AgentManifestState>;
export declare function isRootNotAllowed(text: string | null | undefined): boolean;
export declare function rootsAddCommand(path: string, platform: string | null | undefined): string;

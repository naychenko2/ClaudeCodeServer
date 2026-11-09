export interface ModelOption {
    value: string;
    label: string;
    description?: string;
    provider?: string;
    contextWindow?: number;
    curated?: boolean;
    resolvedVersion?: string;
}
export interface ProviderCapabilities {
    provider: string;
    displayName: string;
    supportsPlanMode: boolean;
    supportsCompact: boolean;
    supportsMcp: boolean;
    supportsEffort: boolean;
    supportsPermissionModes: boolean;
    supportsImages: boolean;
    supportsAgents: boolean;
    hasBalance?: boolean;
    configured?: boolean;
    tierStrong?: string | null;
    tierMedium?: string | null;
    tierWeak?: string | null;
}
export declare const CLAUDE_FAMILIES: readonly ["opus", "fable", "sonnet", "haiku"];
export type ClaudeFamily = typeof CLAUDE_FAMILIES[number];
export declare function modelFamily(value: string): string;
export declare const FALLBACK_MODELS: ModelOption[];
export declare const USAGE: {
    readonly chatNew: "chat-new";
    readonly chatPersona: "chat-persona";
    readonly tasksExecutor: "tasks-executor";
};
export type UsageKey = typeof USAGE[keyof typeof USAGE];
export declare function loadModels(): Promise<void>;
export declare function assignedModel(usage?: UsageKey): string;
export declare function defaultModelLabel(usage?: UsageKey): string;
export declare function defaultModelOption(usage?: UsageKey): ModelOption;
export declare function modelProvider(value?: string | null, usage?: UsageKey): string;
export declare function modelCaps(value?: string | null): ProviderCapabilities;
export type ModelChangePlan = {
    kind: 'migrate';
    model: string;
} | {
    kind: 'update';
};
export declare function planModelChange(model: string, chat: {
    model?: string | null;
    claudeSessionId?: string | null;
}, usage?: UsageKey): ModelChangePlan;
export declare function assistantName(value?: string | null): string;
export declare function providerLabel(key: string): string;
export declare function providerCapsByKey(key: string): ProviderCapabilities;
export declare function cliProviderKeys(): string[];
export interface ProviderInfo {
    key: string;
    caps: ProviderCapabilities;
}
export declare function getProviders(): ProviderInfo[];
export declare function useModelCaps(value?: string | null): ProviderCapabilities;
export declare function useProviders(): ProviderInfo[];
export declare function versionHint(option: Pick<ModelOption, 'resolvedVersion'>): string | undefined;
export declare function getModels(): ModelOption[];
export declare function subscribeModels(fn: () => void): () => void;
export declare function useModels(): ModelOption[];
export declare function useModelLabel(value?: string | null): string;
export declare function useDefaultModelOption(usage?: UsageKey): ModelOption;
export declare function modelLabel(value?: string | null): string;
export declare const DEFAULT_CONTEXT_WINDOW = 200000;
export declare function contextWindowFor(model?: string | null): number;

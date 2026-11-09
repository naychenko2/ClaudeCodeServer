import type { TierKey } from './modelProvidersShared';
import type { ScopedPreset, SpecialtySettingsResponse, SpecialtySettingsLayer, ModelRoutePreset, ModelPreviewResponse, PlacePresetRef, SubagentModelChip, ResetResult } from '../types';
export declare const PRESET_PREFIX = "preset:";
export declare function isPresetRoute(route: string | null | undefined): boolean;
export declare function presetIdOf(route: string | null | undefined): string | null;
export declare function presetRoute(id: string): string;
export declare function findPreset(presets: ScopedPreset[] | ModelRoutePreset[] | null | undefined, id: string | null | undefined): ModelRoutePreset | null;
export declare const FALLBACK_BUDGET_DEFAULT = 4;
export interface ChainLabelContext {
    tierModels: Record<TierKey, string>;
    ollamaModel?: string;
    ollamaProvider?: string;
}
export declare function chainStepLabel(step: string, ctx: ChainLabelContext): string;
export declare function chainSummary(preset: ModelRoutePreset, ctx: ChainLabelContext): string;
export declare function presetValueLabel(route: string, presets: ScopedPreset[] | ModelRoutePreset[] | null | undefined): string;
export declare function stepsWord(n: number): string;
export declare function substitutionsWord(n: number): string;
export declare function isBrokenPresetRoute(route: string | null | undefined, presets: ScopedPreset[] | ModelRoutePreset[] | null | undefined): boolean;
export declare function routeDisplayLabel(route: string | null | undefined, presets: ScopedPreset[] | ModelRoutePreset[] | null | undefined, ctx: ChainLabelContext): string;
export declare function cellPresetLabel(route: string | null | undefined, presets: ScopedPreset[] | ModelRoutePreset[] | null | undefined, ctx: ChainLabelContext): {
    label: string;
    title: string;
};
export declare function resolvePlacePreset(route: string | null | undefined, info: PlacePresetRef | null | undefined, presets: ScopedPreset[] | ModelRoutePreset[] | null | undefined, settingsLoaded: boolean): {
    preset: ModelRoutePreset | null;
    broken: boolean;
    presetId: string | null;
};
export declare function ensurePresetSettingsLoaded(): Promise<void>;
export declare function reloadPresetSettings(): void;
export declare function updateSpecialtySettings(s: SpecialtySettingsResponse): void;
export type LayerReducer = (current: SpecialtySettingsLayer) => SpecialtySettingsLayer;
type WriteScope = 'global' | 'owner' | 'user';
export declare function getSettingsError(): string | null;
export declare function saveLayer(scope: WriteScope, reducer: LayerReducer, userId?: string | null): Promise<void>;
export declare function resetLayer(scope: 'owner' | 'global', key?: string): Promise<ResetResult>;
interface SaveStateSnapshot {
    savingScope: WriteScope | null;
    savingUserId: string | null;
    settingsError: string | null;
    resettingScope: WriteScope | null;
}
export declare function useSaveState(): SaveStateSnapshot;
export declare function loadUserLayer(userId: string | null | undefined): Promise<void>;
export declare function getUserLayer(userId: string | null | undefined): SpecialtySettingsLayer | null;
export declare function hasUserLayer(userId: string | null | undefined): boolean;
export declare function getUserLayerError(userId: string | null | undefined): string | null;
export declare function useUserLayer(userId: string | null | undefined): SpecialtySettingsLayer | null;
export declare function commitUserLayer(userId: string, layer: SpecialtySettingsLayer): void;
export declare function rollbackUserLayer(userId: string, prevLayer: SpecialtySettingsLayer | undefined): void;
export declare function getSpecialtySettings(): SpecialtySettingsResponse | null;
export declare function useSpecialtySettings(): SpecialtySettingsResponse | null;
export declare function usePresets(): ScopedPreset[];
export declare function useSubstitutionBudget(): number;
export declare function isChainStepDimmed(index: number, budget: number): boolean;
export interface EffectiveLineContext {
    kind: 'specialty' | 'persona' | 'action';
    tier?: TierKey;
    specialtyKey?: string;
    personaId?: string;
    actionKey?: string;
}
export interface PreviewQuery {
    place?: string;
    personaId?: string;
    specialty?: string;
    tier?: string;
    sessionId?: string;
}
export declare function formatEffectiveLine(d: ModelPreviewResponse, opts?: {
    tierText?: string;
    prefix?: string;
}): string | null;
export declare function invalidateEffectiveLines(): void;
export declare function usePreview(ctx: EffectiveLineContext): ModelPreviewResponse | null;
export declare function useSubagentModelChip(personaId: string | null | undefined, sessionId: string | null | undefined): SubagentModelChip | null;
export declare function useEffectiveLine(ctx: EffectiveLineContext): string | null;
export declare function usePreviewTick(): number;
export declare function placesWord(n: number): string;
export {};

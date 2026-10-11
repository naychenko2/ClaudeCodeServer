import { type ModelTiers } from './api';
export type ModelTierKey = 'strong' | 'medium' | 'weak';
export declare const TIER_ORDER: ModelTierKey[];
export declare const TIER_TITLE: Record<ModelTierKey, string>;
export declare function parseTier(value?: string | null): ModelTierKey | '';
export declare function effectiveTierModel(t: ModelTierKey, own: ModelTiers | null, global: ModelTiers | null): string;
export type TierModels = Record<ModelTierKey, string>;
export declare function ensureTierModelsLoaded(): Promise<void>;
export declare function useTierModels(): TierModels;

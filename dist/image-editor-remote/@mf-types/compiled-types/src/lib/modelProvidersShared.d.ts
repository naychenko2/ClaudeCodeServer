import type { CSSProperties, Dispatch, SetStateAction } from 'react';
import { type ModelTiers } from './api';
import { type ProviderCapabilities, type ModelOption } from './models';
import type { OllamaUsageInfo, AppSettings, UserProfile } from '../types';
export declare const ROW_CLASS = "cc-mprov-row";
export declare const groupHeaderStyle: CSSProperties;
export declare function levelTitleStyle(): CSSProperties;
export declare const BRIEFING_KEY = "daily-briefing";
export type PresetKey = 'tiers' | 'tiers-local';
export type TierKey = 'strong' | 'medium' | 'weak';
export declare const TIERS: Record<TierKey, {
    title: string;
    hint: string;
    field: keyof AppSettings;
    route: string;
}>;
export declare const TIER_ORDER: TierKey[];
export declare function tierSubtitle(model: string, inheritedModel?: string | null): string;
export declare const DOT_COLOR: Record<'active' | 'inactive' | 'offline', string>;
export declare function hasTierTriple(caps: ProviderCapabilities): boolean;
export interface ProviderData {
    info: OllamaUsageInfo | undefined;
    setInfo: Dispatch<SetStateAction<OllamaUsageInfo | undefined>>;
    globalSettings: AppSettings | null;
    setGlobalSettings: Dispatch<SetStateAction<AppSettings | null>>;
    users: UserProfile[];
    ownTiers: ModelTiers | null;
    setOwnTiers: Dispatch<SetStateAction<ModelTiers | null>>;
    userTiers: ModelTiers | null;
    setUserTiers: Dispatch<SetStateAction<ModelTiers | null>>;
    selectedTiers: ModelTiers | null;
    globalTiers: ModelTiers | null;
    effectiveTierModel: (t: TierKey) => string;
}
export declare function useProviderData(isAdmin: boolean, contextUserId: string | null): ProviderData;
export interface ProviderTile {
    name: string;
    status: 'active' | 'inactive' | 'offline';
    statusLabel: string;
    count?: number;
}
export declare function buildProviderTiles(providers: {
    key: string;
    caps: ProviderCapabilities;
}[], models: ModelOption[], info: OllamaUsageInfo | undefined): ProviderTile[];
export declare function routeTier(route: string | null | undefined): TierKey | null;
export declare function routeLabel(route: string | null | undefined, ollamaModel?: string, tierModels?: Record<TierKey, string>): string;

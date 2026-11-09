import type { ModelTierValue, PersonaBindingMode, PersonaBindingType, SpecialtyCatalogEntry, SpecialtyDefaultBinding, SpecialtyPromptSectionsCatalog, SpecialtyPromptSectionsForSpecialty, SpecialtyPromptSectionMeta, SpecialtySettingsLayer, SpecialtyTemplateSettings } from '../types';
export declare function roleIconName(catalog: SpecialtyCatalogEntry | null | undefined, _key: string): string;
export declare function roleColorKey(catalog: SpecialtyCatalogEntry | null | undefined, _key: string): string;
export declare function hasRoleAvatar(roleKey: string): boolean;
export declare function roleAvatarUrl(roleKey: string): string | undefined;
export declare function ensureSpecialtiesLoaded(): Promise<void>;
export declare function reloadSpecialties(): void;
export declare function useSpecialtyCatalog(): SpecialtyCatalogEntry[] | null;
export declare const ANY_SPECIALTY = "any";
export declare function specialtyLabel(catalog: SpecialtyCatalogEntry[] | null, key: string): string;
export declare function emptyLayer(): SpecialtySettingsLayer;
export declare function cloneLayer(layer: SpecialtySettingsLayer): SpecialtySettingsLayer;
export declare function newPresetId(): string;
export declare function withNewPreset(baseLayer: SpecialtySettingsLayer, id: string, name: string, steps: string[]): SpecialtySettingsLayer;
export declare function withTierCell(layer: SpecialtySettingsLayer, key: string, tier: 'strong' | 'medium' | 'weak', value: string, template?: SpecialtyCatalogEntry['template']): SpecialtySettingsLayer;
export declare function mergePresetIntoCell(freshLayer: SpecialtySettingsLayer, key: string, tier: 'strong' | 'medium' | 'weak', presetId: string, template?: SpecialtyCatalogEntry['template']): SpecialtySettingsLayer;
export declare function withDefaultTier(layer: SpecialtySettingsLayer, key: string, tier: ModelTierValue | '', template?: SpecialtyCatalogEntry['template']): SpecialtySettingsLayer;
export declare function effectiveDefaultTier(globalLayer: SpecialtySettingsLayer, ownerLayer: SpecialtySettingsLayer, key: string): {
    tier: ModelTierValue;
    source: 'owner' | 'global';
} | null;
export declare function effectiveSpecialtyRecord(globalLayer: SpecialtySettingsLayer, ownerLayer: SpecialtySettingsLayer, key: string): SpecialtyTemplateSettings | null;
export type PromptSectionSource = 'code' | 'global' | 'user' | 'owner';
export interface EffectivePromptSection {
    id: string;
    enabled: boolean;
    text: string;
    enabledSource: PromptSectionSource;
    textSource: PromptSectionSource;
}
export declare function loadPromptSectionsCatalog(): Promise<SpecialtyPromptSectionsCatalog | null>;
export declare function getPromptSectionsCatalog(): SpecialtyPromptSectionsCatalog | null;
export declare function effectivePromptSection(catalog: SpecialtyPromptSectionsCatalog | null, ownerLayer: SpecialtySettingsLayer | null, userLayer: SpecialtySettingsLayer | null, globalLayer: SpecialtySettingsLayer | null, specialtyKey: string, sectionId: string): EffectivePromptSection;
export declare function withPromptSection(layer: SpecialtySettingsLayer, specialtyKey: string, sectionId: string, patch: {
    enabled?: boolean;
    text?: string | null;
    override?: false;
}): SpecialtySettingsLayer;
export declare function withoutPromptSection(layer: SpecialtySettingsLayer, specialtyKey: string, sectionId: string): SpecialtySettingsLayer;
export declare function withDefaultBindings(layer: SpecialtySettingsLayer, specialtyKey: string, bindings: SpecialtyDefaultBinding[] | null): SpecialtySettingsLayer;
export declare function ensureSpecialtyRecord(layer: SpecialtySettingsLayer, specialtyKey: string): SpecialtyTemplateSettings;
export declare function newDefaultBinding(type: PersonaBindingType, condition: string, mode?: PersonaBindingMode, skillName?: string | null): SpecialtyDefaultBinding;
export declare function newDefaultBindingId(): string;
export declare function sectionsOf(catalog: SpecialtyPromptSectionsCatalog | null): SpecialtyPromptSectionMeta[];
export declare function sectionsForSpecialty(catalog: SpecialtyPromptSectionsCatalog | null, specialtyKey: string): SpecialtyPromptSectionsForSpecialty | null;

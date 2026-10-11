import { type TierKey } from '../lib/modelProvidersShared';
import type { ModelOption } from '../lib/models';
import type { LayerReducer } from '../lib/presets';
import type { SpecialtySettingsLayer } from '../types';
export declare function RoutePicker({ route, label, models, tierModels, ollamaModel, ollamaProvider, allowLocal, busy, readOnly, onChange, placeholder, cardTitle, title, showTiers, showPresets, presetCreation, presetScope, manual, fullWidth, dashed, }: {
    route: string;
    label: string;
    models: ModelOption[];
    tierModels: Record<TierKey, string>;
    ollamaModel?: string;
    ollamaProvider?: string;
    allowLocal?: boolean;
    busy?: boolean;
    readOnly?: boolean;
    onChange: (route: string) => void;
    placeholder?: string;
    cardTitle?: string;
    title?: string;
    showTiers?: boolean;
    showPresets?: boolean;
    presetCreation?: {
        savingScope: 'global' | 'owner' | 'user' | null;
        onSaveLayer: (scope: 'global' | 'owner' | 'user', reducer: LayerReducer, userId?: string | null) => Promise<void>;
        onCreated?: (presetId: string, scope: 'global' | 'owner' | 'user', layer: SpecialtySettingsLayer) => void;
    };
    presetScope?: 'global' | 'owner' | 'user';
    manual?: {
        models?: ModelOption[];
        presetIds?: string[];
    };
    fullWidth?: boolean;
    dashed?: boolean;
}): import("react").JSX.Element;

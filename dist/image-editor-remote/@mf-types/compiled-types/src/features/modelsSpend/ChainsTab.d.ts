import type { LayerReducer } from '../../lib/presets';
import type { ModelOption } from '../../lib/models';
import type { TierKey } from '../../lib/modelProvidersShared';
type ChainScope = 'global';
export declare function ChainsTab({ isAdmin, contextUserId, savingScope, onSaveLayer, onReloadSettings, models, tierModels, ollamaModel, ollamaProvider }: {
    isAdmin: boolean;
    contextUserId: string | null;
    savingScope: 'global' | 'owner' | 'user' | null;
    onSaveLayer: (scope: ChainScope, reducer: LayerReducer, userId?: string | null) => Promise<void>;
    onReloadSettings: () => void;
    models: ModelOption[];
    tierModels: Record<TierKey, string>;
    ollamaModel?: string;
    ollamaProvider?: string;
}): import("react").JSX.Element;
export {};

import { type ProviderData, type TierKey } from '../../lib/modelProvidersShared';
import type { LayerReducer } from '../../lib/presets';
import { type ModelOption } from '../../lib/models';
interface ApplyTabProps {
    isAdmin: boolean;
    data: ProviderData;
    models: ModelOption[];
    tierModels: Record<TierKey, string>;
    savingScope: 'global' | 'owner' | 'user' | null;
    onSaveLayer: (scope: 'global' | 'owner' | 'user', reducer: LayerReducer, userId?: string | null) => Promise<void>;
}
export declare function ApplyTab({ isAdmin, data, models, tierModels, savingScope, onSaveLayer }: ApplyTabProps): import("react").JSX.Element;
export {};

import { type ProviderData, type TierKey } from '../../lib/modelProvidersShared';
import type { LayerReducer } from '../../lib/presets';
import { type ModelOption } from '../../lib/models';
interface SlotsTabProps {
    isAdmin: boolean;
    data: ProviderData;
    contextUserId: string | null;
    onContextUserId: (id: string | null) => void;
    models: ModelOption[];
    tierModels: Record<TierKey, string>;
    ollamaModel?: string;
    ollamaProvider?: string;
    savingScope: 'global' | 'owner' | 'user' | null;
    savingUserId: string | null;
    onSaveLayer: (scope: 'global' | 'owner' | 'user', reducer: LayerReducer, userId?: string | null) => Promise<void>;
    meUserId: string | null;
    pendingDraft?: boolean;
    onPendingDraftConsumed?: () => void;
}
export declare function SlotsTab({ isAdmin, data, contextUserId, onContextUserId, models, tierModels, ollamaModel, ollamaProvider, savingScope, onSaveLayer, meUserId, pendingDraft, onPendingDraftConsumed }: SlotsTabProps): import("react").JSX.Element;
export {};

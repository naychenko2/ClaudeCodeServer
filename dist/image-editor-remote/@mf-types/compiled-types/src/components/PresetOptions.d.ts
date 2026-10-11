import { type ChainLabelContext, type LayerReducer } from '../lib/presets';
import type { ModelOption } from '../lib/models';
import type { SpecialtySettingsLayer } from '../types';
export interface PresetCreationCtx {
    models: ModelOption[];
    savingScope: 'global' | 'owner' | 'user' | null;
    contextUserId?: string | null;
    onSaveLayer: (scope: 'global' | 'owner' | 'user', reducer: LayerReducer, userId?: string | null) => Promise<void>;
    onCreated?: (presetId: string, scope: 'global' | 'owner' | 'user', layer: SpecialtySettingsLayer) => void;
}
export declare function PresetOptions({ value, onPick, ctx, scope, creation, onEditingChange }: {
    value: string;
    onPick: (route: string) => void;
    ctx: ChainLabelContext;
    scope?: 'global' | 'owner' | 'user';
    creation?: PresetCreationCtx;
    onEditingChange?: (editing: boolean) => void;
}): import("react").JSX.Element | null;

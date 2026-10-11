import { type TierKey } from '../lib/modelProvidersShared';
import type { ModelOption } from '../lib/models';
export declare function ChainStepsEditor({ steps, onChange, models, tierModels, ollamaModel, ollamaProvider, readOnly, busy, maxSteps, addLabel }: {
    steps: string[];
    onChange: (steps: string[]) => void;
    models: ModelOption[];
    tierModels: Record<TierKey, string>;
    ollamaModel?: string;
    ollamaProvider?: string;
    readOnly?: boolean;
    busy?: boolean;
    maxSteps?: number;
    addLabel?: string;
}): import("react").JSX.Element;

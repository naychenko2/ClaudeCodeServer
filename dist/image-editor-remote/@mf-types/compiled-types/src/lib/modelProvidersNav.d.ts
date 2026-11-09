import type { ModelsSpendTab } from '../features/modelsSpend/ModelsSpendModal';
export declare function requestOpenModelsSpend(tab: ModelsSpendTab): void;
export declare function requestNewPreset(): void;
export declare function subscribeModelProvidersNav(fn: () => void): () => void;
export declare function hasPendingOpen(): boolean;
export declare function consumeOpenRequest(): ModelsSpendTab | null;
export declare function consumeDraftRequest(): boolean;

import type { LoraVersionsHandle } from './useLoraVersions';
import { type TrainSectionView } from './loraTrainView';
export declare function useCharacterTrain(projectId: string, slug: string, photoCount: number | null, versions: LoraVersionsHandle): {
    view: TrainSectionView;
    training: {
        start: ((trigger?: string) => Promise<boolean>) | undefined;
        cancel: (() => Promise<boolean>) | undefined;
        refresh: (() => Promise<void>) | undefined;
        view: import("./useLoraTraining").LoraTrainView;
        state: import("../api").LoraTrainStateDto | null;
        error: import("./loraErrors").LoraFailure | null;
        busy: boolean;
    };
    trigger: string;
    edited: string | null;
    setTrigger: import("react").Dispatch<import("react").SetStateAction<string | null>>;
    dismiss: import("react").Dispatch<import("react").SetStateAction<string | null>>;
    isAdmin: boolean;
    actionError: string | null;
    connectError: string | null;
};

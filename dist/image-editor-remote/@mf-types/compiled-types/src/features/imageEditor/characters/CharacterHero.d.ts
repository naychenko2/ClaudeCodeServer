import type { CardAction } from './characterCardView';
import type { useCharacterTrain } from './useCharacterTrain';
import type { LoraVersionsHandle } from './useLoraVersions';
export declare function CharacterHero({ action, train, versions, slug, projectId, hasConnected, contextSessionId, disabled, onOpenPhotos, onOpenTune, }: {
    action: CardAction;
    train: ReturnType<typeof useCharacterTrain>;
    versions: LoraVersionsHandle;
    slug: string;
    projectId: string;
    hasConnected: boolean;
    contextSessionId: string | null;
    disabled?: boolean;
    onOpenPhotos: (section?: 'face' | 'train') => void;
    onOpenTune: () => void;
}): import("react").JSX.Element;

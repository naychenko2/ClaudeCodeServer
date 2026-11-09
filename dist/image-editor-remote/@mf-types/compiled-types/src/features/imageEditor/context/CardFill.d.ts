import { type ChatContextPrimary } from 'aihome_shell/kit';
import type { ImageThread } from '../thread/threadsApi';
export declare function primaryVersion(primary: ChatContextPrimary, thread: ImageThread): string | null;
export interface CardFill {
    working: boolean;
    byAgent: boolean;
    threadWorking: boolean;
    work: () => Promise<boolean>;
    pick: () => Promise<void>;
}
export declare function useCardFill(sessionId: string, thread: ImageThread, versionId: string | null): CardFill;
export declare function useFocusedThreadId(sessionId: string): string | null;
export declare function CardContextActions({ sessionId, projectId, thread, versionId, fill, size }: {
    sessionId: string;
    projectId: string;
    thread: ImageThread;
    versionId: string | null;
    fill: CardFill;
    size?: 'xs' | 'sm';
}): import("react").JSX.Element | null;

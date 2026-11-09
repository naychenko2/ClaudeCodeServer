import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { ImageThread } from './threadsApi';
export declare function JobBlock({ projectId, sessionId, thread, inEditor, onPreview }: {
    projectId: string;
    sessionId: string;
    thread: ImageThread;
    inEditor?: boolean;
    onPreview?: (v: {
        jobId: string;
        variant: number;
    } | null) => void;
}): import("react").JSX.Element;
export declare function ThreadAnchor({ ctx }: {
    ctx: ChatItemToolCtx;
}): import("react").JSX.Element | null;

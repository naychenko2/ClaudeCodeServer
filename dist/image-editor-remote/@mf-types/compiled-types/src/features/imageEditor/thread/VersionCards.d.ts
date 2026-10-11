import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import type { ImageThread, ImageThreadVersion } from './threadsApi';
export declare function foldLaunch(versions: ImageThreadVersion[], workingId: string | null): {
    best: ImageThreadVersion;
    rest: ImageThreadVersion[];
    workingInRest: boolean;
} | null;
export declare function VersionCard({ projectId, sessionId, thread, version, focused, model, cropNote, best }: {
    projectId: string;
    sessionId: string;
    thread: ImageThread;
    version: ImageThreadVersion;
    focused: boolean;
    model?: string | null;
    cropNote?: string | null;
    best?: boolean;
}): import("react").JSX.Element;
export declare function OriginAnchor({ projectId, sessionId, thread, focused }: {
    projectId: string;
    sessionId: string;
    thread: ImageThread;
    focused: boolean;
}): import("react").JSX.Element | null;
export declare function LaunchAnchor({ ctx }: {
    ctx: ChatItemToolCtx;
}): import("react").JSX.Element | null;

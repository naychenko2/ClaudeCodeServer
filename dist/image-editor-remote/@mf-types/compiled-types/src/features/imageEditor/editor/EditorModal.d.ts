import { type EditorTool } from '../thread/threadStore';
export declare function EditorModal({ projectId, sessionId, threadId, versionId, startTool }: {
    projectId: string;
    sessionId: string;
    threadId: string;
    versionId?: string | null;
    startTool?: EditorTool;
}): import("react").JSX.Element | null;

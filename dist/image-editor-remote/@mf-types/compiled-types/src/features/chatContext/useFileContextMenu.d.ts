import type { ReactNode } from 'react';
import type { ChatContextDto } from '../../lib/chatContext/types';
import { type ContextOpenerApi } from '../../lib/subsystems/registryCore';
export declare const PROJECT_FILE_KIND = "project-file";
export declare function useFileContextMenu(projectId: string, path: string | null, online: boolean, close: () => void): {
    on: boolean;
    items: ReactNode[];
};
export declare function fileContextItems({ projectId, sessionId, path, state, opener, close }: {
    projectId: string;
    sessionId: string;
    path: string;
    state: ChatContextDto;
    opener: ContextOpenerApi | undefined;
    close: () => void;
}): ReactNode[];

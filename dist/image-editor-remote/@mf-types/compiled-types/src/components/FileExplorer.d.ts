import type { Project } from '../types';
interface Props {
    project: Project;
    onOpenFile: (path: string) => void;
    activeFilePath?: string | null;
    isMobile?: boolean;
    alwaysShowIcons?: boolean;
    onAddToKnowledge?: (relativePath: string) => void;
    onAddFolderToKnowledge?: (relativePath: string) => void;
    indexedFileNames?: Set<string>;
    indexingFiles?: Set<string>;
    indexingFolders?: Set<string>;
    onAttachToChat?: (path: string) => void;
    onRemoveFromKnowledge?: (relativePath: string) => void;
    onOpenDossiers?: (path: string) => void;
}
/** Возвращает текущую папку для создания файлов в проводнике (используется из ChatPanel). */
export declare function getExplorerCreateInDir(projectId: string): string;
export declare function FileExplorer(props: Props): import("react").JSX.Element;
export {};

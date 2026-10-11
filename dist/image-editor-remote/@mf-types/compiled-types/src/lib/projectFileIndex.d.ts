export type ProjectFileIndex = ReadonlyMap<string, string>;
export declare function useProjectFileIndex(projectId: string | null): ProjectFileIndex;
export declare const FILE_LIKE_MENTION: RegExp;
export declare function lookupProjectFile(index: ProjectFileIndex, raw: string, rootPath: string): string | null;

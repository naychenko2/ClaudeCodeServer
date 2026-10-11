export declare function toRelative(raw: string, rootPath: string): string | null;
export declare function relPath(p: string, root?: string | null): string;
export declare function middleEllipsis(name: string, max?: number): string;
export declare function basename(p: string): string;
export declare function stripRoot(text: string, root?: string | null): string;
export declare function relPathTree(p: string, rootPath?: string | null, treePath?: string | null): string;
export declare function stripRootTree(text: string, rootPath?: string | null, treePath?: string | null): string;

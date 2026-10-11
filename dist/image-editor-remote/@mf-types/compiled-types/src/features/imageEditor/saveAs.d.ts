import type { FileEntry } from '../../types';
import type { ImageEncodeFormat, SaveCheckResponse } from './api';
export declare const FORMAT_EXT: Record<ImageEncodeFormat, string>;
export declare function nameStem(input: string): string;
export declare function defaultStem(sourceName: string): string;
export declare function suggestionStem(suggestion: string): string;
export interface SaveFolder {
    path: string;
    files: number;
}
export declare function projectFolders(entries: FileEntry[]): SaveFolder[];
export interface SaveCheckState {
    key: string;
    result: SaveCheckResponse | null;
    error: string | null;
}
export declare const checkKey: (folder: string, stem: string, format: ImageEncodeFormat) => string;
export declare function saveBlocked(stem: string, key: string, check: SaveCheckState | null): boolean;

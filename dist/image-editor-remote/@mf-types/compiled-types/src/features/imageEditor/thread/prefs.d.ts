import type { ImageThreadSettings } from './threadsApi';
import type { EditMode, ImageEditOp } from '../api';
import type { OutpaintRatio } from '../editorInputs';
import type { ImageMode } from './modeState';
import type { MediaFormatWish } from 'aihome_shell/kit';
export interface ImageCreatePrefs {
    provider: string | null;
    model: string | null;
    count: number | null;
}
export interface ImageEditPrefs {
    provider: string | null;
    model: string | null;
    count: number | null;
    op: Exclude<ImageEditOp, 'generate'> | null;
    editMode: EditMode | null;
    ratio: OutpaintRatio | null;
}
export interface ProjectPrefs extends ImageThreadSettings {
    characterSlug: string | null;
    create?: ImageCreatePrefs | null;
    edit?: ImageEditPrefs | null;
    format?: MediaFormatWish | null;
}
export interface ImagePrefsChangedEvent {
    type: 'image_prefs_changed';
    projectId: string;
    prefs: ProjectPrefs;
}
export declare const prefsApi: {
    get: (projectId: string) => Promise<ProjectPrefs>;
    put: (projectId: string, prefs: ProjectPrefs) => Promise<ProjectPrefs>;
    subscribe: (handler: (e: ImagePrefsChangedEvent) => void) => () => void;
};
export declare const formatSig: (f: MediaFormatWish | null | undefined) => string;
export declare function getPrefs(projectId: string): ProjectPrefs;
export declare function prefsSettled(projectId: string): Promise<void>;
export declare function setPrefs(projectId: string, patch: Partial<ProjectPrefs>): void;
export declare function ensurePrefs(projectId: string): Promise<void>;
export declare const subscribePrefs: (fn: () => void) => () => void;
export declare function usePrefs(projectId: string): ProjectPrefs;
export declare function modeSettings(mode: ImageMode, prefs: ProjectPrefs, own: ImageThreadSettings | null | undefined): ImageThreadSettings;
export declare function setModeSettings(projectId: string, mode: ImageMode, patch: Partial<ImageThreadSettings>): void;
export declare function __resetPrefs(): void;

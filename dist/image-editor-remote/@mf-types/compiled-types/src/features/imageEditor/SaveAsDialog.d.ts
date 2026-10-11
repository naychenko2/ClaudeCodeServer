import { type ImageEncodeFormat, type SaveCheckResponse } from './api';
export interface HeavyFile {
    bytes: number;
    webpBytes: number | null;
    compress: boolean;
    onCompress: (on: boolean) => void;
}
export declare function SaveAsDialog({ projectId, sourcePath, defaultName, folder, format, onCheck, onSave, onClose, heavy }: {
    projectId: string;
    sourcePath: string | null;
    defaultName: string;
    folder: string;
    format: ImageEncodeFormat;
    onCheck: (folder: string, name: string) => Promise<SaveCheckResponse>;
    onSave: (v: {
        folder: string;
        fileName: string;
    }) => Promise<void>;
    onClose: () => void;
    heavy?: HeavyFile | null;
}): import("react").JSX.Element;

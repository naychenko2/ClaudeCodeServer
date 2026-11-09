export type ImageMode = 'create' | 'edit';
export declare function getStoredImageMode(sessionId: string | null): ImageMode | null;
export declare function setImageMode(sessionId: string, mode: ImageMode): void;
export declare const effectiveImageMode: (stored: ImageMode | null, hasImage: boolean) => ImageMode;
export declare function useImageModeVersion(): number;
export declare function __resetImageModes(): void;

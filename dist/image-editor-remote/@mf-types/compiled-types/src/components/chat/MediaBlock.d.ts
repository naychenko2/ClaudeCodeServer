export declare function mediaLabel(items: MediaItem[]): string;
export type MediaItem = {
    kind: 'image';
    url: string;
    width?: number;
    height?: number;
    fileName?: string;
} | {
    kind: 'video';
    url: string;
    width?: number;
    height?: number;
    duration?: number;
    fileName?: string;
} | {
    kind: 'audio';
    url: string;
    duration?: number;
    fileName?: string;
};
export declare function isLocalStreamUrl(url: string): boolean;
export declare function mediaSrc(url: string): string;
type Json = null | boolean | number | string | Json[] | {
    [key: string]: Json;
};
export declare function classifyUrl(item: Json | undefined): 'image' | 'video' | 'audio' | null;
export declare function extractMediaFromResult(result: string): MediaItem[];
export type MediaSource = 'fal' | 'glif' | 'higgsfield' | 'local';
export interface MediaMeta {
    model?: string;
    inferenceTime?: number;
    source?: MediaSource;
    outputType?: string;
    jobId?: string;
    costUsd?: number;
}
export declare function extractMediaMeta(result: string, media?: MediaItem[]): MediaMeta;
export declare function MediaBlock({ m, filename, model, inferenceTime, costUsd, costPending, credits, source, outputType, online, }: {
    m: MediaItem;
    filename: string;
    model?: string;
    inferenceTime?: number;
    costUsd?: number;
    costPending?: boolean;
    credits?: number;
    source?: MediaSource;
    outputType?: string;
    online?: boolean;
}): import("react").JSX.Element;
export {};

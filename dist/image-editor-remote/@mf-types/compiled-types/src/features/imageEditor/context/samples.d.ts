import { type ChatContextDto, type ChatContextRef } from 'aihome_shell/kit';
export declare const CHARACTER_KIND = "image-character";
export declare const FILE_KIND = "project-file";
export declare const SAMPLE_ACCEPT = "image/png,image/jpeg,image/webp";
export declare const sampleRefs: (state: ChatContextDto) => ChatContextRef[];
export declare const characterRefs: (state: ChatContextDto) => ChatContextRef[];
export declare const characterSlugOf: (state: ChatContextDto) => string | null;
export declare function contextInputCounts(sessionId: string): {
    references: number;
    hasCharacter: boolean;
};
export declare function uploadSampleRef(scope: string, file: File): Promise<Record<string, unknown>>;
export declare function addUploadSamples(scope: string, sessionId: string, files: File[], role: string): Promise<void>;
export declare const addProjectSample: (sessionId: string, path: string, role: string) => Promise<import("../../../lib/chatContext/store").MutateResult>;
export declare function changeSampleRole(sessionId: string, item: ChatContextRef, role: string): Promise<void>;
export declare function setCharacterRef(sessionId: string, slug: string | null): Promise<void>;

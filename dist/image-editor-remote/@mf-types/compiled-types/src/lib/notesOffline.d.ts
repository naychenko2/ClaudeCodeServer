import type { CreateNoteDto, NoteDetail, NoteSummary } from '../types';
export interface NoteRecord {
    localKey: string;
    serverId: string | null;
    source: string;
    path: string;
    title: string;
    content: string;
    tags: string[];
    baseUpdatedAt: string | null;
    dirty: boolean;
    createdOffline: boolean;
    deletedOffline: boolean;
    localUpdatedAt: number;
}
type NoteOpKind = 'create' | 'update' | 'delete';
interface NotePayload {
    title?: string;
    content?: string;
    source?: string;
    folder?: string;
}
export interface NotesOutboxOp {
    opId: number;
    localKey: string;
    kind: NoteOpKind;
    payload: NotePayload;
    baseUpdatedAt?: string | null;
    attempts: number;
}
export declare function getNoteForView(id: string): Promise<NoteDetail>;
export type NoteMerge = {
    kind: NoteOpKind;
    payload: NotePayload;
} | 'drop';
export declare function mergeNoteOps(last: NotesOutboxOp, kind: NoteOpKind, payload: NotePayload): NoteMerge;
export declare function saveNoteOffline(id: string, patch: {
    content: string;
    title?: string;
}): Promise<void>;
export declare function createNoteOffline(dto: CreateNoteDto): Promise<string>;
export declare function deleteNoteOffline(id: string): Promise<void>;
export declare function drainNotesOutbox(): Promise<boolean>;
export declare function overlayNotesList(serverList: NoteSummary[]): Promise<NoteSummary[]>;
export declare function sliceFragment(content: string, anchor: string): string;
export declare function offlineResolve(name: string, anchor?: string): Promise<{
    title: string;
    content: string;
} | null>;
export declare function warmNote(d: NoteDetail): Promise<void>;
export declare function hasPendingNotes(): Promise<boolean>;
export {};

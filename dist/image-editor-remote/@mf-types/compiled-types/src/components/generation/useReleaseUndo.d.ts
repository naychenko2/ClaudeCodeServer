export declare const RELEASE_UNDO_MS = 4000;
export interface ReleaseOffer<T> {
    snapshot: T;
    text: string;
    until?: number;
}
export interface ReleaseUndoController<T> {
    current(): ReleaseOffer<T> | null;
    subscribe(fn: () => void): () => void;
    release(offer: ReleaseOffer<T>, byHuman: boolean): void;
    undo(): T | null;
    dismiss(): void;
    dispose(): void;
}
export declare function createReleaseUndo<T>(ms?: number): ReleaseUndoController<T>;
export declare function useReleaseUndo<T>(ms?: number): {
    offer: ReleaseOffer<T> | null;
    release: (offer: ReleaseOffer<T>, byHuman: boolean) => void;
    undo: () => T | null;
    dismiss: () => void;
};

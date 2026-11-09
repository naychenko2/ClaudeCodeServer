import type * as ToneNs from 'tone';
import type { MidiDoc } from './midiModel';
type Tone = typeof ToneNs;
type ToneLoader = () => Promise<Tone>;
export type MidiPlayerState = {
    ownerId: string | null;
    playing: boolean;
    positionSec: number;
};
export type PlayOptions = {
    fromSec?: number;
    muted?: Set<number>;
};
export declare function play(ownerId: string, doc: MidiDoc, opts?: PlayOptions): Promise<void>;
export declare function pause(ownerId: string): void;
export declare function stop(ownerId: string): void;
export declare function seek(ownerId: string, sec: number): void;
export declare function getState(): MidiPlayerState;
export declare function subscribe(fn: (s: MidiPlayerState) => void): () => void;
export declare function setToneLoaderForTests(next: ToneLoader | null): void;
export {};

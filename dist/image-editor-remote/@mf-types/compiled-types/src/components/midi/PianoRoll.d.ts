import type { MidiDoc, MidiNote } from './midiModel';
export type PianoRollProps = {
    doc: MidiDoc;
    positionSec: number;
    muted: Set<number>;
    pxPerSec: number;
    height: number;
    reveal?: {
        sec: number;
        key: number;
    };
    onSeek(sec: number): void;
};
export declare const RULER_H = 20;
export declare const rollKeyWidth: (isMobile: boolean) => 40 | 56;
export declare const DRUM_NAMES: Record<number, string>;
export declare const isBlackKey: (pitch: number) => boolean;
export type RollLayout = {
    keyW: number;
    pxPerSec: number;
    scrollLeft: number;
    viewW: number;
    lowPitch: number;
    highPitch: number;
    rowH: number;
};
export declare function makeLayout(doc: Pick<MidiDoc, 'minPitch' | 'maxPitch' | 'noteCount'>, opts: {
    keyW: number;
    pxPerSec: number;
    scrollLeft: number;
    viewW: number;
    height: number;
}): RollLayout;
export declare const timeToX: (l: RollLayout, sec: number) => number;
export declare const xToTime: (l: RollLayout, x: number) => number;
export declare const pitchToY: (l: RollLayout, pitch: number) => number;
export declare function visibleWindow(l: RollLayout): [number, number];
export declare const noteVisible: (n: MidiNote, [t0, t1]: [number, number]) => boolean;
export declare const noteSounding: (n: MidiNote, sec: number) => boolean;
export declare function PianoRoll({ doc, positionSec, muted, pxPerSec, height, reveal, onSeek }: PianoRollProps): import("react").JSX.Element;

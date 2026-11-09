export type MidiNote = {
    midi: number;
    time: number;
    duration: number;
    velocity: number;
};
export type GmFamily = 'piano' | 'keys' | 'strings' | 'pad' | 'brass' | 'lead' | 'bass' | 'drums';
export type MidiTrackView = {
    index: number;
    name: string;
    channel: number;
    isDrums: boolean;
    program: number;
    family: GmFamily;
    notes: MidiNote[];
};
export type MidiDoc = {
    durationSec: number;
    bpm: number;
    timeSig: [number, number];
    bars: number[];
    tracks: MidiTrackView[];
    minPitch: number;
    maxPitch: number;
    noteCount: number;
};
export declare class MidiParseError extends Error {
    constructor(message: string);
}
export declare function gmFamily(program: number, isDrums: boolean): GmFamily;
export declare function parseMidi(bytes: ArrayBuffer): MidiDoc;

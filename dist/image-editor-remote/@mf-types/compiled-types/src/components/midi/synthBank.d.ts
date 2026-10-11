import type * as ToneNs from 'tone';
import type { GmFamily, MidiNote } from './midiModel';
type Tone = typeof ToneNs;
type Disposable = {
    dispose(): unknown;
};
type MelodicVoice = {
    synth: ToneNs.PolySynth;
    transpose: number;
};
type DrumKit = {
    kick: ToneNs.MembraneSynth;
    noise: ToneNs.NoiseSynth;
    lastNoiseTime: number;
};
export type SynthBank = {
    melodic: Map<GmFamily, MelodicVoice>;
    drums: DrumKit | null;
    nodes: Disposable[];
};
export declare function createSynths(Tone: Tone, families: GmFamily[]): SynthBank;
export declare function playNote(bank: SynthBank, family: GmFamily, note: MidiNote, time: number): void;
export declare function disposeSynths(bank: SynthBank): void;
export {};

import type { MidiTrackView } from './midiModel';
export type MidiTracksProps = {
    tracks: MidiTrackView[];
    muted: Set<number>;
    onToggleMute(index: number): void;
    onToggleSolo(index: number): void;
};
export declare function MidiTracks({ tracks, muted, onToggleMute, onToggleSolo }: MidiTracksProps): import("react").JSX.Element | null;

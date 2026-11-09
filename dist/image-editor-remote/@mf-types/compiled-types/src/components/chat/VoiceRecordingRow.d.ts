import type { CSSProperties } from 'react';
export declare function fmtRecTime(s: number): string;
export declare function Waveform(): import("react").JSX.Element;
export declare function VoiceRecordingRow({ seconds, onStop, isMobile, style }: {
    seconds: number;
    onStop: () => void;
    isMobile?: boolean;
    style?: CSSProperties;
}): import("react").JSX.Element;

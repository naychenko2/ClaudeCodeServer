import { type RefObject } from 'react';
type InputElement = HTMLInputElement | HTMLTextAreaElement;
type InputGetter = () => InputElement | null;
interface Props {
    inputRef?: RefObject<InputElement | null>;
    inputGetter?: InputGetter;
    variant?: 'circle' | 'suffix';
    style?: React.CSSProperties;
    isMobile?: boolean;
    onListeningChange?: (listening: boolean) => void;
    recordingRow?: boolean;
    rowStyle?: React.CSSProperties;
}
export declare function VoiceMicButton({ inputRef, inputGetter, variant, style, isMobile, onListeningChange, recordingRow, rowStyle }: Props): import("react").JSX.Element | null;
export {};

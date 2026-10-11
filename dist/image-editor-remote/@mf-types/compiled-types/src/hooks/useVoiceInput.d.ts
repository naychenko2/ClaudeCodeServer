export interface VoiceInputOptions {
    onResult: (chunk: string) => void;
    onKeyboardFallback: () => void;
    onEnd?: () => void;
    onError?: (code: string) => void;
    onHeard?: () => void;
    onCycleStart?: () => void;
    quiet?: () => boolean;
}
export interface VoiceInput {
    hasSpeech: boolean;
    isListening: boolean;
    recSeconds: number;
    startMic: () => void;
    stopMic: (confirm: boolean) => void;
}
export declare function useVoiceInput({ onResult, onKeyboardFallback, onEnd, onError, onHeard, onCycleStart, quiet }: VoiceInputOptions): VoiceInput;

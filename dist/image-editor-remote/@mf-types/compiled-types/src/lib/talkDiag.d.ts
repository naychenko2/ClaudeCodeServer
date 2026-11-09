export interface DiagEntry {
    t: number;
    msg: string;
}
export declare function talkDiag(msg: string, ...args: unknown[]): void;
export type TalkMark = 'speech-end' | 'send' | 'turn-start' | 'first-audio' | 'barge-in';
export declare function talkMark(mark: TalkMark): void;
export declare function talkDiagDump(): string;
export declare function talkDiagResetCycles(): void;
export declare function talkDiagSave(): void;
export declare function talkDiagSaved(): string | null;
export declare function talkDiagUpload(): Promise<boolean>;

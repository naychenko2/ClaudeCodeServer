export declare const HandsChatState: {
    readonly Active: "active";
    readonly Allowed: "allowed";
    readonly Unavailable: "unavailable";
    readonly Stopped: "stopped";
};
export declare const HandsEndReason: {
    readonly StoppedFromTray: "tray-stop";
    readonly HandsDisabled: "disabled";
    readonly AgentStopping: "agent-stopping";
    readonly Busy: "busy";
};
export interface HandsSectionInput {
    handsEnabled: boolean;
    refusal: string | null;
    deviceOffline: boolean;
}
export type HandsSectionTone = 'neutral' | 'ok' | 'warning';
export interface HandsSectionView {
    summary: string;
    tone: HandsSectionTone;
    canToggle: boolean;
    refusal: string | null;
    offlineNote: boolean;
}
export declare function handsSectionView(i: HandsSectionInput): HandsSectionView;
export interface HandsStatusSnapshot {
    state: string;
    deviceName?: string | null;
    reason?: string | null;
}
export declare function handsStatusFeedLine(status: HandsStatusSnapshot): string | null;

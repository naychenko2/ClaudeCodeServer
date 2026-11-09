export interface EtaState {
    run?: number | null;
    runs?: number | null;
    etaSeconds?: number | null;
    queued?: boolean;
}
export declare function etaFraction(s: EtaState, startedAt: number, now: number): number;
export declare function etaTicker(emit: (fraction: number) => void): {
    update(next: EtaState, restart?: boolean): void;
    stop(): void;
};

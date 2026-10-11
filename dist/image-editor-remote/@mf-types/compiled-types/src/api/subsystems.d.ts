export interface Subsystem {
    key: string;
    title: string;
    description: string;
    enabled: boolean;
    active: boolean;
    restartRequired: boolean;
}
export declare const subsystemsApi: {
    readonly get: () => Promise<Subsystem[]>;
};

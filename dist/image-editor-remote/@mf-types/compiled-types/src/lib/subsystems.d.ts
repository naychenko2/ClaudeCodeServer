export declare const SUBSYSTEMS: {
    readonly notes: "notes";
    readonly spend: "spend";
    readonly architecture: "architecture";
};
export type SubsystemKey = (typeof SUBSYSTEMS)[keyof typeof SUBSYSTEMS];
export type SubsystemsFromServer = string[];
export declare function setAllSubsystems(subsystems: SubsystemsFromServer): void;
export declare function isSubsystemEnabled(key: string): boolean;
export declare function getAllSubsystems(): Record<string, boolean>;
export declare function __resetSubsystems(): void;
export declare function subscribeSubsystems(fn: () => void): () => void;
export declare function useSubsystem(key: SubsystemKey): boolean;

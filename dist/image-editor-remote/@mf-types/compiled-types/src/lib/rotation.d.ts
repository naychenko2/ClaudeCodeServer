export interface RotationInfo {
    inRotation?: boolean;
    utilization?: number;
    threshold?: number;
    weeklyUtilization?: number;
    weeklyThreshold?: number;
    exhausted?: boolean;
    isTarget?: boolean;
    targetName?: string;
    freeAvailable?: boolean;
    supportsOpus?: boolean;
    supports1M?: boolean;
    tierBelowTarget?: boolean;
}
export interface RotationBadgeState {
    tone: 'ok' | 'warn';
    label: string;
    reason: string;
}
export declare function rotationBadgeState(info: RotationInfo): RotationBadgeState;

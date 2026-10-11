import type { Session } from '../types';
export declare const EXPIRY_PRESETS: {
    minutes: number;
    label: string;
}[];
export declare const DEFAULT_EXPIRY = 1440;
export declare function expiryOptionLabel(minutes?: number | null): string;
type ExpiryFields = Pick<Session, 'updatedAt' | 'expiresAfterMinutes' | 'expiryAnchor'>;
export declare function expiresAt(session: ExpiryFields): Date | null;
export declare function formatTimeLeft(session: ExpiryFields): string | null;
export declare function formatExpiryDate(at: Date): string;
export {};

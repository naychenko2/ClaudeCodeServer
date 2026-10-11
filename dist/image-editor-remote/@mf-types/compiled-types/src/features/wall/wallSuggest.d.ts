import type { Session } from '../../types';
export interface WallCandidate {
    session: Session;
    live: boolean;
}
interface PickOptions {
    taken: Set<string>;
    limit: number;
    now?: number;
}
export declare function chatsForWall(candidates: Session[], { taken, limit, now }: PickOptions): WallCandidate[];
export declare function loadChatsForWall(): Promise<WallCandidate[]>;
export declare function addChatsToWall(): Promise<number>;
export {};

import type { SessionArtifacts } from '../../hooks/useSessionArtifacts';
export type PanelKey = 'agents' | 'context';
export interface PanelBadgeOpts {
    personaId?: string | null;
}
export interface PanelBadge {
    visible: boolean;
    badge: string | null;
}
export declare function panelBadge(key: PanelKey, a: SessionArtifacts, opts: PanelBadgeOpts): PanelBadge;

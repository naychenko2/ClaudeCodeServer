import type { ReactNode } from 'react';
import type { Session } from '../../types';
import type { PanelKey, RailBadgeInfo } from './panelCatalog';
export interface SessionPanels {
    content: Partial<Record<PanelKey, ReactNode>>;
    visible: (k: PanelKey, isOpen: boolean) => boolean;
    railBadge: (k: PanelKey) => RailBadgeInfo | null;
    headerBadge: (k: PanelKey) => string | null;
}
export declare function useSessionPanels(session: Session | null, projectId?: string, rootPath?: string): SessionPanels;

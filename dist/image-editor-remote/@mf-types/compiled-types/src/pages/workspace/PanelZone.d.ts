import { type ReactNode } from 'react';
import { type PanelKey, type RailBadgeInfo, type Zone } from './panelCatalog';
import { type PanelZonesStore } from './panelStackState';
import type { SessionPanels } from './useSessionPanels';
interface Props {
    side: Zone;
    panels: Partial<Record<PanelKey, ReactNode>>;
    railBadges?: Partial<Record<PanelKey, RailBadgeInfo>>;
    panelStack?: PanelZonesStore;
    allowedKeys?: readonly PanelKey[];
    hideWhenEmpty?: boolean;
    compact?: boolean;
    sessionPanels?: SessionPanels;
    railFooter?: ReactNode;
    floating?: boolean;
    centerFileOpen?: boolean;
    onUserClose?: (k: PanelKey) => void;
}
export declare function PanelZone({ side, panels, railBadges, panelStack, allowedKeys: allowedKeysProp, hideWhenEmpty, compact, sessionPanels, railFooter, floating, centerFileOpen, onUserClose, }: Props): import("react").JSX.Element | null;
export {};

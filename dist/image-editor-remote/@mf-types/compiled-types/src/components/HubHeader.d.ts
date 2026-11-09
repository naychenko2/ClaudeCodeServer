import type { AuthState, Project } from '../types';
import { type HubTabValue } from './HubTabs';
interface Props {
    value: HubTabValue;
    onTab: (t: HubTabValue) => void;
    auth: AuthState;
    onLogout: () => void;
    historyActive?: boolean;
    onOpenExternal?: () => void;
    project?: Project;
    onOpenProjectSettings?: () => void;
}
export declare const PRODUCT_HISTORY_EVENT = "open-product-history";
export declare const productHistorySeenKey: (userId?: string | null) => string;
export declare function HubHeader({ value, onTab, auth, onLogout, historyActive, onOpenExternal, project, onOpenProjectSettings }: Props): import("react").JSX.Element;
export {};

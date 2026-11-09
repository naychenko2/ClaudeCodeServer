import type { ReactNode } from 'react';
import { type HubTab, type HubTabValue } from './hubTabsModel';
export * from './hubTabsModel';
export declare const TAB_LABELS: Record<HubTab, string>;
export declare const TAB_ICONS: Record<HubTab, ReactNode>;
export declare function tabLabel(v: HubTabValue): string;
export declare function tabIcon(v: HubTabValue): ReactNode | undefined;
export declare function HubTabs({ value, onChange, mobile, tablet, tabs }: {
    value: HubTabValue;
    onChange: (t: HubTabValue) => void;
    mobile?: boolean;
    tablet?: boolean;
    tabs?: HubTabValue[];
}): import("react").JSX.Element;

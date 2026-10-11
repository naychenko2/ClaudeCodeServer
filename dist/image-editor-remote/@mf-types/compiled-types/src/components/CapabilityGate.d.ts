import type { ReactNode } from 'react';
import type { Project, ProjectFeatureKey } from '../types';
interface UnavailableProps {
    feature: string;
    title: string;
    reason: string | null | undefined;
    action?: ReactNode;
}
export declare function CapabilityUnavailable({ feature, title, reason, action }: UnavailableProps): import("react").JSX.Element;
interface Props {
    project: Project | null | undefined;
    feature: ProjectFeatureKey;
    title: string;
    children: ReactNode;
    hideWhenUnavailable?: boolean;
    fallback?: ReactNode;
}
export declare function CapabilityGate({ project, feature, title, children, hideWhenUnavailable, fallback }: Props): import("react").JSX.Element | null;
export {};

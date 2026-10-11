import { type ReactNode } from 'react';
import type { Project } from '../types';
interface Props {
    project: Project;
    children: ReactNode;
    relayTitle?: string;
}
export declare function DeviceAgentGate({ project, children, relayTitle }: Props): import("react").JSX.Element;
export {};

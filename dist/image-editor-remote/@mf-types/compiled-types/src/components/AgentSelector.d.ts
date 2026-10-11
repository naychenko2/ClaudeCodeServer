import type { AgentInfo } from '../types';
export declare const AGENT_COLORS: Record<string, string>;
export declare function agentDotColor(color?: string): string;
interface Props {
    agents: AgentInfo[];
    selectedAgent: AgentInfo | null;
    onSelect: (agent: AgentInfo | null) => void;
    isMobile?: boolean;
}
export declare function AgentSelector({ agents, selectedAgent, onSelect, isMobile }: Props): import("react").JSX.Element | null;
export {};

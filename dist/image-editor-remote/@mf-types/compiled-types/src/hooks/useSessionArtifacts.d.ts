import type { ChatItem } from '../types';
export interface ArtifactFile {
    path: string;
    added: number;
    removed: number;
    hasDelta: boolean;
    changed: boolean;
    external: boolean;
}
export interface ArtifactLink {
    url: string;
    domain: string;
}
export type PlanStatus = 'approved' | 'rejected' | 'pending';
export interface PlanArtifact {
    plan: string;
    status: PlanStatus;
}
export interface TodoItem {
    content: string;
    status: string;
    activeForm?: string;
}
export type AgentStatus = 'running' | 'done' | 'error';
export interface AgentToolCall {
    id: string;
    name: string;
    arg?: string;
    running: boolean;
    isError?: boolean;
}
export interface AgentArtifact {
    id: string;
    kind: 'subagent' | 'workflow';
    type?: string;
    label: string;
    status: AgentStatus;
    background: boolean;
    toolCount: number;
    lastTool?: string;
    prompt?: string;
    resultText?: string;
    calls?: AgentToolCall[];
    tools?: {
        name: string;
        count: number;
    }[];
    files?: string[];
}
export interface WorkflowGroup {
    id: string;
    name: string;
    agents: AgentArtifact[];
    doneCount: number;
    settled: boolean;
}
export interface CommentArtifact {
    title: string;
    doc: string;
    mentioned?: boolean;
}
export interface SessionArtifacts {
    files: ArtifactFile[];
    plans: PlanArtifact[];
    todos: TodoItem[];
    links: ArtifactLink[];
    agents: AgentArtifact[];
    workflows: WorkflowGroup[];
    notes: string[];
    comments: CommentArtifact[];
    executingTask: string | null;
}
export interface PlanHint {
    text: string;
    done: number;
    total: number;
}
export declare function planHint(todos: TodoItem[]): PlanHint | undefined;
export interface TodoBatch {
    lastIndex: number;
    todos: TodoItem[];
}
export declare function computeTodoBatches(items: ChatItem[]): TodoBatch[];
export declare function computeTodos(items: ChatItem[]): TodoItem[];
export declare function computeAgents(items: ChatItem[]): {
    agents: AgentArtifact[];
    workflows: WorkflowGroup[];
};
export declare function computeArtifacts(items: ChatItem[], rootPath: string, executingTask?: string | null): SessionArtifacts;
export declare function useSessionArtifacts(sessionId: string | null, projectId?: string, rootPath?: string, executingTaskTitle?: string | null): SessionArtifacts;

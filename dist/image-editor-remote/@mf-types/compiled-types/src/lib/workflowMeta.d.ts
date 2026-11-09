export interface WorkflowPhase {
    title: string;
    detail?: string;
}
export interface WorkflowMeta {
    name?: string;
    description?: string;
    phases?: WorkflowPhase[];
}
export declare function parseWorkflowMeta(input: unknown): WorkflowMeta | null;
export declare function workflowName(input: Record<string, unknown>): string;

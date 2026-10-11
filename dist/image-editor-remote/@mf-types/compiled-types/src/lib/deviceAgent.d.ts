import type { Project } from '../types';
import { type DevicePresence, type ProjectFilesRoute } from './projectCapabilities';
import { type ProjectRoute } from './deviceAgentRoutes';
export declare const DEVICE_AGENT_DEFAULT_PORT = 47318;
export declare const DEVICE_AGENT_UNSUPPORTED_TEXT = "\u0423 \u043B\u043E\u043A\u0430\u043B\u044C\u043D\u043E\u0433\u043E \u043F\u0440\u043E\u0435\u043A\u0442\u0430 \u044D\u0442\u043E \u0434\u0435\u0439\u0441\u0442\u0432\u0438\u0435 \u043F\u043E\u043A\u0430 \u043D\u0435\u0434\u043E\u0441\u0442\u0443\u043F\u043D\u043E: \u0430\u0433\u0435\u043D\u0442 \u0443\u0441\u0442\u0440\u043E\u0439\u0441\u0442\u0432\u0430 \u0435\u0433\u043E \u043D\u0435 \u0443\u043C\u0435\u0435\u0442";
export declare const RELAY_READ_ONLY_TEXT = "\u0421 \u0434\u0440\u0443\u0433\u043E\u0433\u043E \u0443\u0441\u0442\u0440\u043E\u0439\u0441\u0442\u0432\u0430 \u043B\u043E\u043A\u0430\u043B\u044C\u043D\u044B\u0439 \u043F\u0440\u043E\u0435\u043A\u0442 \u043E\u0442\u043A\u0440\u044B\u0442 \u0442\u043E\u043B\u044C\u043A\u043E \u0434\u043B\u044F \u043F\u0440\u043E\u0441\u043C\u043E\u0442\u0440\u0430: \u0438\u0437\u043C\u0435\u043D\u0438\u0442\u044C \u0435\u0433\u043E \u043C\u043E\u0436\u043D\u043E \u043D\u0430 \u043A\u043E\u043C\u043F\u044C\u044E\u0442\u0435\u0440\u0435 \u043F\u0440\u043E\u0435\u043A\u0442\u0430";
export declare function noteProject(project: Project | null | undefined): void;
export declare function noteProjects(projects: Project[]): void;
export declare function devicePresenceOf(projectId: string): DevicePresence;
export declare function projectRouteOf(projectId: string): ProjectFilesRoute;
export type DeviceAgentStatus = {
    kind: 'checking';
} | {
    kind: 'ready';
} | {
    kind: 'unreachable';
} | {
    kind: 'refused';
    reason: string;
} | {
    kind: 'rejected';
    reason: string;
} | {
    kind: 'relay';
} | {
    kind: 'relay-unavailable';
    reason: string;
};
type RelayStatus = Extract<DeviceAgentStatus, {
    kind: 'relay' | 'relay-unavailable';
}>;
export declare function getDeviceAgentStatus(projectId: string): DeviceAgentStatus;
export declare function relayStatusOf(projectId: string): RelayStatus;
export declare class DeviceAgentError extends Error {
    readonly kind: 'unreachable' | 'refused' | 'rejected' | 'unsupported' | 'timeout';
    readonly status?: number;
    constructor(kind: DeviceAgentError['kind'], message: string, status?: number);
}
interface AgentTicket {
    ticket: string;
    expiresAt: number;
    port: number;
    header: string;
}
export declare function getAgentTicket(projectId: string, force?: boolean): Promise<AgentTicket>;
export declare function agentRequest<T>(projectId: string, pathAndQuery: string, init?: RequestInit, timeoutMs?: number): Promise<T>;
export declare function projectRequest<T>(url: string, options?: RequestInit & {
    timeoutMs?: number;
    live?: boolean;
}): Promise<T>;
export declare function assertServerRoute(projectId: string, method: string, path: string): void;
export declare const RELAY_UNAVAILABLE_CODE = "relay_unavailable";
export declare function probeRelay(projectId: string): Promise<void>;
export declare function agentStreamUrl(projectId: string, path: string, force?: boolean): Promise<string>;
export declare function agentHubTicket(projectId: string): Promise<string>;
export declare function agentHubUrl(projectId: string): Promise<string>;
export declare function agentPreviewUrl(projectId: string, force?: boolean): Promise<string>;
export declare function uploadAgentAttachment(projectId: string, file: File): Promise<{
    path: string;
}>;
export declare function probeDeviceAgent(projectId: string): Promise<void>;
export declare function useDeviceAgent(project: Project | null | undefined, enabled?: boolean): DeviceAgentStatus;
export declare function useProjectFilesRoute(project: Project | null | undefined): ProjectFilesRoute;
export interface ProjectRoutes {
    route: ProjectFilesRoute;
    can: (route: ProjectRoute) => boolean;
}
export declare function useProjectRoutes(project: Project | null | undefined): ProjectRoutes;
export declare function resetDeviceAgentForTests(): void;
export {};

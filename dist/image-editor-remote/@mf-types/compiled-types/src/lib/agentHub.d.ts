import * as signalR from '@microsoft/signalr';
import type { ServerMessage } from '../types';
type AgentHubHandler = (msg: ServerMessage) => void;
export declare function agentHub(projectId: string): Promise<signalR.HubConnection>;
export declare function agentHubIfConnected(projectId: string): signalR.HubConnection | null;
export declare function onAgentHubMessage(handler: AgentHubHandler): () => void;
export declare function resetAgentHubs(): Promise<void>;
export declare function resetAgentHubForTests(): void;
export {};

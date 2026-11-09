import type { McpBuiltinServer, McpCatalogRevision, McpProbeResult, McpServer, McpServerUpsert, Persona, Project } from '../../types';
export interface McpStatusTone {
    label: string;
    dot: string;
    text: string;
}
export declare function mcpStatusTone(status?: string | null): McpStatusTone;
export declare function plural(n: number, one: string, few: string, many: string): string;
export declare function mcpStatusLine(status: McpServer['status'], probe?: McpProbeResult): string;
export declare function mcpAuthLine(server: McpServer, probe?: McpProbeResult): string;
export declare function accessSummary(personasOff: number, projectsOff: number): string;
export declare function accessSummaryOn(personasOn: number, projectsOn: number, outsideOn: boolean): string;
export declare function personaOffFor(persona: Persona, serverKey: string): boolean;
export declare function personaBindingFor(persona: Persona, serverKey: string): import("../../types").PersonaBinding | undefined;
export declare function personaGrantedFor(persona: Persona, serverKey: string): boolean;
export interface McpData {
    servers: McpServer[] | null;
    builtin: McpBuiltinServer[];
    projects: Project[];
    personas: Persona[];
    error: string | null;
    setError: (e: string | null) => void;
    checking: Record<string, boolean>;
    probes: Record<string, McpProbeResult>;
    reload: () => void;
    setEnabled: (server: McpServer, enabled: boolean) => void;
    probe: (server: McpServer) => Promise<{
        kind: 'done';
    } | {
        kind: 'needsConfirmation';
        command: string;
    }>;
    confirmProbe: (server: McpServer) => Promise<void>;
    remove: (server: McpServer) => Promise<void>;
    save: (id: string | null, data: McpServerUpsert) => Promise<McpServer>;
    importJson: (fragment: unknown) => Promise<{
        created: McpServer[];
        skipped: {
            key: string;
            reason: string;
        }[];
    }>;
    setProjectOn: (project: Project, serverKey: string, on: boolean) => Promise<{
        kind: 'ok';
    } | {
        kind: 'needsConfirmation';
        servers: {
            key: string;
            command: string;
        }[];
    }>;
    confirmSetProjectOn: (project: Project, serverKey: string) => Promise<void>;
    projectsOnCount: (serverKey: string) => number;
    personasOnCount: (serverKey: string) => number;
    grantPersona: (persona: Persona, serverKey: string) => Promise<void>;
    revokePersona: (persona: Persona, serverKey: string) => Promise<void>;
    oauthPending: Record<string, boolean>;
    oauthNotice: Record<string, string>;
    startOAuth: (server: McpServer, clientId?: string) => Promise<void>;
    completeOAuth: (server: McpServer, code: string) => Promise<boolean>;
    dismissOAuthNotice: (server: McpServer) => void;
    revisions: Record<string, McpCatalogRevision>;
    revisionsCheckFailed: boolean;
}
export declare function useMcpData(): McpData;

import type { Project } from '../types';
export interface NavSnapshot {
    screen: 'home' | 'projects' | 'project' | 'chats' | 'wall' | 'calendar' | 'notes' | 'personas' | 'specialties' | 'knowledge' | 'notifications' | 'spend' | 'telemetry' | 'module';
    moduleId?: string;
    project?: Project;
    chatId?: string;
    view?: 'sidebar' | 'chat';
    file?: string | null;
    revealInTree?: boolean;
    task?: string | null;
    board?: boolean;
    note?: string | null;
    persona?: string | null;
    personaView?: 'automation';
    knowledge?: string | null;
    specialty?: string | null;
    specialtyEdit?: boolean;
}
export interface HashTarget {
    screen: 'home' | 'projects' | 'chats' | 'wall' | 'calendar' | 'project' | 'notes' | 'personas' | 'specialties' | 'knowledge' | 'notifications' | 'spend' | 'telemetry' | 'module';
    projectId?: string;
    moduleId?: string;
    taskId?: string;
    file?: string;
    board?: boolean;
    noteId?: string;
    personaId?: string;
    personaView?: 'automation';
    knowledgeId?: string;
    specialtyKey?: string;
    specialtyEdit?: boolean;
    chatId?: string;
    incidentFingerprint?: string;
    telemetryIncidents?: boolean;
    history?: boolean;
    intro?: boolean;
    modelsSpend?: boolean;
}
export declare function parseHash(hash?: string): HashTarget | null;
export declare const NAV_CHANGE_EVENT = "cc-nav-change";
export declare function navPush(s: NavSnapshot): void;
export declare function navReplace(s: NavSnapshot): void;
export declare function getNav(): NavSnapshot | null;

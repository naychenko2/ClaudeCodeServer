import type { Me, Project, Session } from '../types';
interface MeState {
    loaded: boolean;
    userId: string | null;
    defaultPersonaId: string | null;
    role: string;
    needsOnboarding: boolean;
    onboardingSessionId: string | null;
    executionEnvironment: 'local' | 'container' | null;
}
export declare function setMeFromServer(me: Me): void;
export declare function clearMe(): void;
export declare function refreshMe(): Promise<void>;
export declare function getMeSnapshot(): MeState;
export declare function useMe(): MeState;
export declare function getContextDefaultPersonaId(project?: Pick<Project, 'defaultPersonaId'> | null): string | null;
export declare class OnboardingRequiredError extends Error {
    readonly scope: 'user' | 'project';
    constructor(scope: 'user' | 'project');
}
export declare function createChatWithContextPersona(project?: Pick<Project, 'id' | 'defaultPersonaId'> | null, opts?: {
    mode?: string;
    name?: string;
}): Promise<Session>;
export {};

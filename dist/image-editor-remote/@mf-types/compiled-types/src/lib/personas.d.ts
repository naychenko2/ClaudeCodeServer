import type { Persona } from '../types';
export declare function reloadPersonas(): Promise<void>;
export declare function ensurePersonasLoaded(): Promise<void>;
export declare function usePersonas(): Persona[];
export declare function usePersonasVersion(): number;
export declare function getPersonasSnapshot(): Persona[];
export declare function getPersonaById(id: string): Persona | undefined;
export declare function bumpPersonas(): void;
export declare function personaLabel(p: {
    role?: string;
    name: string;
}): string;
export declare function personaInitials(name: string): string;
export declare function personaTitleLines(p: {
    role?: string;
    name: string;
}): {
    primary: string;
    secondary?: string;
};

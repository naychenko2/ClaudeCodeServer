import type { Persona } from '../types';
export declare function useContextPersona(opts?: {
    personaId?: string | null;
    projectDefaultId?: string | null;
}): Persona | null;

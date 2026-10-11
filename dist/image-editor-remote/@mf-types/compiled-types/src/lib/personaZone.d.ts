import type { Persona } from '../types';
type ZonePersona = Pick<Persona, 'scope' | 'projectId' | 'sphereId'>;
export type SphereOf = (projectId: string) => string | undefined;
export declare const isProjectPersona: (p: Pick<Persona, "scope"> | null | undefined) => boolean;
export declare const isSpherePersona: (p: Pick<Persona, "scope"> | null | undefined) => boolean;
export declare const isGlobalPersona: (p: Pick<Persona, "scope"> | null | undefined) => boolean;
export declare const isZonedPersona: (p: Pick<Persona, "scope"> | null | undefined) => boolean;
export declare function ownProjectId(p: ZonePersona | null | undefined): string | undefined;
export declare function isProjectTeam(p: ZonePersona, projectId: string | undefined): boolean;
export declare function isSphereTeam(p: ZonePersona, sphereId: string | undefined): boolean;
export declare function visibleIn(p: ZonePersona, projectId: string | undefined, sphereOf?: SphereOf): boolean;
export interface ZoneNames {
    projectName?: (projectId: string | undefined) => string | undefined;
    sphereName?: (sphereId: string | undefined) => string | undefined;
}
export declare function zoneLabel(p: ZonePersona, names?: ZoneNames, short?: boolean): string;
export declare function zoneToValue(p: ZonePersona): string;
export declare function zoneFromValue(value: string): {
    scope: Persona['scope'];
    projectId?: string;
    sphereId?: string;
};
export declare const OPEN_COMPANION_PICKER_EVENT = "cc-open-companion-picker";
export declare function isZoneRefusal(item: {
    text: string;
    action?: string | null;
}): boolean;
export declare function zoneHint(zone: Pick<Persona, 'scope'>, projectCount: number): string;
export {};

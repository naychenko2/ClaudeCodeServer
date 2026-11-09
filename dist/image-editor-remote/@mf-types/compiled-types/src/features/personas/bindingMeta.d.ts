import type { ReactNode } from 'react';
import type { BindingTarget, PersonaBinding, PersonaBindingMode, PersonaBindingType } from '../../types';
export declare const BINDING_ICONS: Record<PersonaBindingType, (size: number) => ReactNode>;
export declare const BINDING_TYPE_META: Record<PersonaBindingType, {
    name: string;
    hint: string;
    bg: string;
    fg: string;
}>;
export declare const BINDING_TYPE_ORDER: PersonaBindingType[];
export declare const MODE_LABEL: Record<PersonaBindingMode, string>;
export declare const MODE_HINT: Record<PersonaBindingMode, string>;
export declare const MODE_BADGE: Record<PersonaBindingMode, {
    bg: string;
    fg: string;
}>;
export declare function BindingTypeIcon({ type, size, dim }: {
    type: PersonaBindingType;
    size?: number;
    dim?: boolean;
}): import("react").JSX.Element;
export declare function BindingModeBadge({ mode }: {
    mode: PersonaBindingMode;
}): import("react").JSX.Element;
export declare function bindingPlural(n: number): string;
export declare function bindingsCounter(bindings: PersonaBinding[]): string;
export declare function invalidateBindingTargets(type: string): void;
export declare function fetchBindingTargets(type: string, source?: string, personaId?: string): Promise<BindingTarget[]>;
export declare function catalogTypeFor(t: PersonaBindingType): string;
export declare function bindingLabel(b: PersonaBinding, targets: Map<string, BindingTarget>): string;

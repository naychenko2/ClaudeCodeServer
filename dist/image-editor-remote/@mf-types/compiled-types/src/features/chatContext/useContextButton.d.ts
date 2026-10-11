import type { SessionContextType } from '../../types';
export interface ContextButtonState {
    available: boolean;
    inContext: boolean;
    title: string;
    toggle: () => void;
}
export declare function useContextButton(type: SessionContextType, id: string | null, name?: string): ContextButtonState;

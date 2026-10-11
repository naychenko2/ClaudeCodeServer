import type { ComponentType } from 'react';
import { type ModuleInfo } from './api';
export interface AIHomeModuleContext {
    user: {
        id: string;
        name: string;
    };
    apiBase: string;
    getToken: () => string | null;
    theme: {
        mode: 'light' | 'dark';
    };
    navigate: (hash: string) => void;
    onTitleChange?: (t: string) => void;
    schemaVersion: string;
    kitEntry: string;
    kitVersion: string;
}
export type ModuleTabComponent = ComponentType<AIHomeModuleContext>;
export declare function loadModules(): Promise<void>;
export declare function getModules(): ModuleInfo[];
export declare function getModule(id: string): ModuleInfo | undefined;
export declare function subscribeModules(fn: () => void): () => void;
export declare function useModules(): ModuleInfo[];
export declare function loadModuleTab(m: ModuleInfo): Promise<ModuleTabComponent>;

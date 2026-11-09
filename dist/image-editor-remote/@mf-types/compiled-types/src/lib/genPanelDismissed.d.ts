import { genPanelKeys } from './genPanelKeys';
export declare const MAX_KEYS = 500;
export { genPanelKeys };
export declare function isGenPanelKey(key: string): boolean;
export declare function isGenPanelDismissed(sessionId: string, panelKey: string): boolean;
export declare function markGenPanelDismissed(sessionId: string | null | undefined, panelKey: string): void;
export declare function autoRevealGenerationPanel(panelKey: string, sessionId: string | null | undefined, tab?: string): boolean;

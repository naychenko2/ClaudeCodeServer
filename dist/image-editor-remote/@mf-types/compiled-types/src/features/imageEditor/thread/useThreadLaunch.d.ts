import { type ImageEditCatalog, type ImageEditOp } from '../api';
import { type LaunchAction, type QuickAction, type QuickRoute } from '../editorInputs';
import { type PanelChoice } from '../context/ops';
import { type ImageMode } from './modeState';
import { type ImageThread, type ImageThreadSettings, type ImageThreadVersion } from './threadsApi';
export declare function imageSrc(projectId: string, t: ImageThread, stepId: string | null): string | null;
export declare function versionSrc(projectId: string, t: ImageThread, v: ImageThreadVersion): string | null;
export declare function activeSrc(projectId: string, t: ImageThread): string | null;
export { threadHasImage } from './model';
export declare function resolveModel(catalog: ImageEditCatalog | null, settings: ImageThreadSettings): {
    pv: import("../api").ImageEditProvider | null;
    m: import("../api").ImageEditModel | null;
};
export declare function launchMode(sessionId: string | null, thread: ImageThread | null): ImageMode;
export declare function loadImage(src: string): Promise<HTMLImageElement>;
export declare function quickAvailabilityFor(catalog: ImageEditCatalog | null, settings: ImageThreadSettings, action: QuickAction): import("../editorInputs").QuickAvailability;
export declare function panelRoute(choice: PanelChoice, hasImage: boolean, hasMask: boolean): {
    op: ImageEditOp;
    quick: QuickAction | null;
    one: boolean;
    reason: string;
};
export interface ContextLaunch {
    op: ImageEditOp;
    count: number;
    aspect: string | null;
    contextRevision: number;
    onJob?: (jobId: string) => void;
}
export declare function launchThread(projectId: string, sessionId: string, thread: ImageThread, requested: LaunchAction, opts?: {
    provider?: string;
    ctx?: ContextLaunch;
}): Promise<boolean>;
export declare function useThreadLaunch(projectId: string, sessionId: string | null, thread: ImageThread | null): {
    catalog: ImageEditCatalog | null;
    settings: ImageThreadSettings;
    prefs: import("./prefs").ProjectPrefs;
    imageMode: ImageMode;
    provider: import("../api").ImageEditProvider | null;
    model: import("../api").ImageEditModel | null;
    blocked: string;
    quote: import("../api").ImageEditQuote | null;
    quoteLoading: boolean;
    priceLabel: string;
    price: string | null;
    marks: import("../marks").Mark[];
    drawn: import("../marks").Mark[];
    whole: boolean;
    hasImage: boolean;
    hasMask: boolean;
    setSettings: (patch: Partial<ImageThreadSettings>) => void;
    launch: (action: LaunchAction) => Promise<boolean>;
    route: QuickRoute | null;
    op: ImageEditOp;
    quickAction: QuickAction | null;
    choice: PanelChoice;
    count: number;
    maxCount: number;
    reason: string;
    priceLines: [string, string];
    queue: string | undefined;
    runLabel: string;
};

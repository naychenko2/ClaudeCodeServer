import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import { type ImageEditCatalog, type ImageEditEstimate } from '../api';
export interface LaunchChange {
    field: string;
    from?: unknown;
    to?: unknown;
}
export declare function modelLabel(catalog: ImageEditCatalog | null, provider: string | null | undefined, model: string | null | undefined): string;
export declare function describeChanges(changes: LaunchChange[], catalog: ImageEditCatalog | null): string[];
export declare function changedLine(parts: string[], persona: boolean): string | null;
interface LaunchResult {
    jobId: string;
    provider: string | null;
    model: string | null;
    estimate: ImageEditEstimate | null;
    expectedSeconds: number | null;
    changes: LaunchChange[];
}
export declare function parseLaunchResult(text: string | undefined): LaunchResult | null;
export declare function ImageLaunchCard({ ctx }: {
    ctx: ChatItemToolCtx;
}): import("react").JSX.Element | null;
export declare function ImagePromptCard({ ctx }: {
    ctx: ChatItemToolCtx;
}): import("react").JSX.Element | null;
export declare function ImageLaunchRow({ ctx }: {
    ctx: ChatItemToolCtx;
}): import("react").JSX.Element;
export declare function ImageFileMovedRow({ ctx }: {
    ctx: ChatItemToolCtx;
}): import("react").JSX.Element;
export {};

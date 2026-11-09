import type { WorkspacePanelDefCtx } from '../../../lib/subsystems/registryCore';
export declare const CHARACTERS_PANEL = "characters";
export declare function CharactersContextPanel({ ctx, layout }: {
    ctx: WorkspacePanelDefCtx;
    layout?: 'column' | 'sheet';
}): import("react").JSX.Element;

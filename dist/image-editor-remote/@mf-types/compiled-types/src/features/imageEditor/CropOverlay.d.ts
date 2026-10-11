import type { ImageFractionRect } from './api';
import { type CropRatio, type Dims } from './transforms';
export declare function CropOverlay({ rect, ratio, size, onChange }: {
    rect: ImageFractionRect;
    ratio: CropRatio;
    size: Dims;
    onChange: (r: ImageFractionRect) => void;
}): import("react").JSX.Element;

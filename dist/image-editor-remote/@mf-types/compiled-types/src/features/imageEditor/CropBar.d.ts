import { type CropRatio } from './transforms';
export declare function CropBar({ ratio, mobile, onRatio, onCancel, onApply }: {
    ratio: CropRatio;
    mobile: boolean;
    onRatio: (r: CropRatio) => void;
    onCancel: () => void;
    onApply: () => void;
}): import("react").JSX.Element;

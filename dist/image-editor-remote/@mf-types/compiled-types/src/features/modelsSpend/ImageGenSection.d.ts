import { type CSSProperties } from 'react';
import type { ImageGenerationSettings, ImagePlaceSettings } from '../../types';
export declare const AUTO = "auto";
export declare const MODEL_PICKER_PROVIDER = "fal";
export declare function setupHint(key: string): string;
export declare function placeOf(s: ImageGenerationSettings, key: string): ImagePlaceSettings | null;
export declare function modelOf(place: ImagePlaceSettings, providerKey: string): string | null;
export declare function canPickModel(place: ImagePlaceSettings): boolean;
export declare function withPlaceProvider(s: ImageGenerationSettings, placeKey: string, provider: string): ImageGenerationSettings;
export declare function withPlaceModel(s: ImageGenerationSettings, placeKey: string, providerKey: string, model: string): ImageGenerationSettings;
interface ImageGenSectionProps {
    isAdmin: boolean;
    titleStyle: CSSProperties;
}
export declare function ImageGenSection({ isAdmin, titleStyle }: ImageGenSectionProps): import("react").JSX.Element;
export {};

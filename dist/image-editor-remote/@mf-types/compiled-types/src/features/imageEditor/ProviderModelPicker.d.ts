import { type ReactNode } from 'react';
import type { ImageEditCatalog, ImageEditModel, ImageEditProvider } from './api';
import { type ProviderChoice } from './format';
interface Props {
    catalog: ImageEditCatalog;
    provider: ProviderChoice;
    model: string;
    onProvider: (p: ProviderChoice) => void;
    onModel: (m: string) => void;
    hasImage: boolean;
    hasMask: boolean;
    priceLabel: string;
    mobile: boolean;
}
export declare function ProviderItems({ catalog, value, onPick }: {
    catalog: ImageEditCatalog;
    value: ProviderChoice;
    onPick: (p: ProviderChoice) => void;
}): import("react").JSX.Element;
export declare function currentModel(provider: ImageEditProvider | null, model: string): ImageEditModel | null;
export declare function ProviderModelPicker(p: Props): import("react").JSX.Element | null;
export declare function SectionLabel({ children }: {
    children: ReactNode;
}): import("react").JSX.Element;
export declare function PriceLine({ text }: {
    text: string;
}): import("react").JSX.Element;
export {};

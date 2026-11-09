import type { ComponentType, SVGProps } from 'react';
export declare const LUCIDE_ICON_NAMES: readonly string[];
export declare const LUCIDE_ICON_NAME_SET: ReadonlySet<string>;
export declare function isLucideIconName(name: string): boolean;
type GlyphSvgProps = SVGProps<SVGSVGElement> & {
    size?: number;
};
export declare function preloadGlyph(name: string): Promise<void>;
export declare const Blank: () => null;
export type GlyphIconProps = Omit<GlyphSvgProps, 'name' | 'fallback'> & {
    name: string;
    /** Компонент (НЕ JSX-элемент). Обязателен. */
    fallback: ComponentType;
};
export declare function GlyphIcon(props: GlyphIconProps): import("react").JSX.Element;
export {};

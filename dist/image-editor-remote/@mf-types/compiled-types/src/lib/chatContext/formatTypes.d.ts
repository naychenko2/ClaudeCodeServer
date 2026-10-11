export type FormatTierKey = 'standard' | 'high' | 'max';
export type FormatMode = 'model' | 'aspect' | 'exact' | 'source';
export type FormatActor = 'human' | 'agent';
export interface FormatSize {
    w: number;
    h: number;
}
export interface FormatTierSpec {
    tier: FormatTierKey;
    label: string;
    sizes: Record<string, FormatSize>;
    wireValue?: string | null;
    priceFactor?: number;
    postStep?: string | null;
}
export interface ExactSizeSpec {
    step: number;
    minSide: number;
    maxSide?: number | null;
    maxMegapixels?: number | null;
}
export interface MediaFormatCaps {
    modelLabel: string;
    aspects: string[];
    tiers: FormatTierSpec[];
    exact?: ExactSizeSpec | null;
    wireParam?: string | null;
    nativeNote?: string | null;
}
export interface MediaFormatWish {
    mode: FormatMode;
    aspect?: string | null;
    tier?: FormatTierKey | null;
    width?: number | null;
    height?: number | null;
    by: FormatActor;
    at: string;
}
export interface ResolvedFormat {
    mode: FormatMode;
    aspect?: string | null;
    tier?: FormatTierKey | null;
    width?: number | null;
    height?: number | null;
    wireValue?: string | null;
    postStep?: string | null;
    substituted: boolean;
    rounded: boolean;
    note?: string | null;
}
export interface FormatPrice {
    amount: number;
    unit: string;
    approx?: boolean;
}
export interface FormatTierOption {
    tier: FormatTierKey;
    label: string;
    size?: FormatSize | null;
    wireValue?: string | null;
    postStep?: string | null;
    price?: FormatPrice | null;
    disabledReason?: string | null;
    caption?: string | null;
    extra?: string | null;
    note?: string | null;
}
export interface MediaFormatState {
    caps: MediaFormatCaps | null;
    wish: MediaFormatWish | null;
    resolved: ResolvedFormat;
    tiers: FormatTierOption[];
    exact?: ExactSizeSpec | null;
    exactUnavailableReason?: string | null;
}
export type CropAnchor = 'left' | 'center' | 'right';
export type FramesAnchor = CropAnchor | 'top' | 'bottom';
export interface FormatFramesModel {
    text: string;
    previews: {
        slot: string;
        aspect: string;
        thumb: string | null;
    }[];
    target: string;
    anchor: FramesAnchor;
    axis: 'sides' | 'height' | 'mixed';
    onAnchor: (anchor: FramesAnchor) => void;
}
export interface FormatLockAsk {
    text: string;
    keepLabel: string;
    forceLabel: string;
    onKeep: () => void;
    onForce: () => void;
}
export interface FormatEditModel {
    source: {
        width: number;
        height: number;
        aspect: string;
        thumb: string | null;
    } | null;
    aspect: string | null;
    how: 'outpaint' | 'crop' | null;
    anchor: CropAnchor;
    outpaint: {
        detail: string;
        size: string | null;
        ready: boolean;
    };
    crop: {
        detail: string;
        size: string | null;
        ready: boolean;
    };
    onSource: () => void;
    onAspect: (aspect: string) => void;
    onHow: (how: 'outpaint' | 'crop') => void;
    onAnchor: (anchor: CropAnchor) => void;
    onCancel: () => void;
    cropping: boolean;
}
export type FormatKind = 'generate' | 'edit' | 'readonly';
export interface FormatModel {
    kind: FormatKind;
    title: string;
    state: MediaFormatState | null;
    by: FormatActor | null;
    chip: {
        main: string;
        mainBare?: string;
        sub?: string;
        subShort?: string;
        subBare?: string;
        prefix?: string;
        tone: 'normal' | 'warn' | 'dim';
    };
    footer: (projectName: string | null) => string;
    readonlyNote?: string;
    edit?: FormatEditModel;
    locked?: {
        filmPath: string;
        aspect: string;
        note?: string;
    };
    lockAsk?: FormatLockAsk;
    mismatch?: string;
    mismatchAction?: {
        label: string;
        onClick: () => void;
    };
    frames?: FormatFramesModel;
    onChange: (wish: Omit<MediaFormatWish, 'by' | 'at'>) => void;
    hint?: string;
    needsChoice?: boolean;
}

import type { FormatPrice, FormatTierKey, FormatTierOption, MediaFormatState } from './formatTypes';
export declare const TIER_NAME: Readonly<Record<FormatTierKey, string>>;
export declare const TIER_ORDER: readonly FormatTierKey[];
export declare const FORMAT_TEXT: {
    readonly chat: "Формат";
    readonly asModel: "Как у модели";
    readonly asSource: "Как у исходника";
    readonly loading: "Формат…";
    readonly noCaps: (provider: string, model: string) => string;
    readonly noCapsMore: "Пропорции и качество появятся, когда выберете модель с известными размерами.";
    readonly changeModel: "Сменить модель в «Чем»";
    readonly exactLink: "Точно… ширина × высота ›";
    readonly exactBack: "Вернуться к пропорциям";
    readonly exactHide: "Скрыть";
    readonly exactOut: "Уйдёт в модель:";
    readonly editAspectsHint: "Выберите пропорции — предложим дорисовать края или обрезать.";
    readonly askOutpaint: "Дорисовать края";
    readonly askCrop: "Обрезать";
    readonly askNote: "Сама модель пропорции не тянет и не режет — только то, что выберете здесь.";
    readonly anchors: {
        readonly left: "Слева";
        readonly center: "По центру";
        readonly right: "Справа";
    };
    readonly anchorsHeight: {
        readonly top: "Сверху";
        readonly center: "По центру";
        readonly bottom: "Снизу";
    };
    readonly anchorLabel: "Рамка обрезки:";
    readonly anchorMixed: "Рамка — по центру: кадры режутся в разные стороны.";
    readonly pendingChoice: "Выберите в «Формате», как получить новые пропорции: дорисовать края или обрезать.";
    readonly outpaintNeedsFormat: "«Дорисовать»: выберите в «Формате» пропорции, до которых дорисовать.";
    readonly upscaleChip: "Размер задаёт операция";
    readonly upscaleNote: "У «Увеличить» размер задаёт сама операция. Пропорции не меняются.";
};
export declare function priceLabel(p: FormatPrice | null | undefined): string;
export declare const sizeLabel: (s: {
    w: number;
    h: number;
} | null | undefined) => string;
export declare function tierCaption(o: FormatTierOption): {
    size: string;
    price: string;
};
export declare function generateChip(state: MediaFormatState | null): {
    main: "Формат…";
    tone: "dim";
    prefix?: undefined;
    sub?: undefined;
} | {
    prefix: "Формат";
    main: "Как у модели";
    tone: "dim";
    sub?: undefined;
} | {
    main: string;
    sub: string;
    tone: "normal" | "warn";
    prefix?: undefined;
} | {
    tone: "normal";
    sub?: string | undefined;
    subShort?: string | undefined;
    subBare?: string | undefined;
    main: string;
    prefix?: undefined;
};

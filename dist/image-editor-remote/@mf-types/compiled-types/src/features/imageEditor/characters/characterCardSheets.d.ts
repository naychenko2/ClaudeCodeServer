export type CardSheet = 'name' | 'photos' | 'versions' | 'tune';
export type CardMenuKey = 'name' | 'photos' | 'versions' | 'tune' | 'remove';
export declare function sheetForMenu(key: CardMenuKey): CardSheet | null;
export declare function menuKeys(versionsCount: number): CardMenuKey[];
export type CardChipKey = 'photos' | 'versions' | 'tune';
export declare function sheetForChip(key: CardChipKey): CardSheet;
export declare function chipKeys(versionsCount: number): CardChipKey[];
export declare function photosNeedDot(train: number | null, allRejected: boolean): boolean;
export interface CollageLayout {
    shown: number;
    extra: number;
}
export declare function collageLayout(total: number): CollageLayout;
export declare function orderedPhotos<T extends {
    primary?: boolean | null;
}>(photos: readonly T[]): T[];
export declare function deleteConfirmTexts(name: string, path: string): {
    readonly title: `\u0423\u0434\u0430\u043B\u0438\u0442\u044C \u00AB${string}\u00BB?`;
    readonly subtitle: `\u041F\u0430\u043F\u043A\u0430 ${string}/ \u0441 \u0444\u043E\u0442\u043E \u0443\u0434\u0430\u043B\u0438\u0442\u0441\u044F \u0438\u0437 \u043F\u0440\u043E\u0435\u043A\u0442\u0430`;
    readonly confirmLabel: "Удалить";
};

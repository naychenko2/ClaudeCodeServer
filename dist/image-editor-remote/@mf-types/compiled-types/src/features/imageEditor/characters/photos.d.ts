export declare const MIN_PHOTOS = 3;
export declare const MAX_PHOTOS = 10;
export declare const MAX_PHOTO_MB = 8;
export declare function shrinkPhoto(file: File): Promise<Blob>;
export declare const photosCountText: (n: number) => string;
export declare function pickFaceFiles<T extends {
    name: string;
    type: string;
}>(files: T[], existing: number): {
    picked: T[];
    error: string | null;
};
export declare const photoSizeError: (name: string, size: number) => string | null;
export declare function uploadWithConsent<T>(consent: boolean, files: T[], onAdd: (files: T[]) => Promise<void>, resetConsent: () => void): Promise<boolean>;
export declare function addGate(consent: boolean, count: number): 'consent' | 'empty' | 'ok';
export declare function missingText(name: string, photoCount: number, consent: boolean): string;

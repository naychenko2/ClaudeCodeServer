export type FileStatus = 'M' | 'A' | 'D' | 'R' | '?';
/** Значок состояния файла. Неизвестный код не рисуется вовсе. */
export declare function FileStatusBadge({ status }: {
    status: string;
}): import("react").JSX.Element | null;

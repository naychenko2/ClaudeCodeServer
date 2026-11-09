import { type ReactNode } from 'react';
export declare function isFileLikeAbsPath(p: string): boolean;
export declare function ChatImage({ src, alt }: {
    src?: string;
    alt?: string;
}): import("react").JSX.Element;
export declare function FileLink({ path, onOpen, mono, children }: {
    path: string;
    onOpen: (path: string) => void;
    mono?: boolean;
    children: ReactNode;
}): import("react").JSX.Element;
export declare function stripServiceMarkers(text: string): string;
export declare const MarkdownContent: import("react").MemoExoticComponent<({ text }: {
    text: string;
}) => import("react").JSX.Element>;
export declare function proxyUrl(url: string): string;

import type { ReactNode } from 'react';
export declare function LoadingScreen({ hint, overlay, children }: {
    hint?: string;
    overlay?: boolean;
    children?: ReactNode;
}): import("react").JSX.Element;
export declare function LoadingOverlay({ hint, children }: {
    hint?: string;
    children?: ReactNode;
}): import("react").ReactPortal;

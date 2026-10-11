import { type RefObject } from 'react';
export interface Heading {
    level: number;
    text: string;
    el: HTMLElement;
    occurrence: number;
}
export interface DocToc {
    path: string;
    headings: Heading[];
    jump: (h: Heading) => void;
    sectionOf: (h: Heading) => string | null;
    subscribeActive: (cb: (index: number) => void) => () => void;
}
export declare function headingText(el: HTMLElement): string;
export declare function collectHeadings(nodes: HTMLElement[]): Heading[];
export declare function useHeadings(contentRef: RefObject<HTMLElement | null>, dep: unknown, containerToken?: unknown): Heading[];
export declare function scrollToHeading(root: HTMLElement | null, h: Heading): boolean;
export declare function flashHeading(root: HTMLElement | null, el: HTMLElement, occurrence?: number): void;
export declare function resolveHeadingEl(root: HTMLElement | null, h: Heading): HTMLElement | null;
export declare function useHeadingSpy(contentRef: RefObject<HTMLElement | null>, headings: Heading[]): {
    subscribe: DocToc['subscribeActive'];
    pin: (h: Heading) => void;
};

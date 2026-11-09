import { type RefObject } from 'react';
export declare function useInView(opts?: {
    rootMargin?: string;
    threshold?: number;
    root?: RefObject<Element | null>;
}): [ref: (node: HTMLElement | null) => void, inView: boolean];

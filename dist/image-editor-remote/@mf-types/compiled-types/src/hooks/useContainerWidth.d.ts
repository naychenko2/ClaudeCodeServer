import { type RefCallback } from 'react';
export declare function observeWidth(el: HTMLElement, onWidth: (width: number) => void): () => void;
export declare function useContainerWidth<T extends HTMLElement>(): [RefCallback<T>, number | null];
export declare function useNarrowContainer<T extends HTMLElement>(threshold: number): [RefCallback<T>, boolean];

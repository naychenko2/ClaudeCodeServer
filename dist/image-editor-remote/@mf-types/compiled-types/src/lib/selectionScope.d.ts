export declare function registerCopyDoc(el: HTMLElement, getText: () => string | null): () => void;
export declare function copyMarkdown(text: string): Promise<boolean>;
export declare function copyRenderedHtml(el: HTMLElement, plainFallback?: string): Promise<boolean>;
export declare function installSelectionScopes(): () => void;

import type { ImageEditEstimate, ImageEditorApi, ImageEditQuote, ImageEditQuoteRequest } from './api';
export declare function staleEstimate(last: {
    req: ImageEditQuoteRequest;
    quote: ImageEditQuote;
} | null, req: ImageEditQuoteRequest | null): ImageEditEstimate | null;
export declare function sharedQuote(api: ImageEditorApi, projectId: string, req: ImageEditQuoteRequest): Promise<ImageEditQuote>;
export declare function __resetSharedQuotes(): void;
export declare function useQuote(api: ImageEditorApi, projectId: string, req: ImageEditQuoteRequest | null): {
    quote: ImageEditQuote | null;
    error: string | null;
    loading: boolean;
    stale: ImageEditEstimate | null;
};

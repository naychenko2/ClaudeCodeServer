export interface OpenChatOptions {
    missingTitle?: string;
    missingBody?: string;
}
export declare function openChatById(sessionId: string, opts?: OpenChatOptions): Promise<boolean>;

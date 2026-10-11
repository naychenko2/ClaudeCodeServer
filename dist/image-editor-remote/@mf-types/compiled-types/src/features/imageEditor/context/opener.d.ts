export declare function imageRefOfPath(projectId: string, sessionId: string, path: string): Promise<{
    kind: string;
    ref: {
        threadId: string;
        versionId: string;
    } | {
        threadId: string;
        versionId?: undefined;
    };
} | null>;

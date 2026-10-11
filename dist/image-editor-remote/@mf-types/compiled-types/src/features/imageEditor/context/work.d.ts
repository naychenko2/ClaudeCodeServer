export declare const imageRefOf: (threadId: string, versionId: string | null) => {
    kind: string;
    ref: {
        threadId: string;
        versionId: string;
    } | {
        threadId: string;
        versionId?: undefined;
    };
};
export declare function workWithInContext(sessionId: string, threadId: string, versionId: string | null, reveal?: boolean): Promise<boolean>;
export declare function pickInContext(sessionId: string, threadId: string, versionId: string | null, working: boolean): Promise<void>;

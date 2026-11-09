export declare const CHAT_CONTEXT_EVENT = "chat_context_changed";
export declare const ChatContextErrors: {
    readonly contextChanged: "context_changed";
    readonly kindUnknown: "kind_unknown";
    readonly refInvalid: "ref_invalid";
    readonly roleNotAccepted: "role_not_accepted";
    readonly refsLimit: "refs_limit";
    readonly projectLocalUnsupported: "project_local_unsupported";
};
export type ChatContextErrorCode = typeof ChatContextErrors[keyof typeof ChatContextErrors];
export declare const MAX_REFS = 16;
export declare const chatContextRoutes: {
    base: (sessionId: string) => string;
    primary: (sessionId: string) => string;
    refs: (sessionId: string) => string;
    ref: (sessionId: string, itemId: string) => string;
    savedFiles: (sessionId: string) => string;
};
export declare class ReportedError extends Error {
    readonly reported = true;
}
export declare const isReported: (e: unknown) => boolean;

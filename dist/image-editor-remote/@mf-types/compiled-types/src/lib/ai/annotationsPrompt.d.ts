export declare const ANNOTATIONS_TOOL_KEY = "notes-annotations";
export declare function docAnnotationsPrompt(scope: string, path: string): string;
export declare function allAnnotationsPrompt(project?: {
    id: string;
    name: string;
}): string;

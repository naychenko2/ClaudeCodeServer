export type DocLinkKind = 'doc' | 'repo' | 'external';
export interface ResolvedDocLink {
    kind: DocLinkKind;
    target: string;
    anchor: string | null;
}
export declare function stripMarkdown(text: string): string;
export declare function slugify(headingText: string): string;
export declare function splitAnchor(target: string): [string, string | null];
export declare function isExternal(target: string): boolean;
export declare function resolveRelative(fromDoc: string, target: string): string | null;
export declare function resolveDocImage(fromDoc: string, src: string): string | null;
export declare function resolveDocLink(fromDoc: string, href: string, knownDocs: ReadonlySet<string>): ResolvedDocLink | null;
export declare function sliceSection(markdown: string, slug: string): string | null;

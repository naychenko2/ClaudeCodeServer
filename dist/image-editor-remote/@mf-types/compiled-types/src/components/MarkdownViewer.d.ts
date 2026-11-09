export interface ResolvedNote {
    title: string;
    content: string;
}
interface Props {
    content: string;
    blockPos?: boolean;
    onWikilink?: (target: string) => void;
    existingTitles?: Set<string>;
    resolveNote?: (name: string, anchor?: string) => Promise<ResolvedNote | null>;
    embedSource?: string;
    embedDepth?: number;
    hideLeadingH1?: boolean;
    onDocLink?: (href: string) => void;
    resolveImageSrc?: (src: string) => string | undefined;
}
export declare function stripFrontmatter(md: string): {
    body: string;
    offset: number;
};
export declare function clearResolveCache(): void;
export declare function MarkdownViewer({ content, blockPos, onWikilink, existingTitles, resolveNote, embedSource, embedDepth, hideLeadingH1, onDocLink, resolveImageSrc }: Props): import("react").JSX.Element;
export {};

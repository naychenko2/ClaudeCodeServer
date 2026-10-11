import type { Heading } from '../../hooks/useHeadings';
export declare function resolveHeading(anchor: string, anchorIndex: number, headings: Heading[]): Heading | null;
export declare function headingHasDuplicates(text: string, headings: Heading[]): boolean;
export declare function sliceSection(planText: string, heading: Heading): string;
export declare function stripInlineMarkdown(s: string): string;

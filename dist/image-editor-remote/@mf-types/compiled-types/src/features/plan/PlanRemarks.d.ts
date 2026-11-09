import { type RefObject } from 'react';
import { type Heading } from '../../hooks/useHeadings';
interface Props {
    contentRef: RefObject<HTMLElement | null>;
    planText: string;
    containerToken?: unknown;
    status: 'pending' | 'resolved';
    onSubmit: (feedback: string) => void;
    onCountChange?: (count: number) => void;
    externalRequest?: {
        heading: string;
        occurrence: number;
        token: number;
    } | null;
}
export declare const PLAN_GENERAL_HEADING = "\u041E\u0431\u0449\u0435\u0435 \u043F\u043E \u043F\u043B\u0430\u043D\u0443";
export declare function headingForRange(root: HTMLElement, range: Range): HTMLElement | null;
export declare function anchorForHeadingEl(el: HTMLElement | null, headings: Heading[]): {
    heading: string;
    occurrence: number;
} | null;
export declare function PlanRemarks({ contentRef, planText, containerToken, status, onSubmit, onCountChange, externalRequest }: Props): import("react").JSX.Element | null;
export {};

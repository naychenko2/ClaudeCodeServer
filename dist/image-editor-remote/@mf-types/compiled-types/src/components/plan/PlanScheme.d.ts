import { type RefObject } from 'react';
import type { PlanMap } from '../../types';
interface Props {
    map: PlanMap;
    planText: string;
    contentRef: RefObject<HTMLElement | null>;
    onRequestRemark?: (anchor: {
        heading: string;
        occurrence: number;
    }) => void;
}
export declare function PlanScheme({ map, planText, contentRef, onRequestRemark }: Props): import("react").JSX.Element;
export {};

import type { Operation } from '../../lib/toolLabels';
export declare function WaitingIndicator({ planning, hint, awaitingResponse, waitingReason, waitingTicks, activeToolLabel, activeToolStartedAt, activeToolTimer, activeToolAppearedAt, activeToolDetail, activeToolOnScreen, activeToolOperation }?: {
    planning?: 'planning' | 'replanning';
    hint?: string;
    awaitingResponse?: boolean;
    waitingReason?: string | null;
    waitingTicks?: number;
    activeToolLabel?: string | null;
    activeToolStartedAt?: number | null;
    activeToolTimer?: boolean;
    activeToolAppearedAt?: number | null;
    activeToolDetail?: string | null;
    activeToolOnScreen?: boolean;
    activeToolOperation?: Operation | null;
}): import("react").JSX.Element;

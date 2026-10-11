export interface PlanRemark {
    anchorHeading: string;
    anchorIndex?: number;
    quote?: string;
    text: string;
}
export declare const PLAN_FEEDBACK_FOOTER = "\u0420\u0430\u0437\u0434\u0435\u043B\u044B \u0431\u0435\u0437 \u0437\u0430\u043C\u0435\u0447\u0430\u043D\u0438\u0439 \u0441\u043E\u0433\u043B\u0430\u0441\u043E\u0432\u0430\u043D\u044B";
export declare function buildPlanFeedback(remarks: readonly PlanRemark[], headingOrder: readonly string[]): string;

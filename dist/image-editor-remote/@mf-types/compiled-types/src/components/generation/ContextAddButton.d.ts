import { type ContextCandidate } from '../../lib/chatContext/fill';
export declare const NO_PRIMARY_REASON = "\u0421\u043D\u0430\u0447\u0430\u043B\u0430 \u0432\u044B\u0431\u0435\u0440\u0438\u0442\u0435, \u0441 \u0447\u0435\u043C \u0440\u0430\u0431\u043E\u0442\u0430\u0442\u044C: \u00AB\u0420\u0430\u0431\u043E\u0442\u0430\u0442\u044C \u0441 \u044D\u0442\u043E\u0439\u00BB \u043D\u0430 \u043A\u0430\u0440\u0442\u043E\u0447\u043A\u0435 \u043B\u0435\u043D\u0442\u044B \u0438\u043B\u0438 \u0432 \u00AB\u0424\u0430\u0439\u043B\u0430\u0445\u00BB";
export declare const NO_ROLE_REASON = "\u0412\u044B\u0431\u0440\u0430\u043D\u043D\u044B\u0439 \u043E\u0431\u044A\u0435\u043A\u0442 \u0442\u0430\u043A\u043E\u0439 \u0440\u0435\u0444\u0435\u0440\u0435\u043D\u0441 \u043D\u0435 \u0431\u0435\u0440\u0451\u0442";
export declare const LG_ADD_LABEL = "\u0412 \u043A\u043E\u043D\u0442\u0435\u043A\u0441\u0442 \u0447\u0430\u0442\u0430";
export declare const LG_IN_LABEL = "\u0412 \u043A\u043E\u043D\u0442\u0435\u043A\u0441\u0442\u0435";
export declare function ContextAddButton({ sessionId, projectId, candidate, size, toggle, isMobile }: {
    sessionId: string;
    projectId: string | null;
    candidate: ContextCandidate;
    size?: 'xs' | 'sm' | 'lg';
    toggle?: boolean;
    isMobile?: boolean;
}): import("react").JSX.Element;

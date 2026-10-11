import type { Session } from '../types';
export type ChatStatusChip = 'active' | 'waiting' | 'done' | 'error';
export type ChatOnlyFilter = 'pinned' | 'temp' | 'group';
export type ChatGroupBy = 'days' | 'tags' | 'none';
export declare const ALL_SORT_ORDERS: readonly ["newest", "oldest", "created-newest", "created-oldest"];
export type ChatSortOrder = (typeof ALL_SORT_ORDERS)[number];
export declare function sortKeyOf(sortOrder: ChatSortOrder): {
    field: 'updatedAt' | 'createdAt';
    dir: 1 | -1;
};
export interface ChatFilters {
    origins: Session['origin'][];
    statuses: ChatStatusChip[];
    personaId: string | null;
    only: ChatOnlyFilter[];
    search: string;
    tags?: string[];
    groupBy: ChatGroupBy;
    sortOrder: ChatSortOrder;
    hierarchy: boolean;
    archivedOnly: boolean;
}
export declare const ALL_ORIGINS: Session['origin'][];
export declare const ALL_STATUS_CHIPS: ChatStatusChip[];
export declare const DEFAULT_STATUSES: ChatStatusChip[];
export declare function loadChatFilters(scopeKey: string): ChatFilters;
export declare function persistChatFilters(scopeKey: string, v: ChatFilters): void;
export declare function chatFilterScope(s: {
    projectId?: string | null;
}): string;
export declare function leaveChatArchiveView(scopeKey: string): void;
export declare function useChatFilters(scopeKey: string): {
    filters: ChatFilters;
    patch: (p: Partial<ChatFilters>) => void;
};
export declare function useSanitizePersonaFilter(filters: ChatFilters, patch: (p: Partial<ChatFilters>) => void, personaIdsInList: string[], listLoaded: boolean): void;
export declare function chatStatusOf(s: Session): ChatStatusChip;
export declare function isArchivedChat(s: Session): boolean;
export declare function matchChatFilter(filters: ChatFilters): (s: Session) => boolean;
export declare function isDefaultFilters(f: ChatFilters): boolean;
export declare function defaultChatFilters(): ChatFilters;
export declare function defaultChatFiltersKeepingView(f: ChatFilters): ChatFilters;
export declare const ARCHIVE_EMPTY_TITLE = "\u0410\u0440\u0445\u0438\u0432\u043D\u044B\u0445 \u0447\u0430\u0442\u043E\u0432 \u043D\u0435\u0442";
export declare const ARCHIVE_EMPTY_SUBTITLE = "\u0423\u0431\u0435\u0440\u0438\u0442\u0435 \u0447\u0430\u0442 \u0432 \u0430\u0440\u0445\u0438\u0432 \u0438\u0437 \u0435\u0433\u043E \u043C\u0435\u043D\u044E \u2014 \u043E\u043D \u0441\u043F\u0440\u044F\u0447\u0435\u0442\u0441\u044F \u0438\u0437 \u0441\u043F\u0438\u0441\u043A\u0430, \u043D\u043E \u043E\u0441\u0442\u0430\u043D\u0435\u0442\u0441\u044F \u0446\u0435\u043B\u044B\u043C: \u0438\u0441\u0442\u043E\u0440\u0438\u044F \u0438 \u043A\u043E\u043D\u0442\u0435\u043A\u0441\u0442 \u043D\u0430 \u043C\u0435\u0441\u0442\u0435";
export declare function chatCountWord(n: number): string;
export declare function buildHiddenReason(total: number, search: string): string;

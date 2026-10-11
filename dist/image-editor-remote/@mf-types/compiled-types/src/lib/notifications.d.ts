import type { NotificationItem, NotificationListResponse, CreateNotificationRequest } from '../types';
export declare function loadNotifications(limit?: number, offset?: number, kind?: string, unreadOnly?: boolean): Promise<NotificationListResponse>;
export declare function loadUnreadCount(): Promise<number>;
export declare function getNotification(id: string): Promise<NotificationItem>;
export declare function createNotification(req: CreateNotificationRequest): Promise<NotificationItem>;
export declare function loadUnreadSticky(): Promise<NotificationItem[]>;
export declare function markRead(id: string): Promise<void>;
export declare function markAllRead(): Promise<number>;
export declare function markReadBatch(ids: string[]): Promise<number>;
export declare function deleteNotification(id: string): Promise<void>;
export declare function deleteBatch(ids: string[]): Promise<number>;
export declare function deleteReadAll(): Promise<number>;
export declare function getNotifications(): NotificationItem[];
export declare function getUnreadCount(): number;
export declare function isLoaded(): boolean;
/** Подтянуть счётчик непрочитанных для бейджа, если он ещё не известен или протух. */
export declare function ensureUnreadCountLoaded(): Promise<void>;
/** Загрузить список уведомлений, если он еще не загружен. */
export declare function ensureNotificationsLoaded(): Promise<void>;
export declare function ensureNotificationsSubscribed(): void;
export declare function subscribeToNotifications(fn: () => void): () => void;

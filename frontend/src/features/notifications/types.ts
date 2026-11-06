// Типы домена «Уведомления» (переехали из types/index.ts, S5-P3). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// ===== Уведомления (центр уведомлений) =====

// alert — тревоги телеметрии (SigNoz). Отдельный вид, а не 'info': иначе срочное
// тонет среди саммари и дайджестов, и отфильтровать его нечем.
export type NotificationKind = 'reminder' | 'claude' | 'info' | 'success' | 'meeting' | 'alert';

export interface NotificationItem {
  id: string;
  kind: NotificationKind;
  type: string;
  title: string;
  body: string;
  url?: string;
  projectId?: string;
  sessionId?: string;
  taskId?: string;
  source?: string;
  tag?: string;
  // Атрибуция персоны (аватар/лицо на уведомлении) + имя проекта
  personaId?: string;
  personaName?: string;
  personaRole?: string;
  personaColor?: string;
  personaHasAvatar?: boolean;
  projectName?: string;
  // Закреплённое: тост всплывает при каждом входе и не гаснет сам, пока не прочитано
  sticky?: boolean;
  isRead: boolean;
  createdAt: string;
  readAt?: string;
}

export interface NotificationListResponse {
  items: NotificationItem[];
  totalCount: number;
  unreadCount: number;
}

export interface CreateNotificationRequest {
  kind: NotificationKind;
  type: string;
  title: string;
  body: string;
  url?: string;
  projectId?: string;
  sessionId?: string;
  taskId?: string;
  source?: string;
  tag?: string;
}

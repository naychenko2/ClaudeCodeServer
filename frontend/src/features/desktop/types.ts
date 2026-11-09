// Типы домена «Устройства» (канал устройства ADR-016; переехали из types/index.ts, S5-P3). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// ---------- Устройства (канал устройства ADR-016) ----------

// Устройство владельца из GET /api/devices. Отпечаток наружу урезан до 12 символов —
// он служит человеку приметой «это та самая машина», а не проверкой.
// online/platform/agentVersion/harnessReady/harnessProblem/capabilities — поля для
// локальных проектов (ADR-016 §4): показываем «устройство офлайн» и решаем, годится ли
// оно для запуска хода (capabilities.exec)
export interface DesktopDevice {
  id: string;
  name: string;
  fingerprint: string;
  clientVersion?: string | null;
  createdAt: string;
  lastSeenAt?: string | null;
  revoked: boolean;
  revokedAt?: string | null;
  tokenVersion: number;
  // Текущий статус: true — клиент в сети и пингует шлюз. Отсутствует у старого бэка —
  // тогда считаем false (не показываем как доступное для привязки)
  online?: boolean;
  // Платформа клиента ("windows" | "macos" | "linux" | ...) — только для карточки
  platform?: string | null;
  // Версия агента устройства (semver) — для диагностики и подсказки «обновите»
  agentVersion?: string | null;
  // Самообновление агента (agent-distribution AD-3/AD-5); null — агент о нём не сообщал
  agentUpdate?: DeviceAgentUpdate | null;
  // Готовность харнеса локальных проектов: false — привязка возможна, но ходы не пойдут,
  // пока владелец не дособерёт (см. harnessProblem)
  harnessReady?: boolean;
  harnessProblem?: string | null;
  // Что устройство сейчас умеет (для пикера привязки в AddProjectDialog). exec — может
  // ли запускать ход локального проекта (Agent локальных проектов ADR-016)
  capabilities?: {
    exec?: boolean;
    files?: boolean;
  } | null;
}

// Состояние самообновления агента (DeviceAgentUpdateStates на бэке): reason — текст с устройства
export interface DeviceAgentUpdate {
  state: 'idle' | 'downloading' | 'waiting-idle' | 'failed';
  targetVersion?: string | null;
  reason?: string | null;
}

// Заявка на сопряжение: код из 8 символов живёт 5 минут и принадлежит ЭТОЙ веб-сессии.
// hostFingerprint — отпечаток машины самого сервера: клиент на ней сопрягаться отказывается
export interface DesktopPairingCode {
  code: string;
  expiresAt: string;
  attemptsLeft: number;
  hostFingerprint?: string;
}

// Первая отрисовка бейджа рук локального проекта (GET /api/sessions/{id}/hands-status),
// зеркало HandsStatusView. state=null — у чата рук нет вовсе, бейдж не рисуется
export interface LocalHandsChatStatus {
  state: string | null;
  reason: string | null;
  deviceName: string | null;
}

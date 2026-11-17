// Типы домена «Детский режим» (методы — ./api.ts).
// Источник правды имён полей: docs/adr/ADR-041-child-mode.md, раздел «Контракт /api/child/*»
// (бэк пишется параллельно по тому же разделу). Имена типов — по плану шага KM-F1,
// поля JSON — буквально как в ADR.

// Старшая причина состояния по порядку И3: rules_pending → budget_exhausted →
// out_of_window → time_up → warn → ok (ADR-041, «Инвариант И3»).
export type ChildLimitsStateKind = 'ok' | 'warn' | 'time_up' | 'out_of_window' | 'rules_pending' | 'budget_exhausted';

// Ответ GET /api/child/status и POST /api/child/heartbeat — один и тот же DTO.
export interface ChildStatus {
  state: ChildLimitsStateKind; // старшая причина по порядку И3
  space: string; // «lessons» — единственное пространство волны 1
  minutesLeft: number | null; // остаток дневного лимита пространства; null — лимита нет
  windowEndsAt: string | null; // конец текущего окна; null — окон нет или сейчас вне окна
  nextWindowAt: string | null; // начало ближайшего окна при state = out_of_window; иначе null
  warnMinutes: number; // порог предупреждения из профиля (по умолчанию 5)
  maxAttachmentBytes: number; // ChildMode:MaxAttachmentBytes, по умолчанию 10 МБ
  attachmentTypes: string[]; // MIME из allow-list (image/png, image/jpeg, image/webp, application/pdf, text/plain)
  rulesAgreedAt: string | null;
  dailyMinutes: number | null; // дневной лимит из профиля; null — лимита нет
  windows: { from: string; to: string }[]; // окна доступа «HH:mm»; [] — окон нет
  minutesToday: number | null; // минуты с уроками за сегодня; null — журнал не прочитать
}

// Окно времени пространства: from/to — «HH:mm», to < from — окно через полночь;
// weekdays — ISO 1..7, нет поля — каждый день. Время — в таймзоне ребёнка (ADR-041, «Общие правила»).
export interface TimeWindow {
  from: string;
  to: string;
  weekdays?: number[];
}

// Тело PUT /api/users/{id}/child-profile и поле profile ответа (ChildProfileDto в ADR).
export interface ChildProfile {
  enabled: boolean;
  guardianUserIds: string[];
  providerKey: string; // запись пула с ApiKey (И2: ход ребёнка никогда не по подписке)
  model: string;
  spaces: Record<string, { windows: TimeWindow[]; dailyMinutes?: number | null }>;
  dailyBudgetUsd?: number | null;
  monthlyBudgetUsd?: number | null;
  warnMinutes?: number; // по умолчанию 5
  resetRules?: boolean; // только во входе: сбросить RulesAgreedAt/RulesSummary
}

// Коды отказа загрузки вложения (413/415/422) — ADR-041, «Ручки» п. 8.
export type ChildAttachmentRejectCode = 'too_large' | 'unsupported_type' | 'pdf_no_text' | 'empty_file';

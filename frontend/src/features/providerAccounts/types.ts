// Типы домена «Аккаунты провайдеров» (методы — ./api.ts). Вынесены из lib/api.ts
// шагом S5-1 (refactor/opt-frontend-api); публичный адрес типов — через ре-экспорт
// lib/api.ts (`export type *`), импорты потребителей не меняются.

// Пользовательский вид: подключение + фактическое состояние из McpStatusStore.
// health='failed' означает, что у апстрима проблемы прямо сейчас (HTTP-ошибка,
// таймаут, пустой белый список) — карточка должна покраснеть, иначе громкий отказ
// теряется (задача 67b7c30a, раздел «громкий отказ не доезжает до человека»).
export type HiggsfieldStatus = {
  connected: boolean;
  health?: string | null;
  error?: string | null;
};
// Админский вид: с expiresAt, чтобы администратор видел, когда токен истекёт.
export type HiggsfieldAdminStatus = HiggsfieldStatus & { expiresAt?: string | null };

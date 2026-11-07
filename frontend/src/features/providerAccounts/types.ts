// Типы домена «Аккаунты провайдеров» (методы — ./api.ts): виды Higgsfield вынесены
// из lib/api.ts шагом S5-1, DTO fal/glif/yandex — из types/index.ts хвостом S5-P
// (refactor/opt-frontend-api). Публичный адрес типов — через ре-экспорт lib/api.ts и
// types/index.ts (`export type *`), импорты потребителей не меняются.

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

// Статистика аккаунта fal.ai (баланс + расход за период)
export interface FalModelSpend { endpointId: string; cost: number; }
export interface FalDaySpend { date: string; cost: number; }
export interface FalUsageSummary {
  days: number;
  total: number;
  byModel: FalModelSpend[];
  series: FalDaySpend[];
}
export interface FalAccountResponse {
  enabled: boolean;
  balance?: number | null;
  currency?: string | null;
  usage?: FalUsageSummary | null;
}

// Статистика аккаунта glif (план + баланс кредитов + расход за окна).
// Кредиты, не USD: currency="credits". Агрегаты расхода фиксированные (24ч/7д/30д).
export interface GlifSpend { last24h?: number; last7d?: number; last30d?: number; }
export interface GlifAccountResponse {
  enabled: boolean;
  plan?: string | null;
  planStatus?: string | null;
  balance?: number | null;
  currency?: string | null;
  spend?: GlifSpend | null;
}

// Деньги Yandex Cloud (GET /api/yandex/account): остаток на биллинг-аккаунте и НАШ расход
// на озвучку за период. Баланс приходит строкой — так его отдаёт Billing API, и валюта у него
// своя (обычно RUB). balanceHidden=true — смотрит не админ: кошелёк инстанса ему не показываем.
// spend считает продукт сам: разбивку по услугам Billing API не отдаёт в принципе.
export interface YandexAccountResponse {
  enabled: boolean;
  error?: string | null;
  account?: {
    id: string;
    name?: string | null;
    balance?: string | null;
    currency?: string | null;
    active: boolean;
  } | null;
  asOf?: string | null;
  balanceHidden: boolean;
  days: number;
  spend?: { total: number; requests: number } | null;
}

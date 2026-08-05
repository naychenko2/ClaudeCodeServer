// Логика карточки «Продолжить на …» (provider_limit): разложение опций на секции
// (аккаунты пула подписок первыми — продолжить на своей подписке дешевле и роднее,
// затем сторонние провайдеры) и подписи кнопок. Чистые функции — под vitest,
// рендер в chat/ChatItemView.tsx (ProviderLimitCard).
import type { ProviderFallbackOption } from '../types';

export interface FallbackSections {
  // Аккаунты того же пула подписок Claude (kind='subscription')
  subscriptions: ProviderFallbackOption[];
  // Сторонние провайдеры (kind='provider' или поле не задано — старый контракт)
  providers: ProviderFallbackOption[];
}

export function splitFallbackOptions(options: ProviderFallbackOption[]): FallbackSections {
  const subscriptions: ProviderFallbackOption[] = [];
  const providers: ProviderFallbackOption[] = [];
  for (const o of options) {
    (o.kind === 'subscription' ? subscriptions : providers).push(o);
  }
  return { subscriptions, providers };
}

// Подпись кнопки аккаунта пула: тариф и утилизация — «Max 5× · 41%».
// Поля опциональны: только тариф, только процент, либо пусто.
export function formatSubscriptionMeta(option: ProviderFallbackOption): string {
  const parts: string[] = [];
  if (option.tierLabel) parts.push(option.tierLabel);
  if (option.utilization != null) parts.push(`${Math.round(option.utilization * 100)}%`);
  return parts.join(' · ');
}

// Три канонические формулировки причины автофолбэка для подсказки «Ответила X — Y
// была недоступна» (ModelSwitchedPill). Значения — wire-имена backend TurnErrorClassifier
// (rate_limit/usage_limit/provider_error/unreachable); rate_limit и usage_limit сведены
// к одной формулировке «Исчерпан лимит» — обе означают исчерпанную квоту, различие
// не для пользователя.
const REASON_LABEL: Record<string, string> = {
  rate_limit: 'Исчерпан лимит',
  usage_limit: 'Исчерпан лимит',
  provider_error: 'Провайдер выключен',
  unreachable: 'Эндпоинт недоступен',
};

export function providerSwitchReasonLabel(reason: string | undefined, fallback: string | undefined): string | undefined {
  if (reason && REASON_LABEL[reason]) return REASON_LABEL[reason];
  return fallback;
}

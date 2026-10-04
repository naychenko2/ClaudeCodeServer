// Общие типы и подписи цены для списка «Исполнитель» (executorRows) и входов запуска: итог настроек нити,
// правка настроек, подпись поставщика и цены. Чистые функции.

import type { AudioModelInfo, AudioOpInputs, AudioProvider, AudioThreadSettings } from '../api';
import type { ResolvedLaunch } from './launch';

// Итог настроек: разрешённый запуск плюс выбор человека как есть
export interface PanelState extends ResolvedLaunch {
  // Выбор человека как есть (null — берётся по цепочке): по нему подсвечиваем «Авто»
  providerKey: string | null;
  modelId: string;
  fields: Record<string, unknown>;
  // Входы операции на сервере: у нити с настройками — её, иначе — префов режима
  inputs: AudioOpInputs | null;
}

export type SettingsPatch = Partial<Pick<AudioThreadSettings, 'mode' | 'operation' | 'provider' | 'model' | 'count'>> & {
  fields?: Record<string, unknown>;
  // undefined — как были (при смене операции — только нужные новой); null — снять
  inputs?: AudioOpInputs | null;
};

export function providerUnit(p: AudioProvider): string {
  switch (p.priceUnit) {
    case 'free': return 'бесплатно';
    case 'usd': return 'в долларах';
    case 'credits': return 'кредиты';
    case 'rub': return 'в рублях';
    default: return p.priceUnit;
  }
}

const PER_LABEL: Record<string, string> = { chars: 'за 1000 симв.', sec: 'за с', min: 'за мин', run: 'за запуск' };

// «$0.1 за 1000 симв.», «бесплатно»; null — цены каталог не знает
export function unitLabel(provider: AudioProvider | null, model: AudioModelInfo | null): string | null {
  if (provider?.priceUnit === 'free' || model?.caps.priceUnit === 'free') return 'бесплатно';
  const h = model?.priceHint;
  if (!h) return null;
  const amount = h.unit === 'chars' ? h.amount * 1000 : h.amount;
  return `${money(amount, provider?.priceUnit ?? 'usd')} ${PER_LABEL[h.unit] ?? ''}`.trim();
}

export function money(amount: number, currency: string): string {
  const n = String(Math.round(amount * 10_000) / 10_000);
  if (currency === 'usd') return `$${n}`;
  if (currency === 'rub') return `${n.replace('.', ',')} ₽`;
  if (currency === 'credits') return `${n.replace('.', ',')} кред.`;
  return `${n} ${currency}`;
}

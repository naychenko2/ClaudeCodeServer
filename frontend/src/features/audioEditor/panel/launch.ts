// Разрешение запуска звука: настройки — цепочкой «настройки нити → префы режима → умолчание каталога», как
// AudioPrefsResolver на бэкенде. Режим задаёт нить; без её настроек — ярлык «Голос» / «Музыка».

import type { AudioCatalog, AudioMode, AudioModelInfo, AudioOp, AudioPrefs, AudioProvider, AudioThread } from '../api';
import { defaultOp } from '../ops';

export interface ResolvedLaunch {
  mode: AudioMode;
  op: AudioOp;
  provider: AudioProvider | null;
  // null у «Авто» без модели, умеющей операцию
  model: AudioModelInfo | null;
  auto: boolean;
  count: number;
  voice: string | null;
  price: string | null;
  heavy: boolean;
}

const str = (v: unknown) => (typeof v === 'string' && v.trim() ? v.trim() : null);

const supports = (p: AudioProvider, op: AudioOp) => p.models.some(m => m.caps.ops.includes(op));

// Порядок «Авто» задаёт сервер (AudioCatalog.AutoCandidates): сами его не считаем, иначе сводка
// показала бы fal, а котировка взяла бы local
export function autoOrder(catalog: AudioCatalog | null): AudioProvider[] {
  const providers = catalog?.providers ?? [];
  const order = catalog?.autoProviders;
  if (!order) return providers;
  return order.map(key => providers.find(p => p.key === key)).filter((p): p is AudioProvider => !!p);
}

export function resolveLaunch(
  thread: AudioThread | null, prefs: AudioPrefs, catalog: AudioCatalog | null, fallbackMode: AudioMode,
): ResolvedLaunch {
  const own = thread?.settings ?? null;
  const mode = own?.mode ?? fallbackMode;
  const p = prefs[mode];
  const op = own?.operation ?? p?.operation ?? defaultOp(mode);
  const providers = catalog?.providers ?? [];
  const providerKey = own?.provider ?? p?.provider ?? null;
  // Явный выбор не подменяем: недоступный поставщик остаётся в сводке, как выбран
  const provider = providerKey
    ? providers.find(x => x.key === providerKey) ?? null
    : autoOrder(catalog).find(x => x.available && supports(x, op)) ?? null;
  const modelId = own?.model ?? p?.model ?? catalog?.autoModelId ?? 'auto';
  const auto = !modelId || modelId === (catalog?.autoModelId ?? 'auto');
  const model = provider
    ? (auto ? provider.models.find(m => m.caps.ops.includes(op)) : provider.models.find(m => m.id === modelId)) ?? null
    : null;
  const max = catalog?.maxCount ?? 4;
  const count = Math.min(Math.max(own?.count ?? p?.count ?? 1, 1), max);
  const fields = { ...(p?.fields ?? {}), ...(own?.fields ?? {}) };
  const voice = mode === 'voice' ? str(fields.voiceName) ?? str(fields.voice) ?? str(fields.speaker) : null;
  return {
    mode, op, provider, model, auto, count, voice,
    price: priceLabel(provider, model),
    heavy: !!model?.caps.heavyOps?.includes(op),
  };
}

// «бесплатно» у локальных; иначе ориентир каталога «≈ $0.1 за 1000 симв.»; точная сумма — в котировке
export function priceLabel(provider: AudioProvider | null, model: AudioModelInfo | null): string | null {
  const unit = model?.caps.priceUnit ?? provider?.priceUnit;
  if (unit === 'free') return 'бесплатно';
  const h = model?.priceHint;
  if (!h) return null;
  return `≈ ${money(h.amount, h.unit)}${h.per ? ` за ${h.per}` : ''}`;
}

function money(amount: number, unit: string): string {
  const n = String(Math.round(amount * 10_000) / 10_000);
  if (unit === 'usd') return `$${n}`;
  if (unit === 'rub') return `${n.replace('.', ',')} ₽`;
  if (unit === 'credits') return `${n.replace('.', ',')} кред.`;
  return `${n} ${unit}`;
}


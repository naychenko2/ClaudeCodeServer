// Сводка полосы «Звук» (макет audio-editor-v2-proposal.md, раздел «Полоса «Звук» над
// композером»): кнопка-сводка, свёрнутая строка и пункт меню переключателя говорят одно и
// то же. Чистые функции — под юнит-тестом.
//
// Настройки — цепочкой «настройки нити → префы режима → умолчание каталога», как
// AudioPrefsResolver на бэкенде. Режим задаёт нить; без её настроек — ярлык «Голос» / «Музыка».

import type { AudioCatalog, AudioMode, AudioModelInfo, AudioOp, AudioPrefs, AudioProvider, AudioThread } from '../api';
import { defaultOp, isNoAi, MODE_LABEL, opInfo } from '../ops';
import type { JobProgress } from '../thread/threadStore';

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
function autoOrder(catalog: AudioCatalog | null): AudioProvider[] {
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

// «podcast-intro.mp3 · версия 3» / «Новый звук»; short — без версии исходника
export function focusLabel(thread: AudioThread): string {
  const name = thread.file ? thread.file.split('/').pop()! : thread.name || 'Новый звук';
  const v = thread.versions.find(x => x.id === thread.currentVersionId);
  if (!v) return name;
  return `${name} · ${v.id === 'origin' ? 'исходник' : `версия ${v.number}`}`;
}

export interface SoundSummaryParts {
  focus: string | null;
  launch: ResolvedLaunch;
}

// short — кнопка-сводка: «Голос · Озвучить · Локально · Qwen3-TTS 1.7B · Аня · 2 вар. · бесплатно»;
// иначе — строка целиком: «Работаем с: intro.mp3 · версия 3 · …» / «Звук не выбран · …».
// У правок без ИИ поставщика, модели и вариантов нет: «Обработка · Склеить · без ИИ»
export function soundSummary({ focus, launch: L }: SoundSummaryParts, short = false): string {
  const model = L.model?.label ?? (L.auto ? 'Авто' : null);
  const body = isNoAi(L.op)
    ? [MODE_LABEL[L.mode], opInfo(L.op)?.label, 'без ИИ']
    : [MODE_LABEL[L.mode], opInfo(L.op)?.label, L.provider?.label, model, L.voice, `${L.count} вар.`, L.price];
  if (short) return body.filter(Boolean).join(' · ');
  return [focus ? `Работаем с: ${focus}` : 'Звук не выбран', ...body].filter(Boolean).join(' · ');
}

// Телефон: «Голос · бесплатно»
export const soundSummaryMobile = (L: ResolvedLaunch) => [MODE_LABEL[L.mode], isNoAi(L.op) ? 'без ИИ' : L.price].filter(Boolean).join(' · ');

const eta = (sec: number) => (sec < 60 ? `${sec} с` : `${Math.round(sec / 60)} мин`);

// Бейдж очереди GPU: ход идущей задачи нити; без задачи — только пометка тяжёлой локальной
// операции. null — бейджа нет (облако без запуска)
export function queueBadge(jobs: JobProgress[], runningWithoutProgress: boolean, L: ResolvedLaunch): string | null {
  const j = jobs[0];
  if (j) {
    if (j.stage === 'queued') {
      const pos = j.queuePosition && j.queuePosition > 0 ? `GPU: ${j.queuePosition}-я в очереди` : 'в очереди';
      return j.etaSeconds != null ? `${pos} · старт ≈ через ${eta(j.etaSeconds)}` : pos;
    }
    if (j.stage === 'downloading') return 'забираем результат';
    const of = j.count > 1 ? ` · вариант ${j.variant} из ${j.count}` : '';
    return `идёт: ${opInfo(L.op)?.label ?? 'генерация'}${of}${j.etaSeconds != null ? ` · ещё ≈ ${eta(j.etaSeconds)}` : ''}`;
  }
  if (runningWithoutProgress) return 'идёт генерация';
  if (L.heavy && L.provider?.key === 'local') return 'тяжёлая · одна за раз';
  return null;
}

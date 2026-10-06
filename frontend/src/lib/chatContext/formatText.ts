// Подписи «Формата» по записке docs/mockups/media-format-v1-proposal.md («Тексты чипа», «Тексты
// интерфейса»). Чистые функции без арифметики формата: размеры и фразы приходят от сервера.

import type { FormatPrice, FormatTierKey, FormatTierOption, MediaFormatState } from './formatTypes';

export const TIER_NAME: Readonly<Record<FormatTierKey, string>> = { standard: 'Стандарт', high: 'Высокое', max: 'Макс' };
export const TIER_ORDER: readonly FormatTierKey[] = ['standard', 'high', 'max'];

export const FORMAT_TEXT = {
  chat: 'Формат',
  asModel: 'Как у модели',
  asSource: 'Как у исходника',
  loading: 'Формат…',
  noCaps: (provider: string, model: string) => `${provider} не сообщил, какие размеры умеет ${model}, — рисуем её размером по умолчанию.`,
  noCapsMore: 'Пропорции и качество появятся, когда выберете модель с известными размерами.',
  changeModel: 'Сменить модель в «Чем»',
  exactLink: 'Точно… ширина × высота ›',
  exactBack: 'Вернуться к пропорциям',
  exactHide: 'Скрыть',
  exactOut: 'Уйдёт в модель:',
  askOutpaint: 'Дорисовать края',
  askCrop: 'Обрезать',
  askNote: 'Сама модель пропорции не тянет и не режет — только то, что выберете здесь.',
  anchors: { left: 'Слева', center: 'По центру', right: 'Справа' },
  anchorLabel: 'Рамка обрезки:',
  pendingChoice: 'Выберите в «Формате», как получить новые пропорции: дорисовать края или обрезать.',
  outpaintNeedsFormat: '«Дорисовать»: выберите в «Формате» пропорции, до которых дорисовать.',
  upscaleChip: 'Размер задаёт операция',
  upscaleNote: 'У «Увеличить» размер задаёт сама операция. Пропорции не меняются.',
} as const;

// «$0.08», «~$0.08», «2 кр.», «бесплатно»
export function priceLabel(p: FormatPrice | null | undefined): string {
  if (!p) return '';
  const tilde = p.approx ? '~' : '';
  if (p.unit === 'free') return 'бесплатно';
  if (p.unit === 'usd') return `${tilde}$${p.amount.toFixed(2)}`;
  return `${tilde}${Math.round(p.amount * 100) / 100} кр.`;
}

export const sizeLabel = (s: { w: number; h: number } | null | undefined) => (s ? `${s.w}×${s.h}` : '');

// Подпись под ступенью: размер и цена; серая — «нет у модели»
export function tierCaption(o: FormatTierOption): { size: string; price: string } {
  if (o.disabledReason) return { size: 'нет у модели', price: '' };
  return { size: o.caption ?? (o.size ? sizeLabel(o.size) : o.label), price: [priceLabel(o.price), o.extra].filter(Boolean).join(' · ') };
}

// Свёрнутый чип новой картинки: «16:9 · 1792×1024», «1920×1088 · точно», «Формат Как у модели»
export function generateChip(state: MediaFormatState | null) {
  if (!state) return { main: FORMAT_TEXT.loading, tone: 'dim' as const };
  if (!state.caps) return { prefix: FORMAT_TEXT.chat, main: FORMAT_TEXT.asModel, tone: 'dim' as const };
  const r = state.resolved;
  if (r.mode === 'exact' && r.width && r.height) {
    return { main: `${r.width}×${r.height}`, sub: '· точно', tone: r.rounded ? 'warn' as const : 'normal' as const };
  }
  const size = r.width && r.height ? `${r.width}×${r.height}` : null;
  return {
    main: r.aspect ?? FORMAT_TEXT.asModel,
    ...(size ? { sub: `· ${size}`, subShort: `· ${r.width}` } : null),
    tone: 'normal' as const,
  };
}

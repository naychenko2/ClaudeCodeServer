// «Что получить» в «Стемах» (вариант А, docs/mockups/audio-panel-v3-proposal.md): прямой вопрос
// «Вокал + минус / 4 / 6 / Караоке» вместо выбора модели — модель подставляется по caps.stemSet
// каталога, а не по id. Чистые функции — под тестом stems.test.ts.

import type { AudioModelInfo, AudioProvider, AudioStemSet } from '../api';
import type { SettingsPatch } from './model';

export const STEM_SETS: readonly { value: AudioStemSet; label: string; full: string }[] = [
  { value: 'vocals', label: 'Вокал + минус', full: 'вокал и минус' },
  { value: '4', label: '4', full: '4 дорожки' },
  { value: '6', label: '6', full: '6 дорожек' },
  { value: 'karaoke', label: 'Караоке', full: 'караоке' },
];

export const isStemSet = (v: unknown): v is AudioStemSet => STEM_SETS.some(s => s.value === v);

const stemModel = (p: AudioProvider | null, set: AudioStemSet): AudioModelInfo | null =>
  p?.models.find(m => m.caps.ops.includes('separate') && m.caps.stemSet === set) ?? null;

export interface StemChoice { value: AudioStemSet; label: string; disabled?: boolean; title?: string }

// Сегменты по поставщику, которым пойдёт запуск: чего у него нет — серое с причиной
export function stemChoices(provider: AudioProvider | null): StemChoice[] {
  return STEM_SETS.map(s => (stemModel(provider, s.value)
    ? { value: s.value, label: s.label }
    : { value: s.value, label: s.label, disabled: true, title: provider ? `У «${provider.label}» нет: ${s.full}` : 'Нет поставщика для стемов' }));
}

// Что выбрано сейчас: набор модели запуска; у модели без набора (SAM Audio) — ничего
export const stemValue = (model: AudioModelInfo | null): AudioStemSet | null => model?.caps.stemSet ?? null;

// Выбор сегмента → правка настроек. Модель той же раскладки уже стоит — ничего не меняем, «Авто»
// остаётся «Авто»; иначе явно берём поставщика и его модель с этим набором
export function stemPatch(provider: AudioProvider | null, model: AudioModelInfo | null, set: AudioStemSet): SettingsPatch | null {
  if (stemValue(model) === set) return null;
  const m = stemModel(provider, set);
  return provider && m ? { provider: provider.key, model: m.id } : null;
}

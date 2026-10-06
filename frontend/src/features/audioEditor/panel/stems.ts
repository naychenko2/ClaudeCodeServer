// Наборы «Стемов»: «Вокал + минус / 4 / 6 / Караоке» вместо выбора модели — модель подставляется по caps.stemSet
// каталога, а не по id.

import type { AudioStemSet } from '../api';

export const STEM_SETS: readonly { value: AudioStemSet; label: string; full: string }[] = [
  { value: 'vocals', label: 'Вокал + минус', full: 'вокал и минус' },
  { value: '4', label: '4 стема', full: '4 дорожки' },
  { value: '6', label: '6 стемов', full: '6 дорожек' },
  { value: 'karaoke', label: 'Караоке', full: 'караоке' },
];

export const isStemSet = (v: unknown): v is AudioStemSet => STEM_SETS.some(s => s.value === v);


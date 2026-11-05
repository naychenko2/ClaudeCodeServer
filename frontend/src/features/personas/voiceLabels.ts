import type { TtsVoiceInfo } from '../../types';

// Подписи амплуа голосов синтеза — ОДИН словарь на форму персоны и редактор звука: две копии
// разъехались бы при первом же новом амплуа. Ключи API человеку не показываем; неизвестный
// ключ — как есть, а не пустота.
const VOICE_ROLE_LABELS: Readonly<Record<string, string>> = {
  good: 'доброе',
  evil: 'злое',
  strict: 'строгое',
  friendly: 'дружелюбное',
  whisper: 'шёпот',
  // LiveTTS
  casual: 'непринуждённое',
  support: 'поддерживающее',
  sales: 'продающее',
  formal: 'официальное',
  narrator: 'дикторское',
  neutral: 'нейтральное',
};

export const voiceRoleLabel = (role: string): string => VOICE_ROLE_LABELS[role] ?? role;

// Цена LiveTTS — один раз в заголовке группы, а не бейджем в каждой строке: бейдж обрезал
// имя и характер. Человек видит её до выбора и в подборе («живой вариант»)
export const LIVE_TTS_PRICE = '≈0,25 ₽ за фразу';
export const LIVE_TTS_GROUP = `LiveTTS · живая речь, ${LIVE_TTS_PRICE}`;

// Подпись голоса с сервера — «Маша · живая, с широким диапазоном»: строка списка в две
// строки, имя сверху, характер под ним
export const voiceName = (v: Pick<TtsVoiceInfo, 'label' | 'voice'>): string =>
  v.label.split(' · ')[0] || v.voice;
export const voiceTrait = (v: Pick<TtsVoiceInfo, 'label'>): string =>
  v.label.split(' · ').slice(1).join(' · ');

export type VoiceGenderFilter = 'all' | 'female' | 'male';

// «ё» и «е» человек в поиске не различает
const fold = (s: string) => s.toLocaleLowerCase('ru').replace(/ё/g, 'е');

// Поиск по имени и характеру голоса (ключ API тоже: «madi» найдёт Мади) плюс фильтр пола
export function filterVoices(catalog: readonly TtsVoiceInfo[], query: string, gender: VoiceGenderFilter): TtsVoiceInfo[] {
  const q = fold(query.trim());
  return catalog.filter(v =>
    (gender === 'all' || v.gender === gender)
    && (!q || fold(`${v.label} ${v.voice}`).includes(q)));
}

// Фраза для прослушки кандидатов подбора — из приветствия персоны: целые предложения, пока
// не наберётся ~40 символов, потолок 120 (запрос тарифицируется до 249 символов, а
// сравнивать голоса удобнее на короткой фразе). Приветствия нет — общая фраза-образец
export function greetingSample(greeting: string | null | undefined): string | null {
  const text = (greeting ?? '').replace(/\s+/g, ' ').trim();
  if (!text) return null;
  let out = '';
  for (const s of text.split(/(?<=[.!?…])\s/)) {
    if (out.length >= 40) break;
    out = out ? `${out} ${s}` : s;
  }
  return out.length > 120 ? `${out.slice(0, 119).trimEnd()}…` : out;
}

export const isLiveTtsVoice = (v: Pick<TtsVoiceInfo, 'model'> | null | undefined): boolean =>
  v?.model === 'livetts';

// Старый сервер поля не шлёт — тогда темп есть у всех, как было до LiveTTS
export const voiceSupportsSpeed = (v: Pick<TtsVoiceInfo, 'speedSupported'> | null | undefined): boolean =>
  v?.speedSupported !== false;

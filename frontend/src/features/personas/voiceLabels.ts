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

// Метка голосов LiveTTS в списке: модель дороже, и человек должен видеть это до выбора
export const LIVE_TTS_BADGE = 'LiveTTS · дороже';

export const isLiveTtsVoice = (v: Pick<TtsVoiceInfo, 'model'> | null | undefined): boolean =>
  v?.model === 'livetts';

// Старый сервер поля не шлёт — тогда темп есть у всех, как было до LiveTTS
export const voiceSupportsSpeed = (v: Pick<TtsVoiceInfo, 'speedSupported'> | null | undefined): boolean =>
  v?.speedSupported !== false;

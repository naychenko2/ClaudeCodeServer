// Стиль озвучки НЕ настраивается — он выводится из устройства.
//
// Почему без выбора: два сценария озвучки различаются не вкусом, а тем, видит ли человек
// экран. Телефон в руке на ходу — ответ нужен короткий целиком ('talk'). За столом экран
// перед глазами — ответ нужен полный, а вслух только пересказ ('digest'). Ширина отвечает
// на этот вопрос точнее, чем переключатель, который пришлось бы дёргать при каждой смене
// устройства. Один и тот же чат, открытый с компа и с телефона, получает правильный стиль
// сам, без единого действия.
//
// Стиль не влияет на разговор без рук: петля работает на любом устройстве, от стиля
// зависит только длина ответа и то, что читается вслух.
//
// Сервер (Session.voiceStyle) хранит последнее выставленное значение — оно нужно ему,
// чтобы выбрать секцию промпта хода. Те же значения на бэке — Models/VoiceStyles.cs.

export type VoiceStyle = 'talk' | 'digest';

export const VOICE_STYLE_TALK: VoiceStyle = 'talk';
export const VOICE_STYLE_DIGEST: VoiceStyle = 'digest';

export function isVoiceStyle(v: unknown): v is VoiceStyle {
  return v === VOICE_STYLE_TALK || v === VOICE_STYLE_DIGEST;
}

// Пустое/битое/легаси значение — talk (то же правило, что у VoiceStyles.Normalize на бэке)
export function normalizeVoiceStyle(v: unknown): VoiceStyle {
  return isVoiceStyle(v) ? v : VOICE_STYLE_TALK;
}

// Единственная точка, где стиль вообще определяется. isMobile берётся из useIsMobile —
// то есть значение реактивно: повернул планшет или растянул окно, и следующий ход придёт
// в другом формате. Это не побочный эффект, а смысл автомата: формат следует за тем, как
// человек сейчас смотрит на экран.
export function voiceStyleFor(isMobile: boolean): VoiceStyle {
  return isMobile ? VOICE_STYLE_TALK : VOICE_STYLE_DIGEST;
}

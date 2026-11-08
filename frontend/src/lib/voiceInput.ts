// Флаг «клавиатурного» режима голосового ввода.
//
// Ставится ватчдогом useVoiceInput, когда Web Speech стартовал, но за отведённое время
// не подал признаков жизни (движок распознавания мёртвый — например, устройство без
// Google-сервисов). Пока флаг стоит, кнопка микрофона не пробует распознавание, а просто
// фокусирует поле, чтобы пользователь надиктовал системной диктовкой клавиатуры.
//
// Флаг включается не с первого промаха, а после MIC_DEAD_STRIKES «мёртвых» стартов
// подряд: на Android единичный медленный старт — норма (микрофон ещё держит диктовка
// клавиатуры, сервис Google просыпается), и один такой промах раньше выключал
// распознавание насовсем. Любой живой старт счётчик обнуляет. Сбросить вручную —
// пункт «Вернуть голосовой ввод» в меню аватара (виден, только когда флаг стоит).

const KEY = 'micKeyboardFallback';

// Сколько «мёртвых» стартов подряд переводят микрофон в клавиатурный режим
export const MIC_DEAD_STRIKES = 2;

function readStrikes(): number {
  try {
    const n = Number(localStorage.getItem(KEY));
    return Number.isFinite(n) && n > 0 ? n : 0;
  } catch { return 0; }
}

export function isMicKeyboardFallback(): boolean {
  return readStrikes() >= MIC_DEAD_STRIKES;
}

// Засчитать «мёртвый» старт; true — порог достигнут, микрофон ушёл в клавиатурный режим
export function setMicKeyboardFallback(): boolean {
  const next = readStrikes() + 1;
  try { localStorage.setItem(KEY, String(next)); } catch { /* localStorage недоступен — не критично */ }
  return next >= MIC_DEAD_STRIKES;
}

export function clearMicKeyboardFallback(): void {
  try { localStorage.removeItem(KEY); } catch { /* localStorage недоступен — не критично */ }
}

// Тост первого промаха: режим ещё не переключён, просим повторить
export const MIC_SLOW_TEXT =
  'Распознавание не ответило. Нажми микрофон ещё раз; если диктовка клавиатуры включена — закрой её.';

// Текст фоллбэка на клавиатуру. Тост клампится тремя строками (NotificationToasts),
// на планшетной ширине это ~120 символов — длиннее просто не увидят: прошлая версия
// теряла ровно подсказку про меню аватара.
export const MIC_FALLBACK_TEXT =
  'Распознавание недоступно. Микрофон открывает клавиатуру — диктуй ею. Вернуть распознавание — в меню аватара.';

// --- Ошибки распознавания ---
//
// Коды из SpeechRecognitionErrorEvent.error. Раньше onerror гасил их молча, и причина
// сбоя на устройстве была не видна — теперь показываем расшифровку в тосте.

// Тексты короткие: тост клампится тремя строками (NotificationToasts)
const ERROR_TEXT: Record<string, string> = {
  'network': 'нет связи с облаком распознавания Google — проверь VPN, прокси, DNS',
  'not-allowed': 'браузеру запрещён доступ к микрофону',
  'service-not-allowed': 'система не даёт доступ к распознаванию речи',
  'audio-capture': 'микрофон не найден или занят',
  'language-not-supported': 'язык ru-RU не поддерживается',
  'bad-grammar': 'движок отверг настройки распознавания',
};

// Штатные исходы, а не сбои: 'aborted' — мы сами прервали (отмена/ватчдог),
// 'no-speech' — пользователь промолчал. Тостом о них не сообщаем.
const SILENT_ERRORS = new Set(['aborted', 'no-speech']);

export function isSilentSpeechError(code: string): boolean {
  return SILENT_ERRORS.has(code);
}

export function describeSpeechError(code: string): string {
  const text = ERROR_TEXT[code];
  // Код показываем всегда — по нему причину видно даже без нашей расшифровки
  return text ? `${text} (${code})` : `неизвестная ошибка распознавания: ${code}`;
}

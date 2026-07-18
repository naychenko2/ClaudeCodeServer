// Флаг «клавиатурного» режима голосового ввода.
//
// Ставится ватчдогом в Composer, когда Web Speech стартовал, но за 2.5с не пришёл
// onaudiostart (движок распознавания мёртвый — например, устройство без Google-сервисов).
// Пока флаг стоит, кнопка микрофона не пробует распознавание, а просто фокусирует поле,
// чтобы пользователь надиктовал системной диктовкой клавиатуры.
//
// Флаг залипает до явного сброса — сбросить можно пунктом «Вернуть голосовой ввод»
// в меню аватара (пункт виден только когда флаг стоит).

const KEY = 'micKeyboardFallback';

export function isMicKeyboardFallback(): boolean {
  try { return localStorage.getItem(KEY) === '1'; } catch { return false; }
}

export function setMicKeyboardFallback(): void {
  try { localStorage.setItem(KEY, '1'); } catch { /* localStorage недоступен — не критично */ }
}

export function clearMicKeyboardFallback(): void {
  try { localStorage.removeItem(KEY); } catch { /* localStorage недоступен — не критично */ }
}

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

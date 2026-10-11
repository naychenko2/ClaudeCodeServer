// Превью сообщения сработавшего сторожа в списке чатов. Сторож пишет в чат
// «⏰ Сторож «{Name}»: …» (WatchdogService), а превью — голая строка без метки происхождения.
// Эмодзи рисует системный шрифт цветным, мимо токенов, — превью показывает вместо него
// монохромную иконку, а текст отдаёт без эмодзи. Текст в ленте и для модели не меняется.

// ⏰ (U+23F0), необязательный селектор вариации U+FE0F, пробел и «Сторож «»
const WATCHDOG_PREFIX = /^⏰️? (?=Сторож «)/;

export function splitWatchdogPreview(text: string): { watchdog: boolean; text: string } {
  const m = WATCHDOG_PREFIX.exec(text);
  return m ? { watchdog: true, text: text.slice(m[0].length) } : { watchdog: false, text };
}

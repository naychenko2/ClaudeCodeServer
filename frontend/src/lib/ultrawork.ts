// Детектор ключевого слова ultrawork — только для бейджа «⚡ ультра» на сообщении.
// Собственной серверной вставки больше нет: слова ловит keyword-detector плагина
// oh-my-claudecode, поэтому детектим ровно его набор (латиница; кириллицу хук не знает).
const ULTRAWORK_RE = /(?<![\p{L}\p{N}])(ultrawork|ulw)(?![\p{L}\p{N}])/iu;

// Есть ли в тексте сообщения ключевое слово максимального усилия
export function hasUltraworkKeyword(text: string): boolean {
  return ULTRAWORK_RE.test(text);
}

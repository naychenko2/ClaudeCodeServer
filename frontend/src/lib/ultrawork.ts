// Детектор ключевого слова «ультра» (флаг ultrawork-keyword).
// Правило то же, что на бэкенде: отдельные слова ultrawork/ulw/ультраворк/ультра,
// регистронезависимо; часть слова («ультразвук», «формула») не считается.
const ULTRAWORK_RE = /(?<![\p{L}\p{N}])(ultrawork|ulw|ультраворк|ультра)(?![\p{L}\p{N}])/iu;

// Есть ли в тексте сообщения ключевое слово максимального усилия
export function hasUltraworkKeyword(text: string): boolean {
  return ULTRAWORK_RE.test(text);
}

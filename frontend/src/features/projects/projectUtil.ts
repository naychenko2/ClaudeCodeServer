// Утилиты отображения проектов (иконка, время, склонения)

// Две буквы для иконки проекта: по первым буквам двух слов, иначе первые 2 буквы одного слова
// (по образцу personaInitials). Fallback — «?».
export function projectInitials(name: string): string {
  const t = name.trim();
  if (!t) return '?';
  const words = t.split(/\s+/).filter(Boolean);
  if (words.length >= 2) return (words[0][0] + words[1][0]).toUpperCase();
  return t.slice(0, 2).toUpperCase();
}

function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
}

export const pluralChats = (n: number) => plural(n, 'чат', 'чата', 'чатов');

// Относительное время: «только что», «5 мин назад», «2 дня назад», иначе дата
export function relativeTime(iso: string): string {
  const t = new Date(iso).getTime();
  const diff = (Date.now() - t) / 1000;
  if (diff < 60) return 'только что';
  if (diff < 3600) { const n = Math.floor(diff / 60); return `${n} ${plural(n, 'минуту', 'минуты', 'минут')} назад`; }
  if (diff < 86400) { const n = Math.floor(diff / 3600); return `${n} ${plural(n, 'час', 'часа', 'часов')} назад`; }
  if (diff < 7 * 86400) { const n = Math.floor(diff / 86400); return `${n} ${plural(n, 'день', 'дня', 'дней')} назад`; }
  return new Date(iso).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' });
}

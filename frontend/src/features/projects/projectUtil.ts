// Утилиты отображения проектов (плитка, время, склонения)

export const TILE_COLORS: [string, string][] = [
  ['#E7F0E8', '#3F7A4F'],
  ['#E6EEF5', '#3E7CA6'],
  ['#FBEBE0', '#C2693B'],
  ['#F2E6F0', '#8E4A82'],
];

export function firstLetter(name: string): string {
  return (name.trim().charAt(0) || '?').toUpperCase();
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

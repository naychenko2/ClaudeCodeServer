// Утилиты отображения проектов (плитка, время, склонения)

import { getEffectiveTheme } from '../../lib/themeMode';

// Пары [фон, текст] плашки-аватара проекта. Фон — светлый пастельный для
// СВЕТЛОЙ темы; в тёмной он заменяется на тёмный тонированный того же оттенка.
const TILE_COLORS_LIGHT: [string, string][] = [
  ['#E7F0E8', '#3F7A4F'],
  ['#E6EEF5', '#3E7CA6'],
  ['#FBEBE0', '#C2693B'],
  ['#F2E6F0', '#8E4A82'],
];

function parseHex(hex: string): [number, number, number] {
  const h = hex.replace('#', '');
  return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
}

// Светлый hex → полупрозрачный rgba (тёмный тонированный фон плашки в dark)
function hexToRgba(hex: string, a: number): string {
  const [r, g, b] = parseHex(hex);
  return `rgba(${r}, ${g}, ${b}, ${a})`;
}

// Осветлить hex к белому (amt 0..1) — для читаемой буквы на тёмном фоне
function lighten(hex: string, amt: number): string {
  const [r, g, b] = parseHex(hex);
  const mix = (c: number) => Math.round(c + (255 - c) * amt);
  return `rgb(${mix(r)}, ${mix(g)}, ${mix(b)})`;
}

// Цвета плашки проекта по индексу с учётом темы: [фон, текст]
export function tileColors(index: number): [string, string] {
  const [bg, fg] = TILE_COLORS_LIGHT[index % TILE_COLORS_LIGHT.length];
  // В тёмной теме: тёмный тонированный фон (rgba от оттенка) + осветлённая буква
  // того же оттенка — иначе тёмная буква на тёмном фоне даёт слабый контраст
  if (getEffectiveTheme() === 'dark') return [hexToRgba(fg, 0.22), lighten(fg, 0.4)];
  return [bg, fg];
}

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

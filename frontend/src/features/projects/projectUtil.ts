// Утилиты отображения проектов (иконка, цвет, время, склонения)
import type { Project } from '../../types';
import { agentDotColor } from '../../components/AgentSelector';
import { projectColor } from '../../lib/tasks';

// Фирменный цвет проекта: icon.color из палитры AGENT_COLORS; если не задан —
// детерминированный projectColor(id), чтобы старые проекты без иконки не «поблели».
// Единая точка: этим цветом красятся плашка иконки (ProjectIcon) и номерки чатов
// в доке стены (WallDock).
export function projectMainColor(project: Project): string {
  return project.icon?.color ? agentDotColor(project.icon.color) : projectColor(project.id).main;
}

// Две буквы для иконки проекта: по первым буквам двух слов, иначе первые 2 буквы одного слова
// (по образцу personaInitials). Fallback — «?».
// Режем по code points ([...s], а не по code units [i]/slice), иначе эмодзи и прочие
// символы вне BMP рвутся пополам на суррогатной паре и дают кракозябру.
export function projectInitials(name: string): string {
  const t = name.trim();
  if (!t) return '?';
  const first = (s: string, n: number) => [...s].slice(0, n).join('');
  // Бьём и по пробелам, и по дефису/подчёркиванию — kebab/snake-имена дают инициалы
  // по кускам: «claude-code-server» → CC, «my_project» → MP.
  const words = t.split(/[\s\-_]+/).filter(Boolean);
  if (words.length >= 2) return (first(words[0], 1) + first(words[1], 1)).toUpperCase();
  return first(t, 2).toUpperCase();
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

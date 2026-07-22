import type { Session } from '../types';

export interface ChatGroup {
  title: string;
  items: Session[];
}

const weekday = (d: Date) => d.toLocaleDateString('ru-RU', { weekday: 'short' });

// Заголовок группы для дня старше вчерашнего: «14 июля (пн)».
// День недели помогает сориентироваться быстрее числа; год — если он не текущий
function dayTitle(d: Date): string {
  const opts: Intl.DateTimeFormatOptions = d.getFullYear() === new Date().getFullYear()
    ? { day: 'numeric', month: 'long' }
    : { day: 'numeric', month: 'long', year: 'numeric' };
  return `${d.toLocaleDateString('ru-RU', opts)} (${weekday(d)})`;
}

// Группировка чатов для сайдбара: Закреплённые → Сегодня → Вчера → по дням.
// Дни идут отдельными группами (а не общим «Ранее») — по разделителю видно,
// какие чаты относятся к одной дате. Внутри группы — свежие сверху.
export function groupChats(chats: Session[]): ChatGroup[] {
  const byDate = [...chats].sort(
    (a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime()
  );

  const pinned = byDate.filter(c => c.isPinned);
  const rest = byDate.filter(c => !c.isPinned);

  const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const today = startOfDay(new Date());
  const day = 86_400_000;

  const todayItems: Session[] = [];
  const yesterdayItems: Session[] = [];
  // Дни старше вчерашнего — своей группой каждый; порядок вставки уже от свежих к старым
  const earlierDays = new Map<number, Session[]>();
  for (const c of rest) {
    const d = startOfDay(new Date(c.updatedAt));
    if (d >= today) todayItems.push(c);
    else if (d >= today - day) yesterdayItems.push(c);
    else {
      const bucket = earlierDays.get(d);
      if (bucket) bucket.push(c);
      else earlierDays.set(d, [c]);
    }
  }

  const groups: ChatGroup[] = [];
  if (pinned.length) groups.push({ title: 'Закреплённые', items: pinned });
  if (todayItems.length) groups.push({ title: 'Сегодня', items: todayItems });
  if (yesterdayItems.length)
    groups.push({ title: `Вчера (${weekday(new Date(today - day))})`, items: yesterdayItems });
  for (const [d, items] of earlierDays) groups.push({ title: dayTitle(new Date(d)), items });
  return groups;
}

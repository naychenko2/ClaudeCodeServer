import type { Session } from '../types';

export interface ChatGroup {
  title: string;
  items: Session[];
}

// Группировка чатов для сайдбара: Закреплённые → Сегодня → Вчера → Ранее.
// Внутри каждой группы — по времени обновления (свежие сверху).
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
  const earlierItems: Session[] = [];
  for (const c of rest) {
    const d = startOfDay(new Date(c.updatedAt));
    if (d >= today) todayItems.push(c);
    else if (d >= today - day) yesterdayItems.push(c);
    else earlierItems.push(c);
  }

  const groups: ChatGroup[] = [];
  if (pinned.length) groups.push({ title: 'Закреплённые', items: pinned });
  if (todayItems.length) groups.push({ title: 'Сегодня', items: todayItems });
  if (yesterdayItems.length) groups.push({ title: 'Вчера', items: yesterdayItems });
  if (earlierItems.length) groups.push({ title: 'Ранее', items: earlierItems });
  return groups;
}

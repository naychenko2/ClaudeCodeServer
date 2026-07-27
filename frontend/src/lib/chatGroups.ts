import type { ProjectTag, Session } from '../types';
import { sortTagsByRegistry } from './tagRegistry';

export interface ChatGroup {
  title: string;
  items: Session[];
}

// Секция режима «Теги»: tag — имя тега из реестра/сирота, null — хвост «Без тегов».
// registryTag — запись реестра (для цвета точки и кнопок порядка ▲▼); у сирот и хвоста её нет.
export interface TagChatGroup extends ChatGroup {
  tag: string | null;
  registryTag?: ProjectTag;
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

const startOfDayTs = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();

// Заголовок группы дня: «Сегодня» / «Вчера (пн)» / «14 июля (пн)». Общий для
// списков чатов и коммитов — даты в разделителях выглядят одинаково везде.
export function dayGroupTitle(d: Date): string {
  const today = startOfDayTs(new Date());
  const t = startOfDayTs(d);
  if (t >= today) return 'Сегодня';
  if (t >= today - 86_400_000) return `Вчера (${weekday(d)})`;
  return dayTitle(d);
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

  // Заголовки — общим dayGroupTitle (тот же текст, что у разделителей коммитов)
  const groups: ChatGroup[] = [];
  if (pinned.length) groups.push({ title: 'Закреплённые', items: pinned });
  if (todayItems.length) groups.push({ title: 'Сегодня', items: todayItems });
  if (yesterdayItems.length)
    groups.push({ title: dayGroupTitle(new Date(today - day)), items: yesterdayItems });
  for (const [d, items] of earlierDays) groups.push({ title: dayGroupTitle(new Date(d)), items });
  return groups;
}

// Группировка чатов для режима «Теги»: секция на каждый тег реестра (в его порядке),
// затем секции тегов-сирот (тег есть у чата, записи в реестре нет — по алфавиту),
// в конце — хвост «Без тегов». Чат с НЕСКОЛЬКИМИ тегами дублируется в каждой своей
// секции — иначе из-под остальных его тегов он был бы не найти. Пустые секции
// (тег реестра без единого чата) не рисуются.
// Внутри секции — по свежести (updatedAt), как и в дневных группах.
export function groupByTags(chats: Session[], registry: ProjectTag[]): TagChatGroup[] {
  const sorted = [...chats].sort(
    (a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime()
  );

  // tag → чаты (ключ — имя как у чата; каноничный регистр берём из реестра при сборке)
  const byTag = new Map<string, Session[]>();
  const untagged: Session[] = [];
  for (const c of sorted) {
    const tags = c.tags ?? [];
    if (tags.length === 0) { untagged.push(c); continue; }
    for (const t of new Set(tags)) {
      const bucket = byTag.get(t);
      if (bucket) bucket.push(c); else byTag.set(t, [c]);
    }
  }

  const groups: TagChatGroup[] = [];
  const seen = new Set<string>(); // имена в нижнем регистре — реестровый дубль сироты
  // Реестровые секции — в порядке реестра (массив уже упорядочен бэком)
  for (const rt of registry) {
    // Имя у чата может отличаться регистром — ищем фактический ключ без учёта регистра
    const key = [...byTag.keys()].find(k => k.toLowerCase() === rt.name.toLowerCase());
    seen.add(rt.name.toLowerCase());
    if (!key) continue; // тег без чатов — секцию не рисуем
    groups.push({ title: rt.name, tag: rt.name, registryTag: rt, items: byTag.get(key)! });
  }
  // Сироты — после реестровых, по алфавиту
  const orphans = [...byTag.keys()]
    .filter(k => !seen.has(k.toLowerCase()))
    .sort((a, b) => a.localeCompare(b));
  for (const k of orphans) groups.push({ title: k, tag: k, items: byTag.get(k)! });
  // Хвост
  if (untagged.length) groups.push({ title: 'Без тегов', tag: null, items: untagged });
  return groups;
}

// Чипы тегов чата в порядке реестра (для карточки): реестровые по order, сироты в конец.
export function chatTagsSorted(chat: Session, registry: ProjectTag[]): string[] {
  return sortTagsByRegistry(chat.tags ?? [], registry);
}

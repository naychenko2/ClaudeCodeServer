import type { ProjectSubject, ProjectTag, Session } from '../types';
import type { FeatureIndexFeature, FeaturesIndex } from '../types/featureIndex';
import { sortTagsByRegistry } from './tagRegistry';
import { sortKeyOf, type ChatSortOrder } from './chatFilters';

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
// Обратное направление ('oldest'/'created-oldest'): порядок секций и порядок внутри
// них обращается, а секция «Закреплённые» всё равно остаётся первой (макет
// chat-unified-view). Поле даты — из sortKeyOf: в режимах 'created-*' и порядок,
// и сами секции дней считаются по createdAt («Сегодня» = чат создан сегодня).
export function groupChats(chats: Session[], sortOrder: ChatSortOrder = 'newest'): ChatGroup[] {
  const { field, dir } = sortKeyOf(sortOrder);
  const ts = (c: Session) => new Date(c[field]).getTime();
  const byDate = [...chats].sort((a, b) => dir * (ts(a) - ts(b)));

  const pinned = byDate.filter(c => c.isPinned);
  const rest = byDate.filter(c => !c.isPinned);

  const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const today = startOfDay(new Date());
  const day = 86_400_000;

  const todayItems: Session[] = [];
  const yesterdayItems: Session[] = [];
  // Дни старше вчерашнего — своей группой каждый; порядок вставки уже в направлении sortOrder
  const earlierDays = new Map<number, Session[]>();
  for (const c of rest) {
    const d = startOfDay(new Date(ts(c)));
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
  const earlier = [...earlierDays].map(([d, items]) => ({ title: dayGroupTitle(new Date(d)), items }));
  const todayGroup = { title: 'Сегодня', items: todayItems };
  const yesterdayGroup = { title: dayGroupTitle(new Date(today - day)), items: yesterdayItems };
  if (pinned.length) groups.push({ title: 'Закреплённые', items: pinned });
  if (dir === 1) {
    // Старые дни сверху → вчера → сегодня в самом низу
    groups.push(...earlier);
    if (yesterdayItems.length) groups.push(yesterdayGroup);
    if (todayItems.length) groups.push(todayGroup);
  } else {
    if (todayItems.length) groups.push(todayGroup);
    if (yesterdayItems.length) groups.push(yesterdayGroup);
    groups.push(...earlier);
  }
  return groups;
}

// Группировка чатов для режима «Теги»: секция на каждый тег реестра (в его порядке),
// затем секции тегов-сирот (тег есть у чата, записи в реестре нет — по алфавиту),
// в конце — хвост «Без тегов». Чат с НЕСКОЛЬКИМИ тегами дублируется в каждой своей
// секции — иначе из-под остальных его тегов он был бы не найти. Пустые секции
// (тег реестра без единого чата) не рисуются.
// Внутри секции — по дате из sortKeyOf (активность или создание) в направлении
// sortOrder; порядок секций от sortOrder не зависит — он реестровый (ручной, ▲▼).
export function groupByTags(chats: Session[], registry: ProjectTag[], sortOrder: ChatSortOrder = 'newest'): TagChatGroup[] {
  const { field, dir } = sortKeyOf(sortOrder);
  const sorted = [...chats].sort(
    (a, b) => dir * (new Date(a[field]).getTime() - new Date(b[field]).getTime())
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

// Секция режима «Темы»: subject — тема реестра, null — хвост «Без темы»
export interface SubjectChatGroup extends ChatGroup {
  subject: ProjectSubject | null;
}

export const NO_SUBJECT_KEY = '__no_subject__';

// Группировка чатов для режима «Темы»: секции тем в порядке реестра
// (order), затем хвост «Без темы». В отличие от тегов чат ровно в одной секции; висячий
// subjectId (темы нет в реестре) — «Без темы»; тема без чатов секцией не рисуется.
// Секцию задаёт КОРЕНЬ ветки (rootOf): тема живёт у корня, а вложенный чат иначе забил бы
// хвост «Без темы» — классификатор вложенные не трогает. Для дерева, где на вход идут
// одни корни, rootOf — тождество.
// Внутри секции закреплённые — первыми, дальше по дате из sortKeyOf (как sortChatsFlat):
// отдельной секции «Закреплённые» нет, иначе раскладка перестала бы быть однозначной.
export function groupBySubjects(
  chats: Session[], registry: ProjectSubject[], sortOrder: ChatSortOrder = 'newest',
  rootOf: (s: Session) => Session = s => s,
): SubjectChatGroup[] {
  const ordered = [...registry].sort((a, b) => a.order - b.order);
  const known = new Set(ordered.map(s => s.id));
  const bySubject = new Map<string, Session[]>();
  const rest: Session[] = [];
  for (const c of sortChatsFlat(chats, sortOrder)) {
    const sid = rootOf(c).subjectId;
    if (!sid || !known.has(sid)) { rest.push(c); continue; }
    const bucket = bySubject.get(sid);
    if (bucket) bucket.push(c); else bySubject.set(sid, [c]);
  }
  const groups: SubjectChatGroup[] = [];
  for (const s of ordered) {
    const items = bySubject.get(s.id);
    if (items) groups.push({ title: s.name, subject: s, items });
  }
  if (rest.length) groups.push({ title: 'Без темы', subject: null, items: rest });
  return groups;
}

// Чипы тегов чата в порядке реестра (для карточки): реестровые по order, сироты в конец.
export function chatTagsSorted(chat: Session, registry: ProjectTag[]): string[] {
  return sortTagsByRegistry(chat.tags ?? [], registry);
}

// Плоский список без группировки (groupBy='none'): закреплённые всегда первыми,
// дальше — по дате из sortKeyOf в направлении sortOrder.
export function sortChatsFlat(chats: Session[], sortOrder: ChatSortOrder = 'newest'): Session[] {
  const { field, dir } = sortKeyOf(sortOrder);
  return [...chats].sort((a, b) => {
    const pin = Number(b.isPinned ?? false) - Number(a.isPinned ?? false);
    if (pin !== 0) return pin;
    return dir * (new Date(a[field]).getTime() - new Date(b[field]).getTime());
  });
}

// === Режим «Фичи» (фасет «Фичи», флаг features-overview) ===

// Секция режима «Фичи»: feature — строка фичи из индекса, null — хвост «Без фичи».
// sessions — чаты группы с ЭФФЕКТИВНЫМ parentSessionId: межгрупповые рёбра дерева
// обрезаны (ребро живёт только внутри группы), поэтому список готов к подаче в
// buildChatTreeRows: оно построит дерево группы, а чат через границу фичи всплывает
// в корень своей группы (контракт «Контракт индекса фич — утверждён (Александр, 18.10)», §3).
export interface FeatureChatGroup {
  feature: FeatureIndexFeature | null;
  title: string;
  sessions: Session[];
}

// Группировка чатов для режима «Фичи». Правило дерева (контракт §3, «исправлено»):
//   1. Группа чата — первая фича из index.chats[id] (список упорядочен по силе связи).
//   2. У чата без фичи — группа родителя по parentSessionId, рекурсивно; корень без
//      фичи уходит в «Без фичи».
//   3. Ребро родитель→ребёнок сохраняется, только если оба в одной группе; иначе
//      ребёнок становится корнем в своей группе (всплывает — как у висячего родителя).
//   4. ParentOverrideId не обрабатывается отдельно: он уже в parentSessionId и
//      подчиняется п.3 — ручной родитель действует внутри фичи, через границу всплывает.
//   5. Мультифичевый чат — в группе первой фичи, строка одна (без дублей).
//
// Порядок групп = порядок features[] бэка (OrderRuns; фронт НЕ пересортировывает).
// Закрытые фичи (bucket=closed) уже в конце OrderRuns — это и есть «отдельная секция»;
// «Без фичи» — после них. Пустая группа (фича без чатов в видимом множестве) не рисуется.
//
// Функция чистая: Session не мутирует — в группы уходят КОПИИ с подменённым
// parentSessionId. sortOrder — для стабильного порядка копий внутри группы (единообразие
// с остальными группировщиками); порядок корней дерева строит сам buildChatTreeRows.
export function groupByFeatures(
  chats: Session[],
  index: FeaturesIndex,
  sortOrder: ChatSortOrder = 'newest',
): FeatureChatGroup[] {
  const byId = new Map(chats.map(c => [c.id, c]));
  const known = new Set(index.features.map(f => f.featureId));

  // 1–2. Группа каждого чата: первая фича из chats[id], иначе группа родителя
  // (рекурсивно), иначе null («Без фичи»). seen обрывает цикл parentSessionId.
  const groupOf = new Map<string, string | null>();
  const resolve = (id: string, seen: Set<string>): string | null => {
    const cached = groupOf.get(id);
    if (cached !== undefined) return cached;
    if (seen.has(id)) return null;
    seen.add(id);
    const links = index.chats[id];
    let g: string | null;
    if (links && links.length > 0) {
      g = links[0].featureId;
    } else {
      const parent = byId.get(id)?.parentSessionId ?? null;
      g = parent && parent !== id ? resolve(parent, seen) : null;
    }
    groupOf.set(id, g);
    return g;
  };
  for (const c of chats) resolve(c.id, new Set());

  // 3. Эффективный parent: ребро держим, только если родитель в этой же группе
  // (и есть во входе). Иначе null — чат становится корнем своей группы.
  const effectiveParent = (c: Session): string | null => {
    const pid = c.parentSessionId;
    if (!pid || pid === c.id) return null;
    if (!byId.has(pid)) return null;
    return groupOf.get(pid) === groupOf.get(c.id) ? pid : null;
  };

  // 5. Разбивка по группам (ключ — featureId, null = «Без фичи»).
  const byGroup = new Map<string | null, Session[]>();
  for (const c of chats) {
    const g = groupOf.get(c.id) ?? null;
    const bucket = byGroup.get(g);
    if (bucket) bucket.push(c); else byGroup.set(g, [c]);
  }

  // Порядок внутри группы — по дате из sortKeyOf (стабильный порядок копий).
  const { field, dir } = sortKeyOf(sortOrder);
  const sortInGroup = (items: Session[]): Session[] =>
    [...items].sort((a, b) => dir * (new Date(a[field]).getTime() - new Date(b[field]).getTime()));
  const withEffectiveParent = (items: Session[]): Session[] =>
    sortInGroup(items).map(c => ({ ...c, parentSessionId: effectiveParent(c) }));

  const groups: FeatureChatGroup[] = [];
  // Фичи — строго в порядке features[] (OrderRuns); закрытые уже в конце.
  for (const f of index.features) {
    const items = byGroup.get(f.featureId);
    if (!items) continue; // фича без чатов — секцию не рисуем
    groups.push({ feature: f, title: f.title, sessions: withEffectiveParent(items) });
  }
  // «Без фичи» — хвост: чаты без фичи (null) + висячие featureId (нет в features[]),
  // иначе такие чаты потерялись бы (группа есть, строки фичи — нет).
  const rest: Session[] = [...(byGroup.get(null) ?? [])];
  for (const [g, items] of byGroup) {
    if (g !== null && !known.has(g)) rest.push(...items);
  }
  if (rest.length) {
    groups.push({ feature: null, title: 'Без фичи', sessions: withEffectiveParent(rest) });
  }
  return groups;
}

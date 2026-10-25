import type { ProjectSubject, ProjectTag, Session } from '../types';
import type { FeatureChip, FeatureIndexFeature, FeatureRailKey, FeaturesIndex } from '../types/featureIndex';
import { sortTagsByRegistry } from './tagRegistry';
import { sortKeyOf, type ChatGroupBy, type ChatSortOrder } from './chatFilters';
import { buildChatTreeRows, type ChatTreeRowData } from './chatTree';

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

// Группировка чатов для режима «Фичи». Правило дерева (контракт §3, изменено 24.10 —
// решение Андрея, вариант 1):
//   1. Группа чата — ВСЕ featureId из index.chats[id]: мультифичевый чат показывается
//      в каждой своей группе один раз, с пометкой «общий с …» в строке.
//   2. У чата без фичи — группа родителя по parentSessionId, рекурсивно; корень без
//      фичи уходит в «Без фичи».
//   3. Ребро родитель→ребёнок сохраняется, только если родитель тоже в этой группе;
//      иначе ребёнок становится корнем в своей группе (всплывает — как у висячего родителя).
//   4. ParentOverrideId не обрабатывается отдельно: он уже в parentSessionId и
//      подчиняется п.3 — ручной родитель действует внутри фичи, через границу всплывает.
//   5. Висячий featureId (нет в features[]) группы не создаёт: у чата с ним своей
//      группы нет, и он идёт по п.2 — в группу родителя (рекурсивно); в «Без фичи»
//      уходит только если и цепочка родителей без фичи.
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

  // 1–2. Группы каждого чата: все известные featureId из chats[id]; если их нет —
  // группы родителя (рекурсивно), иначе пустой список (null → «Без фичи»). Висячие
  // featureId (нет в features[]) группы не дают. seen обрывает цикл parentSessionId.
  const groupsOf = new Map<string, string[]>();
  const resolve = (id: string, seen: Set<string>): string[] => {
    const cached = groupsOf.get(id);
    if (cached !== undefined) return cached;
    if (seen.has(id)) return [];
    seen.add(id);
    const links = index.chats[id];
    let g: string[] = links ? [...new Set(links.map(l => l.featureId).filter(fid => known.has(fid)))] : [];
    if (g.length === 0) {
      const parent = byId.get(id)?.parentSessionId ?? null;
      g = parent && parent !== id ? resolve(parent, seen) : [];
    }
    groupsOf.set(id, g);
    return g;
  };
  for (const c of chats) resolve(c.id, new Set());

  // 3. Эффективный parent для конкретной группы: ребро держим, только если родитель
  // тоже в этой группе (и есть во входе). Иначе null — чат становится корнем группы.
  const effectiveParent = (c: Session, group: string | null): string | null => {
    const pid = c.parentSessionId;
    if (!pid || pid === c.id) return null;
    if (!byId.has(pid)) return null;
    const pg = groupsOf.get(pid) ?? [];
    return group === null ? (pg.length === 0 ? pid : null) : (pg.includes(group) ? pid : null);
  };

  // 1, 5. Разбивка по группам (ключ — featureId, null = «Без фичи»): мультифичевый
  // чат попадает в каждую свою группу ровно один раз.
  const byGroup = new Map<string | null, Session[]>();
  for (const c of chats) {
    const gs = groupsOf.get(c.id) ?? [];
    if (gs.length === 0) {
      const bucket = byGroup.get(null);
      if (bucket) bucket.push(c); else byGroup.set(null, [c]);
    } else {
      for (const g of gs) {
        const bucket = byGroup.get(g);
        if (bucket) bucket.push(c); else byGroup.set(g, [c]);
      }
    }
  }

  // Порядок внутри группы — по дате из sortKeyOf (стабильный порядок копий).
  const { field, dir } = sortKeyOf(sortOrder);
  const sortInGroup = (items: Session[]): Session[] =>
    [...items].sort((a, b) => dir * (new Date(a[field]).getTime() - new Date(b[field]).getTime()));
  const withEffectiveParent = (items: Session[], group: string | null): Session[] =>
    sortInGroup(items).map(c => ({ ...c, parentSessionId: effectiveParent(c, group) }));

  const groups: FeatureChatGroup[] = [];
  // Фичи — строго в порядке features[] (OrderRuns); закрытые уже в конце.
  for (const f of index.features) {
    const items = byGroup.get(f.featureId);
    if (!items) continue; // фича без чатов — секцию не рисуем
    groups.push({ feature: f, title: f.title, sessions: withEffectiveParent(items, f.featureId) });
  }
  // «Без фичи» — хвост: чаты, у которых нет ни своей известной фичи, ни группы
  // родителя (в том числе с висячими featureId при пустой цепочке родителей).
  const rest = byGroup.get(null);
  if (rest && rest.length) {
    groups.push({ feature: null, title: 'Без фичи', sessions: withEffectiveParent(rest, null) });
  }
  return groups;
}

// === Заголовок группы фичи (макет features-overview.html, .fgh) ===

// Имя стадии конвейера по ключу — для статуса «{этап} · доработка/идёт».
// Порядок и названия — те же, что в FeatureRail (STAGE_NAME).
// Экспортируется: страница фичи (волна 3) строит тег «идёт · {этап}» по run.state
// и активной стадии — имя стадии берётся отсюда, не дублируется.
export const STAGE_NAME: Record<FeatureRailKey, string> = {
  cut: 'Разрез', design: 'Дизайн', plan: 'План', prod: 'Производство', review: 'Ревью',
  qa: 'Проверка', final: 'Финальное ревью', merge: 'Мерж',
};

// Визуальное состояние фичи для заголовка группы: глиф, кромка тревоги, статус.
// Бэк считает bucket/attention/rail/summary (контракт индекса фич); фронт только
// раскладывает их в глиф/кромку/статус — не пересчитывает. Чистая функция.
// Значения — подмножество глифа FeatureGlyph (wait/stopped/run/loop/closed/done):
// поэтому st.kind передаётся в FeatureGlyph БЕЗ каста, и tsc ловит рассинхрон.
// Смерженная фича → 'done' (галочка, макет glyph('done')), закрытая без мержа → 'closed'.
export type FeatureDisplayKind = 'wait' | 'stopped' | 'run' | 'loop' | 'closed' | 'done';

export interface FeatureDisplayState {
  // Глиф (FeatureGlyph): wait — ромб, stopped — квадрат, run/loop/closed/done.
  kind: FeatureDisplayKind;
  // Кромка тревоги 3px: 'wait' (ждёт вас, info) / 'stop' (упала, danger) / null.
  alarm: 'wait' | 'stop' | null;
  // Статус-подпись в заголовке.
  status: string;
}

// Длительность в человекочитаемом виде: «25 мин», «2 ч 15 мин», «3 дн 6 ч».
// Единый формат для статусов тревоги («стоит · … · 25 мин») и закрытых фич
// («смержена … · 3 дн 6 ч»). Чистая функция — тестируется без React.
export function formatDuration(minutes: number): string {
  if (!Number.isFinite(minutes) || minutes < 1) return '1 мин';
  if (minutes < 60) return `${Math.round(minutes)} мин`;
  const h = Math.floor(minutes / 60);
  const m = Math.round(minutes % 60);
  if (h < 24) return m ? `${h} ч ${m} мин` : `${h} ч`;
  const d = Math.floor(h / 24);
  const rh = h % 24;
  return rh ? `${d} дн ${rh} ч` : `${d} дн`;
}

// Единая формулировка состояния merge_pending (макет feature-banner-1): «ждёт согласия на мерж».
// Используется в плашке фичи, шапке группы, карточке чата и теге страницы — одно место,
// чтобы не разъезжалась (ревью фронта 1, major 1).
export const MERGE_PENDING_STATUS = 'ждёт согласия на мерж';

// enrich — необязательное «кто · сколько» (макет: «стоит · Денис · 25 мин»):
//   personaName — имя персоны (attention.personaId / now0.personaId, резолвит вызывающий);
//   now         — «сейчас» в мс (для стабильных тестов; не задано — Date.now()).
// Без enrich статус базовый («стоит», «ждёт вас») — поведение прежних вызовов не меняется.
export function featureDisplayState(
  f: FeatureIndexFeature,
  enrich?: { now?: number; personaName?: string | null },
): FeatureDisplayState {
  const who = enrich?.personaName ?? null;
  // «Сколько» — по attention.since (с какого момента стоит/ждёт); null — нет данных.
  const sinceMin = f.attention?.since
    ? Math.max(0, Math.round(((enrich?.now ?? Date.now()) - new Date(f.attention.since).getTime()) / 60000))
    : null;
  const dur = sinceMin != null ? formatDuration(sinceMin) : null;
  const join = (base: string, w: string | null, d: string | null): string =>
    `${base}${w ? ` · ${w}` : ''}${d ? ` · ${d}` : ''}`;

  // Закрыта: смержена (summary.mergedAt) или закрыта без мержа.
  if (f.bucket === 'closed') {
    const merged = f.summary?.mergedAt != null;
    return { kind: merged ? 'done' : 'closed', alarm: null, status: merged ? 'смержена' : 'закрыта' };
  }
  // Смержена, но ещё не закрыта (bucket=merged): ждёт закрытия.
  if (f.bucket === 'merged') return { kind: 'done', alarm: null, status: 'смержена, ждёт закрытия' };
  // Источник правды — f.state (бэк Б1a/Б1b): merge_pending → «ждёт согласия на мерж»,
  // merging → «идёт», waiting → «ждёт вас», stopped → «стоит». Без state (старые данные/кеш)
  // — запасной путь по attention (attention.kind), чтобы существующие тесты не упали.
  if (f.state) {
    if (f.state === 'stopped') return { kind: 'stopped', alarm: 'stop', status: join('стоит', who, dur) };
    if (f.state === 'merge_pending') return { kind: 'wait', alarm: 'wait', status: join(MERGE_PENDING_STATUS, null, dur) };
    if (f.state === 'waiting') return { kind: 'wait', alarm: 'wait', status: join('ждёт вас', who, dur) };
    if (f.state === 'merging') return { kind: 'run', alarm: null, status: join('Мерж идёт', who, null) };
    // merged/closed — bucket выше; running/not_started/idle — «идёт» (ветка ниже).
  }
  // Запасной путь: state в данных нет (кеш/моки) — прежний признак (attention).
  if (f.attention) {
    if (f.attention.kind === 'stopped') return { kind: 'stopped', alarm: 'stop', status: join('стоит', who, dur) };
    if (f.attention.kind === 'merge') return { kind: 'wait', alarm: 'wait', status: join(MERGE_PENDING_STATUS, null, dur) };
    return { kind: 'wait', alarm: 'wait', status: join('ждёт вас', who, dur) };
  }
  // Идёт: по активной стадии рельсы — доработка (loop) или работа (run). «Кто» —
  // исполнитель (now0.personaId); «сколько» для run/loop не ведётся (now0 без длительности).
  const active = f.rail.find(s => s.state === 'active');
  if (active?.tone === 'loop') return { kind: 'loop', alarm: null, status: join(`${STAGE_NAME[active.key]} · доработка`, who, null) };
  if (active) return { kind: 'run', alarm: null, status: join(`${STAGE_NAME[active.key]} · идёт`, who, null) };
  return { kind: 'run', alarm: null, status: join('идёт', who, null) };
}

// Тег статуса в шапке страницы фичи (волна 2, шаг Ф2.4; макет §5.1): короткое слово
// + тон (run/wait/merge/stop/loop/merged/closed). Бэк считает bucket/attention/rail/
// summary (контракт индекса фич); фронт только раскладывает в тег — не вычисляет.
// Чистая функция. От `featureDisplayState` отличается: тот даёт длинный статус
// («стоит · Денис · 25 мин») для плашки, здесь — короткое слово для тега шапки.
export type FeatureStatusTone = 'run' | 'wait' | 'merge' | 'stop' | 'loop' | 'merged' | 'closed';
export interface FeatureStatusTag { label: string; tone: FeatureStatusTone }

export function featureStatusTag(f: FeatureIndexFeature): FeatureStatusTag {
  if (f.bucket === 'closed') {
    return f.summary?.mergedAt != null
      ? { label: 'Смержена', tone: 'merged' }
      : { label: 'Без мержа', tone: 'closed' };
  }
  if (f.bucket === 'merged') return { label: 'Смержена', tone: 'merged' };
  // Источник правды — f.state (бэк Б1a/Б1b): merge_pending → «К мержу», merging → «Идёт»,
  // waiting → «Ждёт вас», stopped → «Стоит». Без state (старые данные/кеш) — запасной путь
  // по attention (attention.kind), чтобы существующие тесты не упали.
  if (f.state) {
    if (f.state === 'stopped') return { label: 'Стоит', tone: 'stop' };
    if (f.state === 'merge_pending') return { label: 'К мержу', tone: 'merge' };
    if (f.state === 'waiting') return { label: 'Ждёт вас', tone: 'wait' };
    if (f.state === 'merging') return { label: 'Идёт', tone: 'run' };
    // merged/closed — bucket выше; running/not_started/idle — «Идёт» (ветка ниже).
  }
  // Запасной путь: state в данных нет (кеш/моки) — прежний признак (attention).
  if (f.attention) {
    if (f.attention.kind === 'stopped') return { label: 'Стоит', tone: 'stop' };
    if (f.attention.kind === 'merge') return { label: 'К мержу', tone: 'merge' };
    return { label: 'Ждёт вас', tone: 'wait' };
  }
  const active = f.rail.find(s => s.state === 'active');
  if (active?.tone === 'loop') return { label: 'Доработка', tone: 'loop' };
  return { label: 'Идёт', tone: 'run' };
}

// Живые (идёт) и вставшие (упал) статусы чата: по ним чат выводится наружу из
// свёрнутой группы. «Живой» совпадает с «дышащими» статусами (RUNNING_STATUSES в
// chatTree); «вставший» — статусы ошибки/осиревшего.
const LIVE_STATUSES: ReadonlySet<Session['status']> = new Set(['starting', 'working', 'waiting']);
const STUCK_STATUSES: ReadonlySet<Session['status']> = new Set(['error', 'orphaned']);

// Раскладка чатов группы фичи:
//   ordered     — все чаты, ключевой первым (раскрытый вид);
//   exposed     — ключевой + живые + вставшие (выведены наружу из свёрнутой группы);
//   hiddenCount — «ещё N»: сколько чатов скрыто под свёрнутой группой.
export interface FeatureGroupChats {
  ordered: Session[];
  exposed: Session[];
  hiddenCount: number;
}

export function featureGroupChats(feature: FeatureIndexFeature, sessions: Session[]): FeatureGroupChats {
  const keyId = feature.keySessionId;
  // Ключевой чат — первым, остальные — в порядке группы (по дате, из groupByFeatures).
  // Стабильная сортировка: при равенстве (оба не ключевые) порядок группы сохраняем.
  const ordered = [...sessions].sort((a, b) =>
    (a.id === keyId ? 0 : 1) - (b.id === keyId ? 0 : 1));
  // Выведенные наружу: ключевой + живые + вставшие (без дублей, ключевой первым).
  const exposed: Session[] = [];
  const seen = new Set<string>();
  for (const s of ordered) {
    if (s.id === keyId || LIVE_STATUSES.has(s.status) || STUCK_STATUSES.has(s.status)) {
      if (!seen.has(s.id)) { seen.add(s.id); exposed.push(s); }
    }
  }
  return { ordered, exposed, hiddenCount: Math.max(0, sessions.length - exposed.length) };
}

// Дерево группы фичи (макет features-overview.html, .fg .kids): строки для рендера
// ChatTreeBranch. Строится по ЭФФЕКТИВНЫМ родителям (sessions — результат groupByFeatures:
// межфичевые рёбра обрезаны, через границу фичи ребёнок всплывает корнем — контракт С0,
// §3). Это и есть «своё дерево» группы: исполнители разных фич оркестратора не висят
// одним деревом, а расходятся по своим группам (C1).
//   exposedIds — какие чаты вывести (null = все): свёрнуто — ключевой + живые/вставшие;
//     скрытый «прокалывается» (buildChatTreeRows поднимает его видимых детей), как в
//     основном дереве. isVisible — фильтры чата (поиск/статус/персона + архивный срез).
//   keyId — ключевой чат: его поддерево (если он корень) переставляется в начало
//     (макет: ключевой первым).
// Чистая функция — тестируется без React (окружение node).
export function featureGroupTreeRows(
  sessions: Session[],
  keyId: string | null,
  exposedIds: Set<string> | null,
  opts: {
    isVisible: (c: Session) => boolean;
    collapsedIds: Set<string>;
    expandedIds?: Set<string>;
    activeId: string | null;
    sortOrder?: ChatSortOrder;
    bgWorkIds?: ReadonlySet<string>;
  },
): ChatTreeRowData[] {
  const visible = exposedIds
    ? (c: Session) => opts.isVisible(c) && exposedIds.has(c.id)
    : opts.isVisible;
  let rows = buildChatTreeRows(sessions, {
    isVisible: visible,
    collapsedIds: opts.collapsedIds,
    expandedIds: opts.expandedIds,
    activeId: opts.activeId,
    sortOrder: opts.sortOrder,
    bgWorkIds: opts.bgWorkIds,
  }).rows;
  // Ключевой чат первым: его поддерево (только если оно корень, depth 0) — в начало.
  // Иначе (вложенный) перестановка оторвёт его от родителя — оставляем как есть.
  if (keyId) {
    const idx = rows.findIndex(r => r.chat.id === keyId);
    if (idx > 0 && rows[idx].depth === 0) {
      const d = rows[idx].depth;
      let end = idx + 1;
      while (end < rows.length && rows[end].depth > d) end++;
      const subtree = rows.slice(idx, end);
      rows = [...subtree, ...rows.slice(0, idx), ...rows.slice(end)];
    }
  }
  return rows;
}

// Пункты группировки для тулбара: 'features' — только в списке проекта и при
// включённом флаге features-overview (у глобального списка реестра фич нет).
export function featureGroupByOptions(inProject: boolean, featuresOverview: boolean): ChatGroupBy[] {
  const base: ChatGroupBy[] = inProject ? ['days', 'tags', 'subjects'] : ['days'];
  if (inProject && featuresOverview) base.push('features');
  base.push('none');
  return base;
}

// === Чипы-фильтры фич (макет features-overview.html, .chips/.fch) ===
// Ряд чипов над списком в режиме «Фичи»: Открытые · Ждут вас · Стоят · К мержу ·
// Смержены · Закрытые (состав и порядок ряда — ALL_FEATURE_FILTER_CHIPS в chatFilters.ts). Принадлежность фичи к чипу НЕ вычисляет фронт — бэк отдаёт готовый
// chips[] (контракт «Контракт индекса фич — утверждён (Александр, 18.10)»); фронт
// только считает числа по chips[] и фильтрует список по выбранному чипу.

// Подписи чипов (только текст): порядок ряда задаёт ALL_FEATURE_FILTER_CHIPS, не этот объект.
// Экспортируется: секция «Фичи · что с ними» в FilterBar и строка снимаемых чипов
// в SessionList берут подписи отсюда, а не заводят вторую точку правды.
export const FEATURE_CHIP_LABEL: Record<FeatureChip, string> = {
  open: 'Открытые', waiting: 'Ждут вас', stopped: 'Стоят', merge: 'К мержу', closed: 'Закрытые',
  running: 'Идут', merged: 'Смержены',
};

// Число фич по каждому чипу: фронт считает по chips[] бэка (не вычисляет членство сам).
// Одна фича может входить в несколько чипов (например, «открытая» и «ждёт вас»).
export function featureChipCounts(features: FeatureIndexFeature[]): Record<FeatureChip, number> {
  const counts: Record<FeatureChip, number> = { open: 0, waiting: 0, stopped: 0, merge: 0, closed: 0, running: 0, merged: 0 };
  for (const f of features) for (const c of f.chips) counts[c]++;
  return counts;
}

// Закрытые фичи, которые РИСУЮТСЯ (имеют хотя бы один чат: chatCount > 0). ОДНО число
// для тумблера «Показывать закрытые фичи · N» (FilterBar) и заголовка секции
// «Закрытые · M» (SessionList) (m2): раньше тумблер считал ВСЕ закрытые (featureChipCounts),
// а секция — только нарисованные (closedGroups), и фича без чатов давала расхождение.
// Фича без чатов не рисуется → не считаем.
export function drawnClosedFeatures(features: FeatureIndexFeature[]): number {
  return features.filter(f => f.bucket === 'closed' && f.chatCount > 0).length;
}

// Карту фич по featureId строим ОДИН раз на объект индекса (WeakMap — живёт, пока
// живёт сам index). Без неё sharedFeatureTitles на каждую строку списка делала
// линейный .find по features[] для каждой ссылки чата (минор Глеба, 24.10).
const featureTitleIndex = new WeakMap<FeaturesIndex, Map<string, FeatureIndexFeature>>();
function titleIndex(index: FeaturesIndex): Map<string, FeatureIndexFeature> {
  let m = featureTitleIndex.get(index);
  if (!m) {
    m = new Map(index.features.map(f => [f.featureId, f]));
    featureTitleIndex.set(index, m);
  }
  return m;
}

// Названия ДРУГИХ известных фич чата (кроме текущей группы) — для пометки «общий с …»
// (решение Андрея 24.10, вариант 1): мультифичевый чат показывается в группе каждой
// своей фичы, а пометка называет остальные его группы. Висячие featureId (нет в
// features[]) не упоминаются — их названия неизвестны.
export function sharedFeatureTitles(chatId: string, groupId: string, index: FeaturesIndex): string[] {
  const links = index.chats[chatId];
  if (!links) return [];
  const map = titleIndex(index);
  const out: string[] = [];
  const seen = new Set<string>();
  for (const l of links) {
    if (l.featureId === groupId || seen.has(l.featureId)) continue;
    const f = map.get(l.featureId);
    if (!f) continue;
    seen.add(l.featureId);
    out.push(f.title);
  }
  return out;
}


// Ссылка «+N чат — в группе «X»» (F3.3) удалена: с 24.10 (решение Андрея, вариант 1)
// мультифичевый чат рисуется в группе каждой своей фичы напрямую, и «чужой» группы
// для него больше нет — ссылка дублировала бы сам чат.

// Роль чата в группе фичи (макет features-overview.html, .fg .kids): «ключевой»
// (ключевой чат фичи) и/или «общий с …» (чат показывается и в группах других
// своих фич — решение Андрея 24.10, вариант 1). Считается по id САМОЙ строки, а не
// замыканием корня: ChatTreeBranch рекурсивно передаёт renderCard потомкам, и роль
// корня лилась на всё поддерево (M3). undefined — метки нет (чат не ключевой и не
// мультифичевый).
// Метка ограничена по длине (минор Глеба, 24.10): называем ПЕРВУЮ другую фичу,
// остальные — счётчиком «· ещё N». Полный список на 360 px выталкивал имя чата
// из строки; сам чип дополнительно обрезан maxWidth+ellipsis (ChatCard).
export function featureRoleFor(
  sessionId: string, keyId: string | null, index: FeaturesIndex, groupId: string,
): string | undefined {
  const others = sharedFeatureTitles(sessionId, groupId, index);
  if (others.length === 0) return sessionId === keyId ? 'ключевой' : undefined;
  const shared = others.length === 1
    ? `общий с ${others[0]}`
    : `общий с ${others[0]} · ещё ${others.length - 1}`;
  return sessionId === keyId ? `ключевой · ${shared}` : shared;
}

// Подсказка у метки «ключевой» (волна 2, Ф3.2): что значит «ключевой чат» —
// единый источник текста для метки в группе (ChatCard) и в блоке «Чаты фичи»
// (FeaturePageBody): не два литерала, а одна константа.
export const KEY_SESSION_TOOLTIP = 'Здесь управляют фичей: запускают шаги и принимают результат';

// === Плашка группы фичи, волна 2 (макет features-overview-2.html, .ha) ===
// Чистые функции отображения: бэк считает bucket/attention/chips/keySessionId
// (контракт «Контракт индекса фич — утверждён (Александр, 18.10)»), фронт только
// раскладывает их в быстрое действие и стопку аватаров — не вычисляет сам.

// Быстрое действие плашки (макет .qa, «Общее: два действия у плашки»): «Ответить» /
// «К мержу» / «Ключевой чат». Чистая функция по attention.kind, chips, keySessionId:
//   closed (chip)          → null (действие живёт на странице фичи, не в списке);
//   attention.kind 'merge' → 'К мержу';
//   attention.kind 'wait'  → 'Ответить';
//   иначе (идёт/стоит)      → 'Ключевой чат' (только если ключевой чат найден), иначе null.
export function featureQuickAction(f: FeatureIndexFeature): string | null {
  if (f.chips.includes('closed')) return null;
  // Источник правды — f.state (бэк Б1a/Б1b): merge_pending → «К мержу», waiting → «Ответить»,
  // merging/running → «Ключевой чат». Без state (старые данные/кеш) — запасной путь
  // по attention.kind, чтобы существующие тесты не упали.
  let kind: 'merge' | 'wait' | null = null;
  if (f.state) {
    if (f.state === 'merge_pending') kind = 'merge';
    else if (f.state === 'waiting') kind = 'wait';
  } else if (f.attention) {
    if (f.attention.kind === 'merge') kind = 'merge';
    else if (f.attention.kind === 'wait') kind = 'wait';
  }
  if (kind === 'merge') return 'К мержу';
  if (kind === 'wait') return 'Ответить';
  return f.keySessionId ? 'Ключевой чат' : null;
}

// Стопка аватаров «кто сейчас» (макет .l2): по nowPersonaIds (волна 2, массив идущих
// персон, по убыванию старта); если массив пуст/отсутствует (старый ответ бэка) —
// фолбэк на now0 (одна InProgress-задача волны 1); если и там пусто (после сужения
// run.now у фичи, где все исполнители ждут человека — дефект 4a241bee) — фолбэк на
// attention.personaId (кто остановился/ждёт). Чистая функция. Правило «идёт» не
// трогается — это только выбор показа.
export function featureAvatarPersonaIds(f: FeatureIndexFeature): string[] {
  if (f.nowPersonaIds && f.nowPersonaIds.length > 0) return f.nowPersonaIds;
  if (f.now0?.personaId) return [f.now0.personaId];
  if (f.attention?.personaId) return [f.attention.personaId];
  return [];
}

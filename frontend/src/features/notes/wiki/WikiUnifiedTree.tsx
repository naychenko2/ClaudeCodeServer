// Единое дерево левой зоны «Заметки + Вики» (ADR-038, F2; макет notes-wiki-unified-v1, §1):
// в узле раздела вперемешку заметки и страницы вики, у каждой строки значок происхождения.
// Над деревом — фильтр «кто написал». Строка поиска живёт в шапке левой зоны (NotesPage):
// заметки приходят уже отфильтрованными серверным поиском, страницы вики фильтруются здесь
// по тексту того же запроса.
//
// Данные вики — каталоги разделов (GET /api/wiki/section). Дерево грузит их само, а устаревший
// ответ отбрасывает по ключу запроса: набор разделов поменялся (проект удалён, дерево
// перечитано), пока первый запрос был в пути, — его поздний ответ не должен затереть свежий.

import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { ChevronDown, ChevronRight, Folder, Globe, Layers, Lock, Search } from 'lucide-react';
import {
  C, FONT, FS, R, SP, useIsMobile, ensureWikiLoaded, loadWikiCatalog, groupSectionsByKind,
  wikiAuthorshipOf, WikiError,
  type WikiCatalogDto, type WikiPageInfoAuthoredDto, type WikiSectionDto,
} from 'aihome_shell/kit';
import type { NoteSummary } from '../../../types';
import { Button } from '../../../components/ui/Button';
import { Chip } from '../../../components/ui/Chip';
import { EmptyState } from '../../../components/ui/EmptyState';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import { OriginGlyph, type OriginKind } from './OriginGlyph';
import { PAGE_TYPE, wikiErrorText } from './wikiLabels';

// Строка дерева: 30 на десктопе, 44 на тач (макет .tr / .app.m .tr)
const ROW_H = 30;
const ROW_H_TOUCH = 44;
const SKELETON_ROWS = 9;
// Ширины полос скелетона — «разные», чтобы не читались как таблица
const SKELETON_W = [72, 55, 84, 63, 48, 77, 59, 68, 52];
// Узел для заметок, чей источник не совпал ни с одним разделом вики
const OTHER_KEY = '__other__';
const PERSONAL_KEY = 'personal';
// Служебные карточки источников лежат в папке «Источники» раздела: открываются сноской, в дереве не нужны
const SOURCE_CARD_DIR = 'Источники';
// Раздел сферы — место зарезервировано, запись в него отказана ('wiki_sphere_reserved', ADR-036 §7),
// наполнение — отдельная фича. В раскрытом пустом узле вместо строк — только это пояснение.
const SPHERE_EMPTY_HINT = 'Страниц пока нет — раздел сферы наполняется отдельной фичей';

// ─── Подгруппы узла раздела (3А) ───
// Внутри узла страницы раскладываются по подгруппам по типу каталога (WikiPageInfoDto.type —
// источник правды), порядок фиксированный списком ниже, не по алфавиту. Пустые подгруппы
// в раскладку не попадают. Счётчик подгруппы — число строк после фильтра и поиска.
// Свёртка подгруппы переживает перечитку: ключ localStorage — FOLDS_KEY, запись —
// «ключ раздела#вид».
const FOLDS_KEY = 'cc_wiki_tree_folds';

const NOTE_SUB = 'note';
// «Прочее» — types из каталога, не вошедшие в список выше, и страницы без типа
const OTHER_SUB = 'other';
// Порядок подгрупп внутри узла: решения → правила → термины → состояния → хроники →
// прочее → мои заметки. Подписи — из PAGE_TYPE (wikiLabels); «Прочее» и «Мои заметки» —
// здесь: единой метки для группы типов и для заметок в PAGE_TYPE нет.
const SUBGROUP_ORDER: { key: string; label: string }[] = [
  { key: 'decision', label: PAGE_TYPE.decision },
  { key: 'rule', label: PAGE_TYPE.rule },
  { key: 'term', label: PAGE_TYPE.term },
  { key: 'state', label: PAGE_TYPE.state },
  { key: 'chronicle', label: PAGE_TYPE.chronicle },
  { key: OTHER_SUB, label: 'Прочее' },
  { key: NOTE_SUB, label: 'Мои заметки' },
];

// Ключ свёртки: раздел + вид. '#'-суффикс не пересекается с ключом узла (тот его не несёт)
const foldKey = (sectionKey: string, subKey: string) => `${sectionKey}#${subKey}`;

function readFolds(): Set<string> {
  try {
    const raw = localStorage.getItem(FOLDS_KEY);
    const arr: unknown = raw ? JSON.parse(raw) : [];
    return new Set(Array.isArray(arr) ? arr.filter((k): k is string => typeof k === 'string') : []);
  } catch { return new Set(); } // приватный режим/переполнение — свёртка удобство, не данные
}

function writeFolds(folds: ReadonlySet<string>): void {
  try { localStorage.setItem(FOLDS_KEY, JSON.stringify([...folds])); } catch { /* та же причина */ }
}

export type OriginFilter = 'all' | 'note' | 'ai' | 'together' | 'me';

const FILTERS: { value: OriginFilter; label: string; glyph: OriginKind | null }[] = [
  { value: 'all', label: 'Все', glyph: null },
  { value: 'note', label: 'Заметки', glyph: 'note' },
  { value: 'ai', label: 'ИИ', glyph: 'ai' },
  { value: 'together', label: 'Вместе', glyph: 'mix' },
  { value: 'me', label: 'Мои', glyph: 'me' },
];

export interface UnifiedItem {
  kind: 'note' | 'page';
  /** Заметка — её id; страница — pageKey */
  id: string;
  sectionKey: string;
  title: string;
  origin: OriginFilter;
  /** Тип страницы из каталога (source of truth по видам); у заметки — null */
  type: string | null;
}

export interface UnifiedSubgroup {
  /** Ключ подгруппы для свёртки: `${ключ раздела}#${вид}` */
  key: string;
  sectionKey: string;
  /** Вид: тип каталога, 'other' (прочие/без типа) или 'note' (мои заметки) */
  typeKey: string;
  label: string;
  items: UnifiedItem[];
}

export interface UnifiedGroup {
  key: string;
  label: string;
  /** Тип узла — иконка и порядок: проекты, сферы, общее, личное, прочие */
  kind: 'project' | 'sphere' | 'common' | 'personal' | 'other';
  orphan: boolean;
  /** Строки узла в порядке подгрупп — общий вид для счётчиков и порядка строк */
  items: UnifiedItem[];
  /** Подгруппы непустые, в фиксированном порядке */
  subgroups: UnifiedSubgroup[];
}

const GLYPH_OF: Record<Exclude<OriginFilter, 'all'>, OriginKind> = {
  note: 'note', ai: 'ai', together: 'mix', me: 'me',
};

// Текст запроса без операторов заметок (tag:, status:, source:) — по ним страницы вики не ищем
export function queryText(query: string): string {
  return query.split(/\s+/).filter(p => p && !/^(tag|status|source):/i.test(p)).join(' ').toLowerCase();
}

// Раздел заметки: личные — «Личное», проектные — раздел проекта; иначе узла нет (прочие)
export function noteSectionKey(note: NoteSummary, sections: readonly WikiSectionDto[]): string | null {
  if (note.source === 'personal') return PERSONAL_KEY;
  const key = `projects/${note.source}`;
  return sections.some(s => s.key === key) ? key : null;
}

// Служебная карточка источника: тип из шапки страницы или сегмент «Источники» в полном pageKey
// (на бэке ключ несёт папку раздела: «Проекты/{id}/Источники/Имя-abc12345.md»)
const isSourceCard = (p: WikiPageInfoAuthoredDto) =>
  p.type === 'source' || p.pageKey.replace(/\\/g, '/').split('/').includes(SOURCE_CARD_DIR);

/** Строки дерева: до фильтра по происхождению (по нему считаются счётчики чипов) */
export function buildUnifiedGroups(
  sections: readonly WikiSectionDto[],
  notes: readonly NoteSummary[],
  catalogs: Readonly<Record<string, WikiCatalogDto>>,
  query: string,
): UnifiedGroup[] {
  const text = queryText(query);
  const by = groupSectionsByKind(sections as WikiSectionDto[]);
  const groups = new Map<string, UnifiedGroup>();
  const addAll = (list: readonly WikiSectionDto[], kind: UnifiedGroup['kind'], label: (s: WikiSectionDto) => string) => {
    for (const s of list) {
      if (s.local) continue;
      groups.set(s.key, { key: s.key, orphan: s.orphan, kind, label: label(s), items: [], subgroups: [] });
    }
  };
  // Порядок узлов: проекты → сферы → «Общее» → «Личное» (решение Андрея 04.11); «Другие заметки» — после них.
  // Сфера — узел-раздел с живым именем (у сироты имени нет — заместитель, как в WikiSectionTree).
  // Заметки в узел сферы не раскладываются: noteSectionKey знает только проектные и личные разделы.
  addAll(by.project, 'project', s => s.name ?? 'Удалённый проект');
  addAll(by.sphere, 'sphere', s => s.name ?? 'Сфера');
  addAll(by.common, 'common', () => 'Общее');
  addAll(by.personal, 'personal', () => 'Личное');
  const other: UnifiedGroup = { key: OTHER_KEY, label: 'Другие заметки', kind: 'other', orphan: false, items: [], subgroups: [] };

  const itemsOf = new Map<string, UnifiedItem[]>();
  const push = (g: UnifiedGroup, item: UnifiedItem) => {
    let list = itemsOf.get(g.key);
    if (!list) { list = []; itemsOf.set(g.key, list); }
    list.push(item);
  };

  for (const g of groups.values()) {
    for (const p of (catalogs[g.key]?.pages ?? []) as WikiPageInfoAuthoredDto[]) {
      if (isSourceCard(p)) continue;
      if (text && !`${p.title} ${p.summary ?? ''}`.toLowerCase().includes(text)) continue;
      const a = wikiAuthorshipOf(p);
      push(g, {
        kind: 'page', id: p.pageKey, sectionKey: g.key, title: p.title,
        origin: a === 'human' ? 'me' : a, type: p.type ?? null,
      });
    }
  }
  // Один объект — один ряд: вики-страницы бэк отдаёт и заметками (`source === 'wiki'`, путь = pageKey),
  // они уже в дереве из каталогов разделов — второй раз не показываем.
  for (const n of notes) {
    if (n.source === 'wiki') continue;
    const key = noteSectionKey(n, sections);
    const target = (key && groups.get(key)) || other;
    push(target, { kind: 'note', id: n.id, sectionKey: target.key, title: n.title, origin: 'note', type: null });
  }
  for (const g of groups.values()) {
    const items = itemsOf.get(g.key);
    if (items?.length) splitSubgroups(g, items);
  }
  const otherItems = itemsOf.get(OTHER_KEY);
  if (otherItems?.length) splitSubgroups(other, otherItems);
  const out = [...groups.values()];
  if (other.items.length) out.push(other);
  return out;
}

// Раскладка строк узла по подгруппам фиксированного порядка: тип из списка → своя
// подгруппа; остальные типы и страницы без типа → «Прочее»; заметки → «Мои заметки».
// Пустые подгруппы не создаются; items узла — строки в порядке подгрупп
// (общий вид для счётчиков и порядка строк).
export function splitSubgroups(g: UnifiedGroup, items: readonly UnifiedItem[]): void {
  const byType = new Map<string, UnifiedItem[]>();
  for (const it of items) {
    const key = it.kind === 'note' ? NOTE_SUB : (it.type && SUBGROUP_ORDER.some(s => s.key === it.type) ? it.type : OTHER_SUB);
    const list = byType.get(key);
    if (list) list.push(it); else byType.set(key, [it]);
  }
  const subs: UnifiedSubgroup[] = [];
  for (const s of SUBGROUP_ORDER) {
    const list = byType.get(s.key);
    if (!list?.length) continue;
    subs.push({ key: foldKey(g.key, s.key), sectionKey: g.key, typeKey: s.key, label: s.label, items: list });
  }
  g.subgroups = subs;
  g.items = subs.flatMap(s => s.items);
}

export function countByOrigin(groups: readonly UnifiedGroup[]): Record<OriginFilter, number> {
  const c: Record<OriginFilter, number> = { all: 0, note: 0, ai: 0, together: 0, me: 0 };
  for (const g of groups) for (const i of g.items) { c.all++; c[i.origin]++; }
  return c;
}

export function filterGroups(groups: readonly UnifiedGroup[], filter: OriginFilter): UnifiedGroup[] {
  if (filter === 'all') return groups.map(g => ({ ...g }));
  // Подгруппы пересобираются из отфильтрованных строк: счётчик подгруппы — число после
  // фильтра; подгруппа, ставшая пустой, исчезает
  return groups
    .map(g => {
      const next: UnifiedGroup = { ...g, items: [], subgroups: [] };
      splitSubgroups(next, g.items.filter(i => i.origin === filter));
      return next;
    })
    .filter(g => g.items.length > 0);
}

// ─── Загрузка каталогов ───

interface CatalogsState {
  catalogs: Record<string, WikiCatalogDto>;
  /** Идёт первая загрузка: данных ещё нет */
  loading: boolean;
  error: unknown;
}

// Каталоги нужны всем разделам со страницами: от них зависят и узлы, и счётчики чипов.
// Ключ запроса — набор разделов плюс номер поколения: ответ, чей ключ уже не последний
// (набор поменялся или дерево перечитали), в состояние не попадает.
function useSectionCatalogs(sections: readonly WikiSectionDto[]): CatalogsState & { reload: () => void } {
  const [state, setState] = useState<CatalogsState>({ catalogs: {}, loading: true, error: null });
  const [retry, setRetry] = useState(0);
  const latest = useRef('');
  const gen = useRef(0);
  const keys = useMemo(
    () => sections.filter(s => !s.local && !s.sphereReserved && s.pageCount > 0).map(s => s.key).sort(),
    [sections],
  );

  useEffect(() => {
    const token = `${keys.join('|')}#${++gen.current}`;
    latest.current = token;
    if (!keys.length) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- разделов со страницами нет: загрузка закончена, ждать нечего
      setState({ catalogs: {}, loading: false, error: null });
      return;
    }
    void Promise.allSettled(keys.map(k => loadWikiCatalog(k))).then(results => {
      if (latest.current !== token) return;
      const catalogs: Record<string, WikiCatalogDto> = {};
      let error: unknown = null;
      results.forEach((r, i) => {
        if (r.status === 'rejected') error ??= r.reason; else if (r.value) catalogs[keys[i]] = r.value;
      });
      setState({ catalogs, loading: false, error });
    });
  }, [keys, retry]);

  return { ...state, reload: () => setRetry(n => n + 1) };
}

// ─── Компонент ───

export interface WikiUnifiedTreeProps {
  sections: readonly WikiSectionDto[];
  notes: readonly NoteSummary[];
  /** Запрос поиска из шапки левой зоны */
  query: string;
  selectedNoteId: string | null;
  selectedPage: { section: string; page: string } | null;
  /** sectionKey — ключ узла, где лежит заметка; для «Других заметок» (OTHER_KEY) — null:
   * чип раздела при выборе такой заметки не меняется. Второй аргумент необязателен для
   * вызывающего: существующие одноаргументные обработчики остаются валидны. */
  onSelectNote: (id: string, sectionKey: string | null) => void;
  onOpenPage: (section: string, page: string) => void;
}

export function WikiUnifiedTree({
  sections, notes, query, selectedNoteId, selectedPage, onSelectNote, onOpenPage,
}: WikiUnifiedTreeProps) {
  const isMobile = useIsMobile();
  const [filter, setFilter] = useState<OriginFilter>('all');
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());
  // Свёртка подгрупп переживает перечитку: читаем хранилище при монтировании (один раз,
  // синхронно — localStorage), а не в эффекте
  const [folded, setFolded] = useState<ReadonlySet<string>>(readFolds);
  const [hovered, setHovered] = useState<string | null>(null);
  const { catalogs, loading, error, reload } = useSectionCatalogs(sections);

  const all = useMemo(() => buildUnifiedGroups(sections, notes, catalogs, query), [sections, notes, catalogs, query]);
  const counts = useMemo(() => countByOrigin(all), [all]);
  const groups = useMemo(() => filterGroups(all, filter), [all, filter]);
  const searching = filter !== 'all' || queryText(query) !== '';
  const rowH = isMobile ? ROW_H_TOUCH : ROW_H;

  const toggle = (key: string) => setCollapsed(prev => {
    const next = new Set(prev);
    if (!next.delete(key)) next.add(key);
    return next;
  });

  // Сворачиваем подгруппу — запись сразу в хранилище (своего ключа), состояние — за ним
  const toggleSub = useCallback((key: string) => {
    setFolded(prev => {
      const next = new Set(prev);
      if (!next.delete(key)) next.add(key);
      writeFolds(next);
      return next;
    });
  }, []);

  const rowStyle = (selected: boolean, hover: boolean): CSSProperties => ({
    display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', textAlign: 'left', border: 'none',
    borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`, paddingLeft: SP.xl, minHeight: rowH,
    fontFamily: FONT.sans, fontSize: FS.base, cursor: 'pointer',
    fontWeight: selected ? 600 : 400, color: selected ? C.textHeading : C.textPrimary,
    background: selected ? C.bgSelected : hover ? C.bgCard : 'transparent',
  });

  const itemRow = (item: UnifiedItem) => {
    const id = `${item.kind}:${item.sectionKey}:${item.id}`;
    const selected = item.kind === 'note'
      ? selectedNoteId === item.id
      : selectedPage?.section === item.sectionKey && selectedPage.page === item.id;
    return (
      <button
        key={id} type="button" data-testid="unified-row" data-kind={item.kind} data-origin={item.origin}
        title={item.title} aria-current={selected ? 'true' : undefined}
        style={rowStyle(selected, !isMobile && hovered === id)}
        onMouseEnter={() => !isMobile && setHovered(id)}
        onMouseLeave={() => setHovered(h => (h === id ? null : h))}
        onClick={() => (item.kind === 'note'
          ? onSelectNote(item.id, item.sectionKey === OTHER_KEY ? null : item.sectionKey)
          : onOpenPage(item.sectionKey, item.id))}
      >
        <OriginGlyph kind={item.kind === 'note' ? 'note' : GLYPH_OF[item.origin as Exclude<OriginFilter, 'all'>]} />
        <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{item.title}</span>
      </button>
    );
  };

  const groupIcon = (g: UnifiedGroup) => {
    const p = { size: ICON_SIZE.xs, strokeWidth: ICON_STROKE, style: { flexShrink: 0, color: C.textMuted } };
    // Своей иконки сфера в DTO нет (её значок из SphereTile — выбор пользователя, в раздел не едет);
    // Layers — стопка слоёв, читается как группа проектов
    if (g.kind === 'sphere') return <Layers {...p} />;
    return g.kind === 'common' ? <Globe {...p} /> : g.kind === 'personal' ? <Lock {...p} /> : <Folder {...p} />;
  };

  // Подгруппа узла: заголовок со счётчиком (число после фильтра/поиска) и свёрткой;
  // строки — с отступом на уровень глубже. Поиск/фильтр раскрывают найденное — как узел.
  const subgroupNode = (sub: UnifiedSubgroup) => {
    const open = searching || !folded.has(sub.key);
    return (
      <Fragment key={sub.key}>
        <button
          type="button" data-testid="unified-subgroup" data-section={sub.sectionKey} data-type={sub.typeKey} aria-expanded={open}
          onClick={() => toggleSub(sub.key)}
          style={{
            display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%', textAlign: 'left', border: 'none',
            background: 'transparent', minHeight: rowH, padding: `${SP.xs}px ${SP.sm}px`, paddingLeft: SP.lg,
            cursor: 'pointer', fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 500, color: C.textSecondary,
          }}
        >
          {open
            ? <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />
            : <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />}
          <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{sub.label}</span>
          <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 400, color: C.textMuted }}>
            {sub.items.length}
          </span>
        </button>
        {open && sub.items.map(itemRow)}
      </Fragment>
    );
  };

  const groupNode = (g: UnifiedGroup) => {
    // Поиск или фильтр раскрывают найденное: свёрнутый узел спрятал бы результат
    const open = searching || !collapsed.has(g.key);
    return (
      <Fragment key={g.key}>
        <button
          type="button" data-testid="unified-group" aria-expanded={open}
          onClick={() => toggle(g.key)}
          style={{
            display: 'flex', alignItems: 'center', gap: SP.xs, width: '100%', textAlign: 'left', border: 'none',
            background: 'transparent', minHeight: rowH, padding: `${SP.xs}px ${SP.sm}px`, cursor: 'pointer',
            fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: g.orphan ? C.textMuted : C.textSecondary,
          }}
        >
          {open
            ? <ChevronDown size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />
            : <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ color: C.textMuted, flexShrink: 0 }} />}
          {groupIcon(g)}
          <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{g.label}</span>
          <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 400, color: C.textMuted }}>
            {g.items.length}
          </span>
        </button>
        {open && g.subgroups.map(subgroupNode)}
        {open && g.kind === 'sphere' && !g.items.length && (
          <div
            data-testid="unified-sphere-hint"
            style={{ padding: `${SP.xs}px ${SP.sm}px`, paddingLeft: SP.xl, fontSize: FS.sm, color: C.textMuted }}
          >
            {SPHERE_EMPTY_HINT}
          </div>
        )}
      </Fragment>
    );
  };

  const body = () => {
    if (loading && !all.some(g => g.items.length)) {
      return (
        <div data-testid="unified-skeleton" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, padding: SP.sm }}>
          {SKELETON_W.slice(0, SKELETON_ROWS).map((w, i) => (
            <div key={i} style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minHeight: rowH }}>
              <span style={{ width: 18, height: 18, borderRadius: R.sm, background: C.bgSelected, flexShrink: 0 }} />
              <span style={{ width: `${w}%`, height: 10, borderRadius: R.sm, background: C.bgSelected }} />
            </div>
          ))}
        </div>
      );
    }
    // Узел сферы — тоже содержимое дерева: он не должен прятаться за общей подсказкой
    if (!all.some(g => g.items.length || g.kind === 'sphere') && !searching) {
      return (
        <div data-testid="unified-empty" style={{ padding: SP.md, fontSize: FS.sm, color: C.textMuted }}>
          Здесь будут ваши заметки и страницы, которые ведёт ИИ.
        </div>
      );
    }
    if (!groups.length) {
      return (
        <EmptyState
          compact inline icon={<Search size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title="Ничего не нашлось"
          subtitle={queryText(query)
            ? `Среди ваших записей нет «${queryText(query)}». Сбросьте фильтр — ИИ мог записать это у себя.`
            : 'С таким происхождением записей нет. Сбросьте фильтр.'}
          action={filter !== 'all'
            ? <Button variant="secondary" size="sm" dataTestId="unified-reset-filter" onClick={() => setFilter('all')}>Показать все</Button>
            : undefined}
        />
      );
    }
    return groups.map(groupNode);
  };

  return (
    <div data-testid="wiki-unified-tree" style={{ display: 'flex', flexDirection: 'column', minHeight: 0 }}>
      <div
        data-testid="unified-filter" role="group" aria-label="Кто написал"
        style={{
          display: 'flex', gap: SP.xs, padding: `${SP.sm}px ${SP.sm}px`, overflowX: 'auto', flexShrink: 0,
          borderBottom: `1px solid ${C.border}`,
        }}
      >
        {FILTERS.map(f => (
          <Chip
            key={f.value} variant="toggle" selected={filter === f.value} large={isMobile}
            // тач-цель на мобиле — не ниже 40 px (палец); на десктопе высота по контенту
            minHeight={isMobile ? 40 : undefined}
            leading={f.glyph ? <OriginGlyph kind={f.glyph} /> : undefined}
            onClick={() => setFilter(f.value)}
          >
            {`${f.label} ${counts[f.value]}`}
          </Chip>
        ))}
      </div>
      {error != null && (
        <div data-testid="unified-error" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, padding: SP.sm, fontSize: FS.sm, color: C.dangerText }}>
          <span style={{ flex: 1, minWidth: 0 }}>{error instanceof WikiError ? wikiErrorText(error) : 'Не удалось загрузить вики'}</span>
          <Button variant="ghost" size="sm" dataTestId="unified-retry" onClick={reload}>Повторить</Button>
        </div>
      )}
      <div style={{ flex: 1, minHeight: 0 }}>{body()}</div>
      <div
        data-testid="unified-legend"
        style={{
          display: 'flex', flexWrap: 'wrap', gap: SP.sm, padding: SP.sm, fontSize: FS.xs, color: C.textMuted,
          borderTop: `1px solid ${C.border}`,
        }}
      >
        {([['note', 'заметка'], ['ai', 'ИИ'], ['mix', 'вместе'], ['me', 'вы']] as const).map(([k, t]) => (
          <span key={k} style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs }}>
            <OriginGlyph kind={k} />{t}
          </span>
        ))}
      </div>
    </div>
  );
}

// Загрузка дерева вики — отдельным вызовом для вызывающего: левая зона показывает дерево и
// тогда, когда WikiTab (центр) ещё не смонтирован
export function useEnsureWikiTree(enabled: boolean): void {
  useEffect(() => {
    if (enabled) void ensureWikiLoaded().catch(() => undefined);
  }, [enabled]);
}

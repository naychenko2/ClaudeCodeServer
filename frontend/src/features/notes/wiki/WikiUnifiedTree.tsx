// Единое дерево левой зоны «Заметки + Вики» (ADR-038, F2; макет notes-wiki-unified-v1, §1):
// в узле раздела вперемешку заметки и страницы вики, у каждой строки значок происхождения.
// Над деревом — фильтр «кто написал». Строка поиска живёт в шапке левой зоны (NotesPage):
// заметки приходят уже отфильтрованными серверным поиском, страницы вики фильтруются здесь
// по тексту того же запроса.
//
// Данные вики — каталоги разделов (GET /api/wiki/section). Дерево грузит их само, а устаревший
// ответ отбрасывает по ключу запроса: набор разделов поменялся (проект удалён, дерево
// перечитано), пока первый запрос был в пути, — его поздний ответ не должен затереть свежий.

import { Fragment, useEffect, useMemo, useRef, useState } from 'react';
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
import { wikiErrorText } from './wikiLabels';

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
}

export interface UnifiedGroup {
  key: string;
  label: string;
  /** Тип узла — иконка и порядок: проекты, сферы, общее, личное, прочие */
  kind: 'project' | 'sphere' | 'common' | 'personal' | 'other';
  orphan: boolean;
  items: UnifiedItem[];
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
      groups.set(s.key, { key: s.key, orphan: s.orphan, kind, label: label(s), items: [] });
    }
  };
  // Порядок узлов: проекты → сферы → «Общее» → «Личное» (решение Андрея 04.11); «Другие заметки» — после них.
  // Сфера — узел-раздел с живым именем (у сироты имени нет — заместитель, как в WikiSectionTree).
  // Заметки в узел сферы не раскладываются: noteSectionKey знает только проектные и личные разделы.
  addAll(by.project, 'project', s => s.name ?? 'Удалённый проект');
  addAll(by.sphere, 'sphere', s => s.name ?? 'Сфера');
  addAll(by.common, 'common', () => 'Общее');
  addAll(by.personal, 'personal', () => 'Личное');
  const other: UnifiedGroup = { key: OTHER_KEY, label: 'Другие заметки', kind: 'other', orphan: false, items: [] };

  for (const g of groups.values()) {
    for (const p of (catalogs[g.key]?.pages ?? []) as WikiPageInfoAuthoredDto[]) {
      if (isSourceCard(p)) continue;
      if (text && !`${p.title} ${p.summary ?? ''}`.toLowerCase().includes(text)) continue;
      const a = wikiAuthorshipOf(p);
      g.items.push({
        kind: 'page', id: p.pageKey, sectionKey: g.key, title: p.title,
        origin: a === 'human' ? 'me' : a,
      });
    }
  }
  // Один объект — один ряд: вики-страницы бэк отдаёт и заметками (`source === 'wiki'`, путь = pageKey),
  // они уже в дереве из каталогов разделов — второй раз не показываем.
  for (const n of notes) {
    if (n.source === 'wiki') continue;
    const key = noteSectionKey(n, sections);
    const target = (key && groups.get(key)) || other;
    target.items.push({ kind: 'note', id: n.id, sectionKey: target.key, title: n.title, origin: 'note' });
  }
  const out = [...groups.values()];
  if (other.items.length) out.push(other);
  return out;
}

export function countByOrigin(groups: readonly UnifiedGroup[]): Record<OriginFilter, number> {
  const c: Record<OriginFilter, number> = { all: 0, note: 0, ai: 0, together: 0, me: 0 };
  for (const g of groups) for (const i of g.items) { c.all++; c[i.origin]++; }
  return c;
}

export function filterGroups(groups: readonly UnifiedGroup[], filter: OriginFilter): UnifiedGroup[] {
  if (filter === 'all') return groups.map(g => ({ ...g }));
  return groups
    .map(g => ({ ...g, items: g.items.filter(i => i.origin === filter) }))
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
        {open && g.items.map(itemRow)}
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

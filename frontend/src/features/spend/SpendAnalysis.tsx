// Экран «Анализ»: pivot-цепочка уровней (порядок собирает пользователь), фильтры-чипы,
// ленивое дерево (/api/spend/pivot по уровню, листья-ходы — /api/spend/turns),
// панель деталей узла (свод /overview по фильтрам узла) и паспорт хода (/turns/{id}).
// На мобиле панель деталей — bottom-sheet по tap на узел.
import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import type {
  SpendOverviewResponse, SpendPivotNode, SpendTaskPromptRun, SpendTurnDetailResponse,
  SpendTurnDto, SpendTurnsResponse,
} from '../../types';
import { api } from '../../lib/api';
import { C, FONT, R, SHADOW, SP, Z } from '../../lib/design';
import {
  ADMIN_ONLY_DIMS, DIM_LABELS, SPEND_PRESETS, fmtDate, fmtTok, fmtTime, isGenSource, nodeName,
  sourceColor, sourceLabel, sourceTextColor, spendQuery,
  type SpendDim, type SpendFilter, type SpendLevel,
} from '../../lib/spend';
import type { SpendEmptyKind, SpendState } from './SpendScreen';
import {
  Chip, ChipX, Dot, DropMenu, EmptyBody, GhostBtn, HBar, LoadError, MenuItem, Skel, nodeIcon,
} from './spendUi';

const TURNS_PAGE = 30;

// Чанк загруженного уровня дерева: узлы разреза или листья-ходы
type Chunk =
  | { state: 'loading' }
  | { state: 'error' }
  | { state: 'nodes'; nodes: SpendPivotNode[] }
  | { state: 'turns'; data: SpendTurnsResponse };

// Выбранный узел дерева с контекстом пути (для панели деталей)
interface SelNode {
  key: string;                    // полный путь 'dim:val|dim:val'
  dim: SpendDim;
  name: string;
  node: SpendPivotNode;
  filters: SpendFilter[];         // фильтры пути: предки + сам узел
  path: string;                   // читаемая цепочка предков «AI Home › Макеты»
  parentTotal: number;
}

interface Props {
  st: SpendState;
  patch: (p: Partial<SpendState>) => void;
  range: { from: string; to: string };
  showUsers: boolean;
  isMobile: boolean;
  isTablet?: boolean;                       // 601–1199: вертикальный стек вместо двух колонок
  emptyKind: SpendEmptyKind;                // чем объяснять пустоту: срез / период / трат не было вообще
  overview: SpendOverviewResponse | null;   // свод текущего среза (для «Итого» и бейджа окна)
  overviewError: boolean;
  onRetryOverview: () => void;
  onCloseScreen: () => void;
}

export function SpendAnalysis({ st, patch, range, showUsers, isMobile, isTablet = false, emptyKind, overview, overviewError, onRetryOverview, onCloseScreen }: Props) {
  // Планшет: экран растянут на вьюпорт, значит панелям деталей потолок высоты не нужен —
  // они тянутся сами (цепочка определённой высоты описана в SpendScreen)
  const fill = isTablet;
  // Действующая цепочка уровней: кастомная или из пресета по роли
  const levels = useMemo<SpendLevel[]>(() => {
    if (st.levels) return st.levels;
    const p = SPEND_PRESETS.find(x => x.key === st.preset) ?? SPEND_PRESETS[0];
    return showUsers ? p.admin : p.user;
  }, [st.levels, st.preset, showUsers]);

  const [chunks, setChunks] = useState<Record<string, Chunk>>({});
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [selNode, setSelNode] = useState<SelNode | null>(null);
  const [menu, setMenu] = useState<string | null>(null);            // 'lvl' | 'filter' | 'fvals:{dim}'
  const [fvals, setFvals] = useState<SpendPivotNode[] | null>(null); // значения для подменю фильтра
  const [sheet, setSheet] = useState(false);
  const [treeTick, setTreeTick] = useState(0);

  // Подпись контекста запроса — общая для всех загрузок дерева
  const baseQuery = useCallback((extraFilters: SpendFilter[], extra?: Record<string, string | number | undefined>) =>
    spendQuery({ from: range.from, to: range.to, scope: st.scope, filters: [...st.filters, ...extraFilters], extra }),
  [range.from, range.to, st.scope, st.filters]);

  const loadChunk = useCallback((key: string, ancestors: SpendFilter[], levelIdx: number) => {
    setChunks(prev => ({ ...prev, [key]: { state: 'loading' } }));
    const fail = () => setChunks(prev => ({ ...prev, [key]: { state: 'error' } }));
    if (levels[levelIdx] === 'turn') {
      api.spend.turns(baseQuery(ancestors, { limit: TURNS_PAGE, sort: 'time' }))
        .then(d => setChunks(prev => ({ ...prev, [key]: { state: 'turns', data: d } })))
        .catch(fail);
    } else {
      api.spend.pivot(baseQuery(ancestors, { groupBy: levels[levelIdx] }))
        .then(d => setChunks(prev => ({ ...prev, [key]: { state: 'nodes', nodes: d.nodes } })))
        .catch(fail);
    }
  }, [levels, baseQuery]);

  // Дозагрузка ходов терминального уровня («… ещё N ходов»)
  const loadMoreTurns = useCallback((key: string, ancestors: SpendFilter[], loaded: number) => {
    api.spend.turns(baseQuery(ancestors, { limit: TURNS_PAGE, offset: loaded, sort: 'time' }))
      .then(d => setChunks(prev => {
        const cur = prev[key];
        if (cur?.state !== 'turns') return prev;
        return { ...prev, [key]: { state: 'turns', data: { ...d, items: [...cur.data.items, ...d.items] } } };
      }))
      .catch(() => {});
  }, [baseQuery]);

  // Смена среза/уровней — дерево строится заново
  const levelsKey = levels.join('>');
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- перестройка сводного дерева при смене среза/фильтров
    setChunks({});
    setExpanded(new Set());
    setSelNode(null);
    setFvals(null);
    if (levels.length > 0) loadChunk('', [], 0);
    // loadChunk уже зависит от levels/baseQuery — перечисленных ниже ключей достаточно
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [levelsKey, range.from, range.to, st.scope, st.filters, treeTick]);

  // Значения для подменю «+ фильтр → разрез»
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- сброс значений подфильтра при смене меню
    if (!menu?.startsWith('fvals:')) { setFvals(null); return; }
    const dim = menu.slice(6) as SpendDim;
    let cancelled = false;
    setFvals(null);
    api.spend.pivot(baseQuery([], { groupBy: dim }))
      .then(d => { if (!cancelled) setFvals(d.nodes.slice(0, 7)); })
      .catch(() => { if (!cancelled) setFvals([]); });
    return () => { cancelled = true; };
  }, [menu, baseQuery]);

  // ---- Паспорт хода (правая панель / шторка) ----
  const selTurnId = st.selKey?.startsWith('turn:') ? st.selKey.slice(5) : null;
  const [turnDetail, setTurnDetail] = useState<SpendTurnDetailResponse | null | 'error'>(null);
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- скелетон вместо старого хода при смене выбора
    if (!selTurnId) { setTurnDetail(null); return; }
    let cancelled = false;
    setTurnDetail(null);
    api.spend.turn(selTurnId)
      .then(d => { if (!cancelled) setTurnDetail(d); })
      .catch(() => { if (!cancelled) setTurnDetail('error'); });
    return () => { cancelled = true; };
  }, [selTurnId]);

  // ---- Свод выбранного узла (панель деталей) ----
  const [nodeOv, setNodeOv] = useState<SpendOverviewResponse | null | 'error'>(null);
  useEffect(() => {
    // eslint-disable-next-line react-hooks/set-state-in-effect -- скелетон вместо старого обзора узла при смене выбора
    if (!selNode) { setNodeOv(null); return; }
    let cancelled = false;
    setNodeOv(null);
    api.spend.overview(baseQuery(selNode.filters))
      .then(d => { if (!cancelled) setNodeOv(d); })
      .catch(() => { if (!cancelled) setNodeOv('error'); });
    return () => { cancelled = true; };
  }, [selNode, baseQuery]);

  const selectTurn = (t: SpendTurnDto) => {
    patch({ selKey: `turn:${t.id}` });
    setSelNode(null);
    if (isMobile) setSheet(true);
  };
  const clickNode = (sel: SelNode, hasKids: boolean, childKey: string, childIdx: number) => {
    patch({ selKey: sel.key });
    setSelNode(sel);
    if (hasKids) {
      setExpanded(prev => {
        const next = new Set(prev);
        if (next.has(sel.key)) next.delete(sel.key);
        else {
          next.add(sel.key);
          if (!chunks[childKey]) loadChunk(childKey, sel.filters, childIdx);
        }
        return next;
      });
    }
    if (isMobile) setSheet(true);
  };

  // ================= Pivot-бар =================
  const availableDims = (Object.keys(DIM_LABELS) as SpendDim[])
    .filter(d => !levels.includes(d) && (showUsers || !ADMIN_ONLY_DIMS.includes(d)));
  const removeLevel = (d: SpendLevel) => {
    const next = levels.filter(x => x !== d);
    patch({ levels: next, selKey: null });
    setSelNode(null);
  };
  const addLevel = (d: SpendLevel) => {
    // «Ход» — только последним; остальные уровни встают перед ним
    const next = d === 'turn'
      ? [...levels, 'turn' as SpendLevel]
      : levels.includes('turn')
        ? [...levels.slice(0, -1), d, 'turn' as SpendLevel]
        : [...levels, d];
    patch({ levels: next, selKey: null });
    setSelNode(null);
    setMenu(null);
  };
  const detailDays = overview?.detailDays;

  const pivotBar = (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 6, padding: '9px 14px',
      borderBottom: `1px solid ${C.borderLight}`, background: C.bgPanel,
      flexWrap: isMobile ? 'nowrap' : 'wrap', overflowX: isMobile ? 'auto' : undefined, scrollbarWidth: 'none',
    }}>
      <span style={{ fontSize: 11, color: C.textMuted, flexShrink: 0, fontFamily: FONT.sans }}>Уровни:</span>
      {levels.map((d, i) => (
        <span key={d} style={{ display: 'inline-flex', alignItems: 'center', gap: 6, flexShrink: 0 }}>
          {i > 0 && <span style={{ color: C.textMuted, fontSize: 11 }}>›</span>}
          {d === 'turn' ? (
            <span style={{
              display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 11, fontWeight: 600,
              padding: '4px 8px', borderRadius: R.pill, whiteSpace: 'nowrap', fontFamily: FONT.sans,
              border: `1px dashed ${C.info}`, color: C.info, background: C.infoBg,
            }}>
              Ход{detailDays ? ` · в окне ${detailDays} дней` : ''}
              <ChipX touch={isTablet} onClick={() => removeLevel('turn')} />
            </span>
          ) : (
            <span style={{
              display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 11, fontWeight: 600,
              padding: '4px 8px', borderRadius: R.pill, whiteSpace: 'nowrap', fontFamily: FONT.sans,
              border: `1px solid ${C.border}`, background: C.bgCard, color: C.textHeading,
            }}>
              {DIM_LABELS[d]}
              {d === 'user' && (
                <span style={{ fontSize: 9, fontWeight: 600, padding: '1px 5px', borderRadius: 5, background: C.warningBg, color: C.warningText }}>
                  админ
                </span>
              )}
              <ChipX touch={isTablet} onClick={() => removeLevel(d)} />
            </span>
          )}
        </span>
      ))}
      <span style={{ position: 'relative', display: 'inline-flex', flexShrink: 0 }}>
        <Chip dashed touch={isTablet} onClick={() => setMenu(menu === 'lvl' ? null : 'lvl')}>+ уровень ▾</Chip>
        {menu === 'lvl' && (
          <DropMenu>
            {availableDims.map(d => (
              <MenuItem key={d} onClick={() => addLevel(d)}>
                <Dot color={C.info} size={8} />{DIM_LABELS[d]}
              </MenuItem>
            ))}
            {availableDims.length > 0 && <div style={{ height: 1, background: C.borderLight, margin: '5px 8px' }} />}
            {levels.includes('turn')
              ? <MenuItem disabled hint="уже в цепочке">Ход</MenuItem>
              : <MenuItem onClick={() => addLevel('turn')} hint="только последним · в окне"><Dot color={C.plan} size={8} />Ход</MenuItem>}
          </DropMenu>
        )}
      </span>
      <Chip dashed touch={isTablet} onClick={() => { patch({ levels: null, preset: 'who', selKey: null }); setSelNode(null); }}>Сбросить</Chip>
      {!isMobile && <span style={{ flex: 1 }} />}
      <span style={{ fontSize: 11, color: C.textMuted, flexShrink: 0, fontFamily: FONT.sans }}>Раскладки:</span>
      {SPEND_PRESETS.map(p => {
        const on = st.preset === p.key && !st.levels;
        return (
          <span
            key={p.key}
            onClick={() => { patch({ preset: p.key, levels: null, selKey: null }); setSelNode(null); }}
            style={{
              fontSize: 11, padding: '3px 10px', borderRadius: R.max, cursor: 'pointer', whiteSpace: 'nowrap',
              fontFamily: FONT.sans, flexShrink: 0,
              border: `1px solid ${on ? C.divider : C.border}`,
              background: on ? C.bgSelected : 'none',
              color: on ? C.textHeading : C.textSecondary,
              fontWeight: on ? 600 : 400,
            }}
          >
            {p.label}
          </span>
        );
      })}
    </div>
  );

  // ================= Фильтр-бар =================
  const filterDims = (Object.keys(DIM_LABELS) as SpendDim[])
    .filter(d => (showUsers || !ADMIN_ONLY_DIMS.includes(d)) && !st.filters.some(f => f.dim === d));
  const addFilter = (f: SpendFilter) => {
    patch({ filters: [...st.filters, f], selKey: null });
    setSelNode(null);
    setMenu(null);
  };
  const filterBar = (
    <div style={{
      display: 'flex', gap: 6, alignItems: 'center', padding: '9px 14px',
      borderBottom: `1px solid ${C.borderLight}`,
      flexWrap: isMobile ? 'nowrap' : 'wrap', overflowX: isMobile ? 'auto' : undefined, scrollbarWidth: 'none',
    }}>
      <span style={{ fontSize: 11, color: C.textMuted, flexShrink: 0, fontFamily: FONT.sans }}>Срез:</span>
      {st.filters.map((f, i) => (
        <Chip key={f.dim} filter touch={isTablet} maxW={isTablet ? '100%' : undefined}>
          {DIM_LABELS[f.dim]}: {f.label}
          <ChipX touch={isTablet} onClick={() => { patch({ filters: st.filters.filter((_, j) => j !== i), selKey: null }); setSelNode(null); }} />
        </Chip>
      ))}
      <span style={{ position: 'relative', display: 'inline-flex', flexShrink: 0 }}>
        <Chip touch={isTablet} onClick={() => setMenu(menu === 'filter' || menu?.startsWith('fvals:') ? null : 'filter')}>+ фильтр ▾</Chip>
        {menu === 'filter' && (
          <DropMenu>
            {filterDims.map(d => (
              <MenuItem key={d} onClick={() => setMenu('fvals:' + d)} hint="›">{DIM_LABELS[d]}</MenuItem>
            ))}
          </DropMenu>
        )}
        {menu?.startsWith('fvals:') && (() => {
          const dim = menu.slice(6) as SpendDim;
          return (
            <DropMenu>
              <div style={{ fontSize: 10, color: C.textMuted, padding: '4px 10px 2px', textTransform: 'uppercase', letterSpacing: 0.5 }}>
                {DIM_LABELS[dim]}
              </div>
              {fvals === null && <div style={{ padding: '6px 10px' }}><Skel w="80%" h={12} /></div>}
              {fvals?.length === 0 && <div style={{ padding: '6px 10px', color: C.textMuted }}>Пусто за период</div>}
              {fvals?.map(v => {
                const name = dim === 'source' ? sourceLabel(v.key) : nodeName(dim, v.key, v.name);
                return (
                  <MenuItem key={v.key || '·'} onClick={() => addFilter({ dim, val: v.key, label: name })}
                    hint={v.tokens.total ? fmtTok(v.tokens.total) : `${v.falGenerations} ген.`}>
                    {name}
                  </MenuItem>
                );
              })}
            </DropMenu>
          );
        })()}
      </span>
      {st.filters.length > 0 && (
        <Chip dashed touch={isTablet} onClick={() => { patch({ filters: [], selKey: null }); setSelNode(null); }}>Сбросить срез</Chip>
      )}
    </div>
  );

  // ================= Дерево =================
  const rootChunk = chunks[''];
  const rootTotal = overview?.totals.total ?? 0;

  function renderTurnRow(t: SpendTurnDto): ReactNode {
    const key = `turn:${t.id}`;
    const sel = st.selKey === key;
    const isGen = isGenSource(t.source);
    const isFree = t.source === 'free';
    return (
      <div
        key={t.id}
        onClick={() => selectTurn(t)}
        style={{
          display: 'flex', alignItems: 'center', gap: 8, padding: isTablet ? `${SP.md}px 10px` : '8px 10px', borderRadius: R.lg,
          cursor: 'pointer', background: sel ? C.accentLight : undefined,
          outline: sel ? `1px solid ${C.accentMuted}` : 'none',
        }}
        onMouseEnter={e => { if (!sel) e.currentTarget.style.background = C.bgSelected; }}
        onMouseLeave={e => { e.currentTarget.style.background = sel ? C.accentLight : 'none'; }}
      >
        <span style={{ width: 14, textAlign: 'center', color: C.textMuted, fontSize: 10, flexShrink: 0 }}>·</span>
        <span style={{ fontFamily: FONT.mono, fontSize: 11, fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap' }}>
          {fmtDate(t.timestamp.slice(0, 10))} {fmtTime(t.timestamp)}
        </span>
        <span style={{ fontSize: 11, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0, fontFamily: FONT.sans }}>
          {isGen ? `${sourceLabel(t.source)} · ${t.model ?? t.label ?? ''}` : [t.label ?? undefined, t.model ?? undefined].filter(Boolean).join(' · ')}
        </span>
        {isFree && (
          <span style={{ fontSize: 10, padding: '1px 7px', borderRadius: R.sm, background: C.successBg, color: C.successText, flexShrink: 0, fontFamily: FONT.sans }}>
            бесплатная
          </span>
        )}
        <span style={{
          marginLeft: 'auto', fontFamily: FONT.mono, fontSize: isGen ? 11 : 12, fontWeight: 600, flexShrink: 0,
          color: isGen ? sourceTextColor(t.source) : isFree ? C.successText : C.textHeading,
        }}>
          {isGen ? `${t.generations} ген.` : fmtTok(t.tokens.total)}
        </span>
      </div>
    );
  }

  function renderChunk(pathKey: string, ancestors: SpendFilter[], levelIdx: number, ancPath: string, parentTotal: number): ReactNode {
    const chunk = chunks[pathKey];
    if (!chunk || chunk.state === 'loading') {
      return (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8, padding: '8px 10px' }}>
          <Skel w="72%" h={16} /><Skel w="58%" h={16} /><Skel w="65%" h={16} />
        </div>
      );
    }
    if (chunk.state === 'error') {
      return (
        <div style={{ padding: '8px 10px' }}>
          <GhostBtn onClick={() => loadChunk(pathKey, ancestors, levelIdx)} style={{ fontSize: 11, padding: '5px 12px' }}>
            Ошибка загрузки — повторить
          </GhostBtn>
        </div>
      );
    }
    if (chunk.state === 'turns') {
      const { data } = chunk;
      if (data.items.length === 0 && !data.windowClamped) {
        return <div style={{ fontSize: 11, color: C.textMuted, padding: '4px 10px 6px 34px', fontFamily: FONT.sans }}>Ходов в этом срезе нет</div>;
      }
      return (
        <>
          {data.items.map(renderTurnRow)}
          {data.items.length < data.total && (
            <div
              onClick={() => loadMoreTurns(pathKey, ancestors, data.items.length)}
              style={{ fontSize: 11, color: C.info, padding: '4px 10px 6px 34px', cursor: 'pointer', fontFamily: FONT.sans }}
            >
              … ещё {data.total - data.items.length} ходов
            </div>
          )}
          {data.windowClamped && (
            <div style={{
              display: 'flex', alignItems: 'center', gap: 6, fontSize: 10, color: C.warningText,
              background: C.warningBg, borderRadius: 7, padding: '4px 9px', margin: '2px 10px 8px 32px', fontFamily: FONT.sans,
            }}>
              🔒 Часть ходов старше окна детализации — доступны только дневные агрегаты
            </div>
          )}
        </>
      );
    }
    // Узлы разреза
    const dim = levels[levelIdx] as SpendDim;
    if (chunk.nodes.length === 0) {
      return <div style={{ fontSize: 11, color: C.textMuted, padding: '4px 10px 6px 34px', fontFamily: FONT.sans }}>Пусто в этом срезе</div>;
    }
    const total = parentTotal || chunk.nodes.reduce((a, n) => a + n.tokens.total, 0);
    return (
      <>
        {chunk.nodes.map(n => {
          const name = dim === 'source' ? sourceLabel(n.key) : nodeName(dim, n.key, n.name);
          const key = pathKey ? `${pathKey}|${dim}:${n.key}` : `${dim}:${n.key}`;
          const nextIdx = levelIdx + 1;
          // Узел «заперт» только перед уровнем ходов: агрегаты дают разрезы, но не ходы
          const lockedForTurns = !n.hasDetail && levels[nextIdx] === 'turn';
          const hasKids = nextIdx < levels.length && !lockedForTurns;
          const open = expanded.has(key) && hasKids;
          const sel = st.selKey === key;
          const share = total > 0 ? n.tokens.total / total : 0;
          // Узел только из генераций медиа (fal/glif): токенов нет — значение счётчиком цветом серии
          const genOnly = n.tokens.total === 0 && n.falGenerations > 0;
          const isFreeSrc = dim === 'source' && n.key === 'free';
          const filters: SpendFilter[] = [...ancestors, { dim, val: n.key, label: name }];
          return (
            <div key={key} style={{ opacity: !n.hasDetail ? 0.72 : 1 }}>
              <div
                onClick={() => clickNode({ key, dim, name, node: n, filters, path: ancPath, parentTotal: total }, hasKids, key, nextIdx)}
                style={{
                  display: 'flex', alignItems: 'center', gap: 8, padding: isTablet ? `${SP.md}px 10px` : '8px 10px', borderRadius: R.lg,
                  cursor: 'pointer', position: 'relative',
                  background: sel ? C.accentLight : undefined,
                  outline: sel ? `1px solid ${C.accentMuted}` : 'none',
                }}
                onMouseEnter={e => { if (!sel) e.currentTarget.style.background = C.bgSelected; }}
                onMouseLeave={e => { e.currentTarget.style.background = sel ? C.accentLight : 'none'; }}
              >
                <span style={{ width: 14, textAlign: 'center', color: C.textMuted, fontSize: 10, flexShrink: 0 }}>
                  {!n.hasDetail && levels[nextIdx] === 'turn' ? '🔒' : hasKids ? (open ? '▾' : '▸') : '·'}
                </span>
                {nodeIcon(dim, name, n.meta, dim === 'source' ? sourceColor(n.key) : undefined)}
                <span style={{ fontWeight: 600, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', fontSize: 13, fontFamily: FONT.sans }}>
                  {name}
                </span>
                {n.turns > 0 && (
                  <span style={{ fontSize: 11, color: C.textMuted, whiteSpace: 'nowrap', flexShrink: 0, fontFamily: FONT.sans }}>
                    {n.turns} х.
                  </span>
                )}
                <span style={{
                  marginLeft: 'auto', fontFamily: FONT.mono, fontSize: genOnly ? 11 : 12, fontWeight: 600, flexShrink: 0,
                  color: genOnly ? (dim === 'source' ? sourceTextColor(n.key) : C.planText) : isFreeSrc ? C.successText : C.textHeading,
                }}>
                  {genOnly ? `${n.falGenerations} ген.` : fmtTok(n.tokens.total)}
                </span>
                {share > 0.01 && (
                  <div style={{ position: 'absolute', left: 10, right: 10, bottom: 2, height: 3, borderRadius: 2, overflow: 'hidden', pointerEvents: 'none' }}>
                    <div style={{ height: '100%', borderRadius: 2, background: C.accent, opacity: 0.45, width: `${Math.round(share * 100)}%` }} />
                  </div>
                )}
              </div>
              {lockedForTurns && sel && (
                <div style={{
                  display: 'flex', alignItems: 'center', gap: 6, fontSize: 10, color: C.warningText,
                  background: C.warningBg, borderRadius: 7, padding: '4px 9px', margin: '2px 10px 8px 32px', fontFamily: FONT.sans,
                }}>
                  Старше окна детализации — только дневные агрегаты, ходы недоступны
                </div>
              )}
              {open && (
                <div style={{ marginLeft: 22, borderLeft: `1px dashed ${C.track}`, paddingLeft: 6 }}>
                  {renderChunk(key, filters, nextIdx, ancPath ? `${ancPath} › ${name}` : name, n.tokens.total)}
                </div>
              )}
            </div>
          );
        })}
      </>
    );
  }

  const treeEmpty = rootChunk?.state === 'nodes' && rootChunk.nodes.length === 0;
  const tree = levels.length === 0
    ? <EmptyBody pic="🧩" title="Уровни не заданы"
        text="Добавьте хотя бы один уровень группировки («+ уровень») или выберите готовую раскладку."
        action={<GhostBtn onClick={() => { patch({ levels: null, preset: 'who', selKey: null }); setSelNode(null); }}>Раскладка по умолчанию</GhostBtn>} />
    : treeEmpty
    ? (emptyKind === 'slice'
        ? <EmptyBody pic="🔍" title="Под этот срез ничего не попало"
            text="Такая комбинация фильтров не встречалась за период. Уберите один из фильтров."
            action={<GhostBtn onClick={() => { patch({ filters: [], day: null, selKey: null }); setSelNode(null); }}>Сбросить срез</GhostBtn>} />
        : emptyKind === 'period'
        ? <EmptyBody pic="📅" title="За этот период трат нет"
            text="В выбранном периоде ходов не было — дереву нечего показать. Попробуйте более широкий период или снимите фильтры." />
        : <EmptyBody pic="🪙" title="Токенов ещё не потрачено"
            text="Дерево наполнится после первого хода в любом чате. Бесплатные модели тоже считаются — их токены видны зелёной серией." />)
    : renderChunk('', [], 0, '', rootTotal);

  // ================= Панель деталей =================
  const detail = selTurnId
    ? <TurnPassport detail={turnDetail} showUsers={showUsers} fill={fill} onCloseScreen={onCloseScreen} />
    : selNode
      ? <NodeDetail sel={selNode} ov={nodeOv} rootTotal={rootTotal} hasTurnLevel={levels.includes('turn')} fill={fill} isTablet={isTablet}
          onShowTurns={() => {
            if (!levels.includes('turn')) patch({ levels: [...levels, 'turn'] });
          }} detailDays={detailDays} />
      : overviewError
        ? <LoadError onRetry={onRetryOverview} />
        : (
          <SliceSummary overview={overview} filters={st.filters} day={st.day} rootTotal={rootTotal} fill={fill} isTablet={isTablet} />
        );

  return (
    <div
      onClick={() => { if (menu) setMenu(null); }}
      style={isTablet ? { display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0 } : undefined}
    >
      {pivotBar}
      {filterBar}
      {/* Планшет: горизонтальный master-detail окупается примерно с 1100px — на 832
          дереву не хватает ширины под имена, поэтому деталь встаёт ПОД дерево */}
      <div style={{
        ...(isTablet
          ? { display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0 }
          : { display: 'grid', gridTemplateColumns: isMobile ? '1fr' : 'minmax(380px, 7fr) minmax(320px, 5fr)' }),
        minHeight: isMobile || isTablet ? 0 : 520,
      }}>
        <div style={{
          borderRight: isMobile || isTablet ? 'none' : `1px solid ${C.borderLight}`, minWidth: 0,
          // 0 1 auto: короткое дерево занимает одну строку, длинное упирается в потолок и скроллится внутри
          ...(isTablet ? { borderBottom: `1px solid ${C.borderLight}`, flex: '0 1 auto', maxHeight: '55%', minHeight: 0, display: 'flex', flexDirection: 'column' } : null),
        }}>
          <div style={{
            padding: '10px 8px 16px', overflow: 'auto', maxHeight: isMobile || isTablet ? undefined : 640,
            ...(isTablet ? { flex: 1, minHeight: 0 } : null),
          }}>
            {rootChunk?.state === 'error' ? <LoadError onRetry={() => setTreeTick(t => t + 1)} /> : tree}
          </div>
        </div>
        {!isMobile && (
          <div style={{
            background: C.bgCard, minWidth: 0,
            // На планшете панель — подошва острова: скругление на обоих нижних углах
            borderRadius: isTablet ? `0 0 ${R.xxl}px ${R.xxl}px` : `0 0 ${R.xxl}px 0`,
            ...(isTablet ? { flex: '1 1 auto', minHeight: 260, display: 'flex', flexDirection: 'column' } : null),
          }}>
            {detail}
          </div>
        )}
      </div>
      {/* Мобильная шторка паспорта узла/хода */}
      {isMobile && sheet && (st.selKey || selNode) && (
        <>
          <div onClick={() => setSheet(false)} style={{ position: 'fixed', inset: 0, zIndex: Z.modal + 1, background: C.overlay }} />
          <div style={{
            position: 'fixed', left: 0, right: 0, bottom: 0, zIndex: Z.modal + 2,
            borderRadius: `${R.sheet}px ${R.sheet}px 0 0`, background: C.bgCard, boxShadow: SHADOW.sheet,
            padding: '8px 0 18px', borderTop: `1px solid ${C.borderLight}`, maxHeight: '72dvh', overflow: 'auto',
          }}>
            <div onClick={() => setSheet(false)} style={{ width: 36, height: 4, borderRadius: 2, background: C.track, margin: '4px auto 10px', cursor: 'pointer' }} />
            {detail}
          </div>
        </>
      )}
    </div>
  );
}

// ---- Шапка панели деталей ----
function DetailHead({ path, title, sub }: { path: string; title: string; sub: string }) {
  return (
    <div style={{ padding: '16px 18px 12px', borderBottom: `1px solid ${C.borderLight}` }}>
      <div style={{ fontSize: 11, color: C.textMuted, marginBottom: 4, minHeight: 13, fontFamily: FONT.sans, wordBreak: 'break-word' }}>{path || ' '}</div>
      <div style={{ fontFamily: FONT.serif, fontSize: 18, fontWeight: 700, color: C.textHeading, wordBreak: 'break-word' }}>{title}</div>
      {sub && <div style={{ fontSize: 11, color: C.textSecondary, marginTop: 2, fontFamily: FONT.sans, wordBreak: 'break-word' }}>{sub}</div>}
    </div>
  );
}

function Metric({ label, value, sub, color }: { label: string; value: string; sub?: string; color?: string }) {
  return (
    <div style={{ background: C.bgPanel, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, padding: '10px 12px', minWidth: 0 }}>
      <div style={{ fontSize: 10, textTransform: 'uppercase', letterSpacing: 0.5, color: C.textMuted, fontFamily: FONT.sans }}>{label}</div>
      <div style={{ fontFamily: FONT.mono, fontSize: 17, fontWeight: 600, color: color ?? C.textHeading, marginTop: 3, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{value}</div>
      {sub && <div style={{ fontSize: 10, color: C.textMuted, marginTop: 2, fontFamily: FONT.sans }}>{sub}</div>}
    </div>
  );
}

function Section({ title, extra, children }: { title: ReactNode; extra?: ReactNode; children: ReactNode }) {
  return (
    <div style={{ border: `1px solid ${C.borderLight}`, borderRadius: R.lg, background: C.bgPanel, padding: 12 }}>
      <div style={{ fontSize: 12, fontWeight: 600, color: C.textHeading, marginBottom: 10, display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', fontFamily: FONT.sans }}>
        {title}{extra}
      </div>
      {children}
    </div>
  );
}

// Спарклайн дневного ряда: агрегатные дни приглушены, пунктир — граница окна, пик подсвечен
function DaySpark({ byDay, fill }: { byDay: { date: string; aggregated: boolean; total: number }[]; fill?: boolean }) {
  const max = Math.max(1, ...byDay.map(d => d.total));
  const hasAgg = byDay.some(d => d.aggregated);
  // Пунктирная граница — один раз, перед первым неагрегатным днём
  const sepIdx = hasAgg ? byDay.findIndex(d => !d.aggregated) : -1;
  return (
    <div style={{ display: 'flex', alignItems: 'flex-end', gap: 2, height: fill ? 96 : 44 }}>
      {byDay.map((d, i) => {
        const sep = i === sepIdx;
        return (
          <span key={d.date} style={{ display: 'contents' }}>
            {sep && <i style={{ borderLeft: `1px dashed ${C.warning}`, alignSelf: 'stretch', margin: '0 1px' }} />}
            <i
              title={`${fmtDate(d.date)} · ${fmtTok(d.total)}`}
              style={{
                flex: 1, borderRadius: '2px 2px 0 0', minHeight: 2,
                height: `${Math.max(4, Math.round(d.total / max * 100))}%`,
                background: d.total === max && d.total > 0 ? C.accent : C.accentMuted,
                opacity: d.aggregated ? 0.4 : 1,
              }}
            />
          </span>
        );
      })}
    </div>
  );
}

// Общая начинка свода (узла или всего среза) из overview-ответа
function OverviewBody({ ov, shareOfRoot, hasTurnLevel, onShowTurns, detailDays, fill = false, isTablet = false }: {
  ov: SpendOverviewResponse; shareOfRoot: number | null; hasTurnLevel: boolean;
  onShowTurns?: () => void; detailDays?: number; fill?: boolean; isTablet?: boolean;
}) {
  const s = ov.totals;
  const freeRow = ov.cards.sources.find(r => r.key === 'free');
  const srcRows = ov.cards.sources.filter(r => !isGenSource(r.key) && r.tokens.total > 0);
  // Генерации медиа по источникам (fal/glif) — из карточки источников: ov.falGenerations суммарный
  const genRows = ov.cards.sources.filter(r => isGenSource(r.key) && r.falGenerations > 0);
  const stackTotal = srcRows.reduce((a, r) => a + r.tokens.total, 0);
  const models = ov.cards.models.slice(0, 4);
  const modelsMax = Math.max(1, ...models.map(m => m.tokens.total));
  const windowClamped = ov.byDay.some(d => d.aggregated);
  return (
    <div style={{
      padding: '14px 18px 18px', display: 'flex', flexDirection: 'column', gap: 12, overflow: 'auto',
      maxHeight: fill ? undefined : 610, ...(fill ? { flex: 1, minHeight: 0 } : null),
    }}>
      <div style={{ display: 'grid', gridTemplateColumns: isTablet ? 'repeat(4, minmax(0, 1fr))' : '1fr 1fr', gap: 8 }}>
        <Metric label="Токены за период" value={fmtTok(s.total)} color={C.accent}
          sub={shareOfRoot !== null ? `${Math.round(shareOfRoot * 100)}% текущего среза` : undefined} />
        <Metric label="Ходов" value={String(ov.turns)}
          sub={ov.falGenerations ? `+ ${ov.falGenerations} генераций медиа` : windowClamped ? 'часть периода — агрегаты (🔒)' : 'все в окне детализации'} />
        <Metric label="In / Out" value={`${fmtTok(s.input)} / ${fmtTok(s.output)}`}
          sub={`cache read ${fmtTok(s.cacheRead)} · create ${fmtTok(s.cacheCreation)}`} />
        <Metric label="Бесплатные" value={freeRow ? fmtTok(freeRow.tokens.total) : '—'} color={C.successText}
          sub={freeRow ? `${freeRow.turns} ходов` : undefined} />
      </div>

      {(srcRows.length > 0 || ov.falGenerations > 0) && (
        <Section title="Источники расхода">
          {stackTotal > 0 && (
            <div style={{ display: 'flex', height: 10, borderRadius: 5, overflow: 'hidden', marginBottom: 8 }}>
              {srcRows.map(r => (
                <i key={r.key} style={{ flex: r.tokens.total, background: sourceColor(r.key) }} />
              ))}
            </div>
          )}
          <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap' }}>
            {srcRows.map(r => (
              <span key={r.key} style={{ display: 'inline-flex', alignItems: 'center', gap: 4, fontSize: 10, color: C.textSecondary, fontFamily: FONT.sans }}>
                <Dot color={sourceColor(r.key)} size={8} />{sourceLabel(r.key)} {fmtTok(r.tokens.total)}
              </span>
            ))}
            {genRows.map(r => (
              <span key={r.key} style={{ display: 'inline-flex', alignItems: 'center', gap: 4, fontSize: 10, color: C.textSecondary, fontFamily: FONT.sans }}>
                <Dot color={sourceColor(r.key)} size={8} />{sourceLabel(r.key)} · {r.falGenerations} генераций
              </span>
            ))}
          </div>
        </Section>
      )}

      {models.length > 0 && (
        <Section title="Топ моделей">
          {models.map(m => (
            <HBar key={m.key || '·'} label={nodeName('model', m.key, m.name)} value={fmtTok(m.tokens.total)}
              share={m.tokens.total / modelsMax} color={C.accent} grow={isTablet} />
          ))}
        </Section>
      )}

      <Section
        title="Динамика"
        extra={windowClamped && detailDays !== undefined && (
          <span style={{ marginLeft: 'auto', fontSize: 10, color: C.textMuted, fontFamily: FONT.sans }}>
            🔒 старше {detailDays} дней — агрегаты
          </span>
        )}
      >
        <DaySpark byDay={ov.byDay} fill={fill} />
      </Section>

      {!hasTurnLevel && onShowTurns && (
        <GhostBtn onClick={onShowTurns} style={{ justifyContent: 'center' }}>
          Показать ходы среза{detailDays ? ` · в окне ${detailDays} дней` : ''}
        </GhostBtn>
      )}
    </div>
  );
}

// «Итого по срезу» — когда узел не выбран
function SliceSummary({ overview, filters, day, rootTotal, fill = false, isTablet = false }: {
  overview: SpendOverviewResponse | null; filters: SpendFilter[]; day: string | null; rootTotal: number;
  fill?: boolean; isTablet?: boolean;
}) {
  if (!overview) {
    return <div style={{ padding: 18 }}><Skel w="40%" h={20} style={{ marginBottom: 12 }} /><Skel w="100%" h={120} /></div>;
  }
  const subParts = [
    ...filters.map(f => `${DIM_LABELS[f.dim]}: ${f.label}`),
    ...(day ? [`День: ${fmtDate(day)}`] : []),
  ];
  return (
    <>
      <DetailHead path="весь текущий срез" title="Итого по срезу" sub={subParts.length ? subParts.join(' · ') : 'фильтры не заданы'} />
      <OverviewBody ov={overview} shareOfRoot={rootTotal > 0 ? 1 : null} hasTurnLevel fill={fill} isTablet={isTablet} />
    </>
  );
}

// Свод выбранного узла
function NodeDetail({ sel, ov, rootTotal, hasTurnLevel, onShowTurns, detailDays, fill = false, isTablet = false }: {
  sel: SelNode; ov: SpendOverviewResponse | null | 'error'; rootTotal: number;
  hasTurnLevel: boolean; onShowTurns: () => void; detailDays?: number; fill?: boolean; isTablet?: boolean;
}) {
  const head = (
    <DetailHead path={sel.path} title={sel.name}
      sub={`${DIM_LABELS[sel.dim]} · ${sel.node.turns} ходов за период${sel.node.hasDetail ? '' : ' · 🔒 агрегаты'}`} />
  );
  // Разбивка постановки — только у узлов разреза «задача» и только для реальной задачи
  // (пустой ключ = «Вне задач», постановки у него нет)
  const promptTaskId = sel.dim === 'task' && sel.node.key ? sel.node.key : null;
  if (ov === 'error') return <>{head}<div style={{ padding: 18, fontSize: 12, color: C.textMuted }}>Не удалось загрузить свод узла.</div></>;
  if (!ov) return <>{head}<div style={{ padding: 18 }}><Skel w="100%" h={90} style={{ marginBottom: 10 }} /><Skel w="100%" h={70} /></div></>;
  return (
    <>
      {head}
      {promptTaskId && <TaskPromptBreakdown taskId={promptTaskId} />}
      <OverviewBody ov={ov} shareOfRoot={rootTotal > 0 ? ov.totals.total / rootTotal : null}
        hasTurnLevel={hasTurnLevel} onShowTurns={onShowTurns} detailDays={detailDays} fill={fill} isTablet={isTablet} />
    </>
  );
}

// Разбивка постановки задачи по секциям: из чего сложился промпт, отправленный исполнителю.
// Показывается при раскрытии узла разреза «Задача». Данные — только размеры в символах,
// содержимого постановки и заметок в сторе нет by design.
function TaskPromptBreakdown({ taskId }: { taskId: string }) {
  const [runs, setRuns] = useState<SpendTaskPromptRun[] | null | 'error'>(null);
  useEffect(() => {
    let cancelled = false;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- скелетон вместо старой разбивки при смене задачи
    setRuns(null);
    api.spend.taskPrompt(taskId)
      .then(d => { if (!cancelled) setRuns(d.runs); })
      .catch(() => { if (!cancelled) setRuns('error'); });
    return () => { cancelled = true; };
  }, [taskId]);

  if (runs === 'error') return null;   // разбивка — дополнение, её отсутствие не ошибка экрана
  if (!runs) return <div style={{ padding: '0 18px 12px' }}><Skel w="100%" h={70} /></div>;
  // Задача запускалась до появления учёта — блок просто не показываем
  if (runs.length === 0) return null;

  const r = runs[0];   // последний запуск: он отражает текущий вид постановки
  const rows: [string, number][] = [
    ['Задача', r.task],
    ['Правила', r.rules + r.restrictions + r.expected + r.tools],
    ['Делегирование', r.delegation + r.omo],
    ['Контекст', r.context],
    ['Заметки', r.notes],
  ];
  const shown = rows.filter(([, v]) => v > 0);
  const max = Math.max(1, ...shown.map(([, v]) => v));

  return (
    <Section title={`Постановка · ${fmtTok(r.totalChars)} симв.${runs.length > 1 ? ` · запусков: ${runs.length}` : ''}`}>
      {shown.map(([label, v]) => (
        <HBar key={label} label={label} value={`${Math.round(v / r.totalChars * 100)}%`}
          share={v / max} color={C.accentSoft} />
      ))}
      <div style={{ fontSize: 10, color: C.textMuted, fontFamily: FONT.sans, marginTop: 2 }}>
        Промпт постановки уходит исполнителю каждый ход — экономия здесь умножается на число ходов.
      </div>
    </Section>
  );
}

// Паспорт хода: состав токенов, соседние ходы, приватность/переход в чат
function TurnPassport({ detail, showUsers, fill = false, onCloseScreen }: {
  detail: SpendTurnDetailResponse | null | 'error'; showUsers: boolean; fill?: boolean; onCloseScreen: () => void;
}) {
  if (detail === 'error') {
    return <EmptyBody pic="🔍" title="Ход недоступен" text="Паспорт хода не найден: он старше окна детализации или недоступен вашей роли." />;
  }
  if (!detail) {
    return <div style={{ padding: 18 }}><Skel w="55%" h={20} style={{ marginBottom: 12 }} /><Skel w="100%" h={110} /></div>;
  }
  const t = detail.turn;
  const isGen = isGenSource(t.source);
  const chatTotal = detail.neighbors.reduce((a, n) => a + n.total, 0);
  const share = chatTotal > 0 ? Math.round(t.tokens.total / chatTotal * 100) : 0;
  const win = detail.neighbors;
  const maxN = Math.max(1, ...win.map(n => n.total));
  const comp = [
    ['Cache read', t.tokens.cacheRead, C.accentSoft],
    ['Input', t.tokens.input, C.accent],
    ['Output', t.tokens.output, C.accent],
    ['Cache create', t.tokens.cacheCreation, C.accentMuted],
  ].filter(c => (c[1] as number) > 0) as [string, number, string][];
  const compMax = Math.max(1, ...comp.map(c => c[1]));
  const cacheShare = t.tokens.total > 0 ? t.tokens.cacheRead / t.tokens.total : 0;

  const openChat = () => {
    if (!t.sessionId) return;
    const url = t.projectId
      ? `#/project/${t.projectId}/chat/${t.sessionId}`
      : `#/chats/${encodeURIComponent(t.sessionId)}`;
    window.dispatchEvent(new CustomEvent('cc-open-url', { detail: { url } }));
    onCloseScreen();
  };

  const pathParts = [showUsers ? t.userName : null, t.projectName, t.chatName ?? t.taskTitle].filter(Boolean) as string[];
  const subParts = [
    t.model, t.provider,
    t.personaName ? `персона ${t.personaName}` : null,
    `источник: ${sourceLabel(t.source).toLowerCase()}`,
  ].filter(Boolean) as string[];

  return (
    <>
      <DetailHead
        path={pathParts.join(' › ')}
        title={`${isGen ? `Генерация ${sourceLabel(t.source)}` : 'Ход'} · ${fmtDate(t.timestamp.slice(0, 10))}, ${fmtTime(t.timestamp)}`}
        sub={subParts.join(' · ')}
      />
      <div style={{
        padding: '14px 18px 18px', display: 'flex', flexDirection: 'column', gap: 12, overflow: 'auto',
        maxHeight: fill ? undefined : 610, ...(fill ? { flex: 1, minHeight: 0 } : null),
      }}>
        {isGen ? (
          <div style={{ border: `1px solid ${C.borderLight}`, borderRadius: R.lg, background: C.bgPanel, padding: 12 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
              <span style={{ fontWeight: 600, color: C.textHeading, fontSize: 13, fontFamily: FONT.sans }}>Операция {sourceLabel(t.source)}</span>
              <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontWeight: 600, color: sourceTextColor(t.source) }}>{t.generations} ген.</span>
            </div>
            <div style={{ fontSize: 11, color: C.textSecondary, fontFamily: FONT.sans }}>
              Модель {t.model ?? t.label ?? '—'} · токенов нет — считаем генерации. В суммы токенов не входит.
            </div>
          </div>
        ) : (
          <div style={{ border: `1px solid ${C.borderLight}`, borderRadius: R.lg, background: C.bgPanel, padding: 12 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
              <span style={{ fontWeight: 600, color: C.textHeading, fontSize: 13, fontFamily: FONT.sans }}>Паспорт хода</span>
              <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontWeight: 600, color: C.accent }}>{fmtTok(t.tokens.total)} ткн</span>
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4, minmax(0, 1fr))', gap: 6 }}>
              {[['In', t.tokens.input], ['Out', t.tokens.output], ['Cache read', t.tokens.cacheRead], ['Cache create', t.tokens.cacheCreation]].map(([l, v]) => (
                <div key={l as string} style={{ background: C.bgInset, borderRadius: R.md, padding: '7px 9px', minWidth: 0, display: 'flex', flexDirection: 'column', justifyContent: 'space-between' }}>
                  <div style={{ fontSize: 9, textTransform: 'uppercase', letterSpacing: 0.4, color: C.textMuted, fontFamily: FONT.sans }}>{l}</div>
                  <div style={{ fontFamily: FONT.mono, fontSize: 12, fontWeight: 600, color: C.textHeading, marginTop: 2 }}>{fmtTok(v as number)}</div>
                </div>
              ))}
            </div>
            {t.own
              ? t.sessionId && (
                  <GhostBtn onClick={openChat} style={{ marginTop: 10, fontSize: 11, padding: '5px 12px' }}>
                    Открыть чат →
                  </GhostBtn>
                )
              : (
                <div style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 10, color: C.textMuted, marginTop: 10, fontFamily: FONT.sans }}>
                  🔒 Содержимое сообщений недоступно — только метрики хода
                </div>
              )}
          </div>
        )}

        {!isGen && comp.length > 0 && (
          <Section title="Состав хода по типам токенов">
            {comp.map(([l, v, color]) => (
              <HBar key={l} label={l} value={fmtTok(v)} share={v / compMax} color={color} grow={fill} />
            ))}
            {cacheShare > 0.5 && (
              <div style={{ fontSize: 10, color: C.textMuted, marginTop: 4, fontFamily: FONT.sans }}>
                {Math.round(cacheShare * 100)}% объёма — чтение кеша: контекст переиспользован, а не набран заново
              </div>
            )}
          </Section>
        )}

        {!isGen && win.length > 1 && (
          <Section title={`Чат «${t.chatName ?? t.taskTitle ?? '…'}» рядом с этим ходом`}>
            <div style={{ display: 'flex', alignItems: 'flex-end', gap: 2, height: fill ? 96 : 44 }}>
              {win.map(n => (
                <i key={n.id} title={fmtTok(n.total)} style={{
                  flex: 1, borderRadius: '2px 2px 0 0', minHeight: 2,
                  height: `${Math.max(6, Math.round(n.total / maxN * 100))}%`,
                  background: n.id === t.id ? C.accent : C.accentMuted,
                }} />
              ))}
            </div>
            <div style={{ fontSize: 10, color: C.textMuted, marginTop: 6, fontFamily: FONT.sans }}>
              {win.length} соседних ходов · этот — {share}% токенов чата за период
            </div>
          </Section>
        )}
      </div>
    </>
  );
}

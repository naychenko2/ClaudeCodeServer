// Панель «Граф» в правой рельсе инструментов — инспектор графа: поиск типа,
// фильтры отображения, god-узлы, паспорт выбранного типа и мини-карта проекта внизу.
// Деградирует при empty (только сборка), контролы блокируются при loading. Сама панель
// живёт в PanelShell рельсы, который даёт шапку острова и кнопку закрытия — здесь только тело.
//
// Контролы (поиск, фильтры, перестроить, развернуть) живут в ШАПКЕ панели через
// PanelHeaderSlot — как у «Файлов», «Изменений» и «Задач». Раньше они стояли в теле
// стопкой секций с разделителями во всю ширину, и панель читалась как форма настроек
// посреди списков-навигаторов. Тело теперь — одна прокручиваемая колонка.
import { useEffect, useMemo, useState } from 'react';
import {
  Search, ChevronRight, FileCode, RefreshCw, AlertTriangle, Loader, Filter,
  Check, Unlink,
} from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import {
  Button, Dot, IconField, EmptyState, WaitingIndicator, Toggle, IconButton,
  Menu, MenuItem, MenuSep, PanelHeaderSlot, useHasPanelHeader, usePanelHeaderHold,
} from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useCodeGraph, useCodeGraphActions, GRAPH_RELATIONS } from '../../lib/codeGraph';
import { useRequestPanelFill } from '../../pages/workspace/panelFill';
import { CodeGraphMiniMap } from './CodeGraphMiniMap';
import { focusNeighbours, graphDegree, isTestSourceFile } from './graphFocus';
import {
  EDGE_COLOR, EDGE_BG, KIND_COLOR, KIND_RING, KIND_GLYPH, RELATION_LABEL,
} from './graphTokens';
import type { CodeGraphRelation, CodeGraphNode, CodeGraph, CodeGraphEdge } from '../../types';

interface Props {
  projectId: string;
  // Открыт ли сейчас документ графа в центре: пока закрыт — панель показывает
  // мини-карту и кнопку «Развернуть», при открытом и то и другое лишнее
  graphOpen?: boolean;
  // Открыть документ графа в центре. Дёргается ТОЛЬКО явным действием (кнопка
  // «Развернуть», клик по мини-карте): режимные действия панели (фильтр, поиск,
  // god-узел, переход по связи в паспорте) центр не трогают — они меняют вид графа,
  // а не место чтения. Раньше их открывал каждый, и панель невозможно было
  // использовать как инспектор, не выбросив документ поверх чата.
  onEnsureGraphOpen: () => void;
  // Свернуть документ центра к чату — кнопка встаёт на место «Развернуть» в углу карты.
  // Не задан (мобильная шторка поверх самого документа) — остаётся только «Развернуть»
  onCollapseGraph?: () => void;
  // Панель показана ВНУТРИ самого документа (мобильная шторка): карта там была бы
  // третьим холстом поверх того, что уже на экране
  hideMap?: boolean;
  onOpenFile: (path: string, line?: number) => void;
  onBuild: () => void;
}

export function CodeGraphPanel({ projectId, graphOpen, onEnsureGraphOpen, onCollapseGraph, hideMap, onOpenFile, onBuild }: Props) {
  const s = useCodeGraph();
  const a = useCodeGraphActions();
  const inHeader = useHasPanelHeader();
  // Поиск разворачивается лупой — как в «Файлах»: в узкой колонке поле не должно
  // стоять постоянной полосой ради действия, которое нужно изредка
  const [searchOpen, setSearchOpen] = useState(false);
  const [filtersAnchor, setFiltersAnchor] = useState<DOMRect | null>(null);
  // Пока открыто меню фильтров или поле поиска, контролы шапки не гаснут
  usePanelHeaderHold(!!filtersAnchor || searchOpen);

  // Первичная загрузка при монтировании панели. loadCodeGraph идемпотентна —
  // безопасно вызывать на каждом рендере, сетевых дублей не будет
  useEffect(() => { a.load(projectId); }, [a, projectId]);

  // Панель с данными просит всю высоту колонки: внизу живёт мини-карта, и по
  // контенту она сжалась бы до полоски. Признак — наличие данных, а не статус:
  // обновление поверх готового графа (status='loading') не должно ронять высоту.
  useRequestPanelFill(!!s.data);

  const degree = useMemo(() => (s.data ? graphDegree(s.data) : undefined), [s.data]);

  // Счётчики рёбер по типу связи — для подписи в меню фильтров
  const relCounts = useMemo(() => {
    const c: Record<CodeGraphRelation, number> = { Calls: 0, Implements: 0, References: 0 };
    if (s.data) for (const e of s.data.edges) c[e.relation]++;
    return c;
  }, [s.data]);

  const selected = useMemo(() => {
    if (!s.selectedId || !s.data) return null;
    return s.data.nodes.find(n => n.id === s.selectedId) ?? null;
  }, [s.selectedId, s.data]);

  // Поиск: узлы, чей label или FQN содержит запрос
  const searchResults = useMemo(() => {
    if (!s.query.trim() || !s.data) return [];
    const q = s.query.trim().toLowerCase();
    return s.data.nodes.filter(n =>
      n.label.toLowerCase().includes(q) || n.fullyQualifiedName.toLowerCase().includes(q)
    );
  }, [s.query, s.data]);

  // Счётчики скрытых узлов — для сводки активных фильтров
  const hiddenTestCount = useMemo(() => {
    if (!s.hideTestNodes || !s.data) return 0;
    return s.data.nodes.filter(n => isTestSourceFile(n.sourceFile)).length;
  }, [s.hideTestNodes, s.data]);

  const hiddenOrphanCount = useMemo(() => {
    if (!s.hideOrphanNodes || !s.data) return 0;
    const deg = new Map<string, number>();
    for (const e of s.data.edges) { deg.set(e.source, (deg.get(e.source) ?? 0) + 1); deg.set(e.target, (deg.get(e.target) ?? 0) + 1); }
    return s.data.nodes.filter(n => !deg.has(n.id) || deg.get(n.id) === 0).length;
  }, [s.hideOrphanNodes, s.data]);

  const disabled = s.status === 'loading' || s.status === 'building';
  const empty = s.status === 'empty';
  const allRelations = s.filters.Calls && s.filters.Implements && s.filters.References;
  const filtersOn = !allRelations || s.hideTestNodes || s.hideOrphanNodes;
  const isStale = !!s.data?.metadata.isStale;

  if (s.status === 'building') {
    // Сборка запущена (кнопкой или бэкендом) — ждём: polling сам переведёт в ready.
    // Иконка Loader, не RefreshCw как у empty: состояния должны различаться с первого взгляда
    return (
      <EmptyState compact
        icon={<Loader size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Граф строится…"
        subtitle="Сборка займёт около минуты — панель обновится сама."
        action={<WaitingIndicator />}
      />
    );
  }

  if (s.status === 'loading' && !s.data) {
    // Первичная загрузка: данных ещё нет — вместо «задизейбленной формы с нулями»
    // честное состояние ожидания (обновление поверх готового графа — приглушение ниже)
    return (
      <EmptyState compact
        icon={<Loader size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Загружаю граф…"
        subtitle="Снапшот графа подгружается — обычно это секунды."
        action={<WaitingIndicator />}
      />
    );
  }

  if (empty) {
    // Empty: фильтры бессмысленны без данных — только сборка
    return (
      <EmptyState compact
        icon={<RefreshCw size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Граф не собран"
        subtitle="Соберите граф, чтобы увидеть типы и связи проекта."
        action={<Button variant="primary" size="sm" fullWidth onClick={onBuild} leftIcon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>Построить граф</Button>}
      />
    );
  }

  if (s.status === 'error') {
    return (
      <EmptyState compact
        icon={<Unlink size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Не удалось загрузить"
        subtitle={s.error ?? 'Повторите позже.'}
        action={<Button variant="secondary" size="sm" fullWidth onClick={() => a.load(projectId, true)}>Повторить</Button>}
      />
    );
  }

  // Главное действие панели — пересборка графа. Живёт в закреплённом слоте шапки
  // (pinned): нейтральные иконки проявляются по наведению на карточку, а эта кнопка
  // нужна видимой всегда — граф устаревает от каждой правки кода.
  // Вход в документ центра сюда не входит: его открывает мини-карта над телом панели.
  const mainAction = (
    <Button variant="primary" size="xs" onClick={() => a.build(projectId)}
      title={isStale ? 'Код изменился после сборки — перестроить граф' : 'Перестроить граф'}
      leftIcon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>
      Перестроить
    </Button>
  );

  // Вспомогательные контролы панели — нейтральными иконками
  const controls = (
    <>
      <IconButton size="xs" title="Поиск типа" active={searchOpen}
        onClick={() => { const next = !searchOpen; setSearchOpen(next); if (!next) a.setQuery(''); }}>
        <Search size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
      </IconButton>
      {/* rect берём СРАЗУ, а не внутри апдейтера: тот вызывается на фазе рендера,
          когда синтетическое событие уже обнулило currentTarget (падало в ErrorBoundary) */}
      <IconButton size="xs" title="Фильтры отображения" active={filtersOn || !!filtersAnchor}
        onClick={e => {
          const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
          setFiltersAnchor(f => (f ? null : rect));
        }}>
        <Filter size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
      </IconButton>
    </>
  );

  return (
    <div style={{
      display: 'flex', flexDirection: 'column', minHeight: 0, flex: 1,
      opacity: disabled ? 0.45 : 1, pointerEvents: disabled ? 'none' : 'auto',
    }}>
      {inHeader
        ? (
          <>
            <PanelHeaderSlot>{controls}</PanelHeaderSlot>
            <PanelHeaderSlot pinned>{mainAction}</PanelHeaderSlot>
          </>
        )
        // Шапки нет (мобильная шторка) — те же контролы своим рядом
        : <div style={{
            flexShrink: 0, display: 'flex', alignItems: 'center', gap: SP.xs,
            padding: `${SP.sm}px ${SP.md}px`, borderBottom: `1px solid ${C.borderLight}`,
          }}>{controls}{mainAction}</div>
      }

      {/* Меню фильтров: что скрыть на холсте и какие связи показывать */}
      {filtersAnchor && (
        <Menu anchor={filtersAnchor} minWidth={240} maxHeight={320} onClose={() => setFiltersAnchor(null)}>
          <MenuItem
            label={<CheckLabel on={s.hideTestNodes}>Скрыть тесты{s.hideTestNodes ? ` · ${hiddenTestCount}` : ''}</CheckLabel>}
            onClick={() => a.toggleHideTestNodes()}
          />
          <MenuItem
            label={<CheckLabel on={s.hideOrphanNodes}>Скрыть сироты{s.hideOrphanNodes ? ` · ${hiddenOrphanCount}` : ''}</CheckLabel>}
            onClick={() => a.toggleHideOrphanNodes()}
          />
          <MenuSep />
          {GRAPH_RELATIONS.map(rel => (
            <MenuItem key={rel}
              icon={<Dot color={s.filters[rel] ? EDGE_COLOR[rel] : C.textMuted} />}
              label={<CheckLabel on={s.filters[rel]}>{RELATION_LABEL[rel]} · {relCounts[rel]}</CheckLabel>}
              onClick={() => a.toggleFilter(rel)}
            />
          ))}
          {filtersOn && (
            <>
              <MenuSep />
              <MenuItem label="Сбросить фильтры" onClick={() => { a.resetFilters(); setFiltersAnchor(null); }} />
            </>
          )}
        </Menu>
      )}

      {/* Карта проекта с навигацией — она же вход в документ центра и выход из него.
          Стоит НАД инспектором: это ответ на вопрос «что за проект», а поиск и паспорт —
          уже разбор деталей. Показывается и при открытом документе: карта остаётся
          навигатором, а кнопка в её углу переключается на «Свернуть» */}
      {!hideMap && <CodeGraphMiniMap graphOpen={graphOpen} onExpand={onEnsureGraphOpen} onCollapse={onCollapseGraph} />}

      {/* Инспектор — одна прокручиваемая колонка. Скролл общий, а не у паспорта:
          иначе карта над ним отъезжала бы вместе с содержимым */}
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        {/* Stale-предупреждение: граф может отставать от кода */}
        {isStale && (
          <div style={{
            display: 'flex', gap: SP.xs, alignItems: 'flex-start', padding: `${SP.sm}px ${SP.md}px`,
            background: C.warningBg, fontSize: FS.xs, color: C.warningText, lineHeight: 1.45,
          }}>
            <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 1 }} />
            <span>Код изменился после сборки — граф может отставать.</span>
          </div>
        )}

        {/* Поиск типа — полосой, пока открыт лупой */}
        {searchOpen && (
          <div style={{ padding: `${SP.sm}px ${SP.md}px 0` }}>
            <IconField
              icon={<Search size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
              value={s.query}
              onChange={a.setQuery}
              placeholder="Поиск типа…"
              height={32}
              radius={R.lg}
              fontSize={FS.sm}
            />
          </div>
        )}

        {/* Результаты поиска — до 20, остальные счётчиком */}
        {searchOpen && s.query.trim() && (
          <div style={{ padding: `${SP.sm}px ${SP.sm}px 0` }}>
            {searchResults.length === 0 && (
              <div style={{ fontSize: FS.xs, color: C.textMuted, padding: `${SP.xs}px ${SP.sm}px` }}>
                Ничего не найдено
              </div>
            )}
            {searchResults.slice(0, SEARCH_LIMIT).map(n => (
              <Row key={n.id} onClick={() => a.select(n.id)} active={n.id === s.selectedId}>
                <RowName>{n.label}</RowName>
                <RowMeta mono>{KIND_GLYPH[n.kind]}</RowMeta>
                <RowMeta style={{ maxWidth: 100, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {n.sourceFile}:{n.sourceLocation}
                </RowMeta>
                <RowMeta mono>{degree?.get(n.id) ?? '?'}</RowMeta>
              </Row>
            ))}
            {searchResults.length > SEARCH_LIMIT && (
              <div style={{ fontSize: FS.xs, color: C.textMuted, padding: `${SP.xs}px ${SP.sm}px`, textAlign: 'center' }}>
                + ещё {searchResults.length - SEARCH_LIMIT}
              </div>
            )}
          </div>
        )}

        {/* Сводка активных фильтров: контролы уехали в шапку, и без неё непонятно,
            почему на холсте не весь граф */}
        {filtersOn && (
          <div style={{
            display: 'flex', alignItems: 'center', gap: SP.xs,
            padding: `${SP.sm}px ${SP.md}px 0`, fontSize: FS.xs, color: C.textMuted,
          }}>
            <span style={{ flex: 1, minWidth: 0 }}>
              {[
                s.hideTestNodes ? `тесты скрыты · ${hiddenTestCount}` : null,
                s.hideOrphanNodes ? `сироты скрыты · ${hiddenOrphanCount}` : null,
                !allRelations ? `связи: ${GRAPH_RELATIONS.filter(r => s.filters[r]).length} из 3` : null,
              ].filter(Boolean).join(' · ')}
            </span>
            <LinkAction onClick={() => a.resetFilters()}>сбросить</LinkAction>
          </div>
        )}

        {/* Фокус — настройки окрестности выбранного типа. Нужны и без открытого
            документа: карта в панели показывает тот же фокус, и глубина с раскрытым
            хвостом «+N» управляют им ровно так же */}
        {s.selectedId && s.data && (
          <Section title="Фокус">
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
              <span style={{ fontSize: FS.sm, color: C.textPrimary, flex: 1 }}>Глубина 2</span>
              <Toggle checked={s.focusDepth2} onChange={() => a.toggleFocusDepth2()} width={36} height={21}
                ariaLabel="Показывать второе кольцо соседей" />
            </div>
            <p style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs, marginBottom: 0, lineHeight: 1.45 }}>
              Второе кольцо строится только для 6 самых связанных соседей — полная окрестность
              глубины 2 у крупного типа это сотни узлов.
            </p>
            {/* Раскрытый хвост: то, что не поместилось на холст и ушло в заглушку «+N» */}
            {s.focusTail && (
              <FocusTail graph={s.data} centerId={s.selectedId} side={s.focusTail}
                filters={s.filters} hideTests={s.hideTestNodes} degree={degree}
                onSelect={a.refocus}
                onClose={() => a.setFocusTail(null)} />
            )}
          </Section>
        )}

        {/* Легенда и god-узлы — сворачиваемая секция (по умолчанию свёрнута) */}
        <div>
          <button onClick={() => a.setLegendOpen(!s.legendOpen)} disabled={disabled}
            style={collapseHeadStyle}>
            <span style={sectionTitleStyle}>Легенда и god-узлы</span>
            <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
              style={{ marginLeft: 'auto', color: C.textMuted, transform: s.legendOpen ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s' }} />
          </button>
          {s.legendOpen && (
            <div style={{ padding: `0 ${SP.md}px ${SP.md}px` }}>
              {/* Легенда типов */}
              {(['Class', 'Interface', 'Struct', 'Enum'] as const).map(k => (
                <LegendRow key={k} color={KIND_RING[k]} glyph={KIND_GLYPH[k]} label={k} />
              ))}
              {/* god-узлы: порог minDegree=10 даёт 140 узлов, показываем топ-15 пока бэкенд не поправлен */}
              {s.data && s.data.godNodes.length > 0 && (
                <>
                  <div style={{ ...sectionTitleStyle, marginTop: SP.sm, marginBottom: SP.xs }}>
                    God-узлы <Dot color={C.accent} size={7} />
                  </div>
                  {s.data.godNodes.slice(0, GOD_LIMIT).map(id => {
                    const node = s.data!.nodes.find(n => n.id === id);
                    if (!node) return null;
                    return (
                      <Row key={id} onClick={() => a.select(id)} active={id === s.selectedId}>
                        <span style={{ width: 8, height: 8, borderRadius: R.full, background: C.accent, flexShrink: 0, boxShadow: `0 0 0 3px ${C.accentLight}` }} />
                        <RowName>{node.label}</RowName>
                        <RowMeta mono>{degree?.get(id) ?? 0}</RowMeta>
                      </Row>
                    );
                  })}
                  <p style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs, lineHeight: 1.5 }}>
                    Точки перегруза — кандидаты на разбиение.
                  </p>
                </>
              )}
            </div>
          )}
        </div>

        {/* Паспорт выбранного типа — главная секция инспектора */}
        <div style={{ padding: SP.md }}>
          <div style={sectionTitleStyle}>Паспорт типа</div>
          {selected ? (
            <Passport node={selected} graph={s.data!} onSelect={a.refocus} onOpenFile={onOpenFile} />
          ) : (
            <p style={{ fontSize: FS.sm, color: C.textMuted, textAlign: 'center', padding: `${SP.md}px ${SP.xs}px`, lineHeight: 1.5 }}>
              Выберите тип на карте или в поиске —<br />здесь появится его паспорт
            </p>
          )}
        </div>
      </div>
    </div>
  );
}

// === Хвост соседей фокуса ===
// Холст показывает 16 соседей на сторону, остальные — в заглушке «+N».
// Клик по заглушке раскрывает здесь ПОЛНЫЙ список стороны с переходом по клику.
function FocusTail({ graph, centerId, side, filters, hideTests, degree, onSelect, onClose }: {
  graph: CodeGraph;
  centerId: string;
  side: 'in' | 'out';
  filters: Record<CodeGraphRelation, boolean>;
  hideTests: boolean;
  degree?: Map<string, number>;
  onSelect: (id: string) => void;
  onClose: () => void;
}) {
  const list = useMemo(
    () => focusNeighbours(graph, centerId, side, { filters, hideTests, degree }),
    [graph, centerId, side, filters, hideTests, degree],
  );

  return (
    <div style={{ marginTop: SP.sm }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, marginBottom: SP.xs }}>
        <span style={sectionTitleStyle}>
          {side === 'in' ? 'Зависят от него' : 'От кого зависит он'} · {list.length}
        </span>
        <LinkAction onClick={onClose}>свернуть</LinkAction>
      </div>
      {list.length ? (
        <div style={{ maxHeight: 220, overflowY: 'auto' }}>
          {list.map(o => (
            <Row key={o.node.id} onClick={() => onSelect(o.node.id)}>
              <RowMeta>{side === 'in' ? '←' : '→'}</RowMeta>
              <Dot color={EDGE_COLOR[o.relations[0] ?? 'Calls']} />
              <RowName weight={400}>{o.node.label}</RowName>
              <RowMeta mono>{o.degree}</RowMeta>
            </Row>
          ))}
        </div>
      ) : <RelEmpty />}
    </div>
  );
}

// === Паспорт узла ===
function Passport({ node, graph, onSelect, onOpenFile }: {
  node: CodeGraphNode;
  graph: CodeGraph;
  onSelect: (id: string) => void;
  onOpenFile: (path: string, line?: number) => void;
}) {
  const isGod = graph.godNodes.includes(node.id);
  const outgoing = graph.edges.filter(e => e.source === node.id);
  const incoming = graph.edges.filter(e => e.target === node.id);

  const relLink = (e: CodeGraphEdge, out: boolean) => {
    const otherId = out ? e.target : e.source;
    const other = graph.nodes.find(n => n.id === otherId);
    if (!other) return null;
    return (
      <Row key={`${e.source}-${e.target}-${e.relation}`} onClick={() => onSelect(otherId)}>
        <RowMeta>{out ? '→' : '←'}</RowMeta>
        <Dot color={EDGE_COLOR[e.relation]} />
        <RowName weight={400}>{other.label}</RowName>
        <span style={{ fontSize: FS.xs, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.3px', fontStyle: e.confidence === 'Inferred' ? 'italic' : 'normal' }}>
          {RELATION_LABEL[e.relation]}{e.confidence === 'Inferred' ? ' · inferred' : ''}
        </span>
      </Row>
    );
  };

  return (
    <div>
      {/* kind-бейдж + god-метка */}
      <div style={{ display: 'flex', gap: SP.xs, alignItems: 'center', flexWrap: 'wrap' }}>
        <span style={{ ...kindBadgeStyle, background: EDGE_BG_forKind(node.kind), color: KIND_COLOR[node.kind] }}>
          <Dot color={KIND_COLOR[node.kind]} size={7} />{node.kind}
        </span>
        {isGod && (
          <span style={{ ...kindBadgeStyle, color: C.accent, background: C.accentLight, textTransform: 'none' }}>
            <Dot color={C.accent} size={7} />god-node
          </span>
        )}
      </div>
      <div style={{ fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading, marginTop: SP.xs }}>{node.label}</div>
      <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, marginTop: SP.xxs, wordBreak: 'break-all' }}>{node.fullyQualifiedName}</div>
      {/* Переход к исходнику — открываем на конкретной строке */}
      <div onClick={() => onOpenFile(node.sourceFile, parseSourceLine(node.sourceLocation))} title="Открыть во вкладке «Файлы»"
        style={{ display: 'flex', alignItems: 'flex-start', gap: SP.xs, fontFamily: FONT.mono, fontSize: FS.xs, color: C.info, background: C.infoBg, borderRadius: R.lg, padding: `${SP.xs}px ${SP.sm}px`, cursor: 'pointer', margin: `${SP.sm}px 0`, wordBreak: 'break-all' }}>
        <FileCode size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 1 }} />
        <span style={{ textDecoration: 'underline' }}>{node.sourceFile}:{node.sourceLocation}</span>
      </div>
      {/* Исходящие связи */}
      <div style={sectionTitleStyle}>Исходящие · {outgoing.length}</div>
      {outgoing.length ? outgoing.map(e => relLink(e, true)) : <RelEmpty />}
      {/* Входящие связи */}
      <div style={{ ...sectionTitleStyle, marginTop: SP.sm }}>Входящие · {incoming.length}</div>
      {incoming.length ? incoming.map(e => relLink(e, false)) : <RelEmpty />}
    </div>
  );
}

// === Мелкие презентационные куски ===

// Строка списка панели — одна на всех (результат поиска, god-узел, связь паспорта,
// сосед фокуса). Раньше у каждого из четырёх мест был свой почти одинаковый стиль
// и ни у одного не было наведения — строки не читались как кликабельные.
function Row({ onClick, active, children }: { onClick?: () => void; active?: boolean; children: React.ReactNode }) {
  const [hover, setHover] = useState(false);
  return (
    <div onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.xs,
        padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.lg, fontSize: FS.xs,
        cursor: onClick ? 'pointer' : 'default',
        background: active ? C.bgSelected : (hover ? C.bgInset : 'transparent'),
      }}>
      {children}
    </div>
  );
}

// Имя типа в строке — забирает свободное место и режется многоточием
function RowName({ children, weight = 600 }: { children: React.ReactNode; weight?: number }) {
  return (
    <span style={{
      fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: weight, color: C.textHeading,
      flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
    }}>{children}</span>
  );
}

// Приписка в строке: путь, связность, глиф вида
function RowMeta({ children, mono, style }: { children: React.ReactNode; mono?: boolean; style?: React.CSSProperties }) {
  return (
    <span style={{
      fontSize: FS.xs, color: C.textMuted, flexShrink: 0,
      ...(mono ? { fontFamily: FONT.mono } : null), ...style,
    }}>{children}</span>
  );
}

// Пункт меню-тумблера: галка слева от подписи появляется у включённого
function CheckLabel({ on, children }: { on: boolean; children: React.ReactNode }) {
  return (
    <span style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm, flex: 1 }}>
      <span>{children}</span>
      {on && <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={C.accent} />}
    </span>
  );
}

function Section({ title, aside, children }: { title?: string; aside?: React.ReactNode; children: React.ReactNode }) {
  return (
    <div style={{ padding: `${SP.md}px ${SP.md}px 0` }}>
      {title && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, marginBottom: SP.sm }}>
          <span style={sectionTitleStyle}>{title}</span>
          {aside}
        </div>
      )}
      {children}
    </div>
  );
}

function LegendRow({ color, glyph, label }: { color: string; glyph: string; label: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.xs, color: C.textSecondary, padding: `${SP.xxs}px 0` }}>
      <span style={{ width: 13, height: 13, borderRadius: R.full, border: `2px solid ${color}`, background: C.bgCard, flexShrink: 0 }} />
      <span style={{ fontFamily: FONT.mono, fontWeight: 600, color, width: 12 }}>{glyph}</span>
      <span>{label}</span>
    </div>
  );
}

function LinkAction({ onClick, children }: { onClick: () => void; children: React.ReactNode }) {
  return (
    <button onClick={onClick} style={{ marginLeft: 'auto', border: 'none', background: 'none', cursor: 'pointer', fontSize: FS.xs, color: C.textMuted, fontFamily: 'inherit', padding: 0 }}>
      {children}
    </button>
  );
}

function RelEmpty() {
  return <div style={{ fontSize: FS.xs, color: C.textMuted, padding: `${SP.xxs}px ${SP.sm}px` }}>нет</div>;
}

// Фон kind-бейджа — soft-подложка под цвет типа (для контраста цветной точки)
function EDGE_BG_forKind(kind: keyof typeof KIND_COLOR): string {
  if (kind === 'Interface') return EDGE_BG.Calls;       // info
  if (kind === 'Struct') return EDGE_BG.Implements;     // success
  if (kind === 'Enum') return EDGE_BG.References;       // plan
  return C.bgSelected;                                   // Class — нейтральный
}

// Парсинг sourceLocation ("line 6" / "6:12" / "6") → номер строки или undefined
function parseSourceLine(loc: string): number | undefined {
  if (!loc) return undefined;
  const m = loc.match(/(?:line\s*)?(\d+)/);
  return m ? parseInt(m[1], 10) : undefined;
}

const SEARCH_LIMIT = 20;
const GOD_LIMIT = 15;

// === Общие стили секций (inline) ===
const sectionTitleStyle: React.CSSProperties = {
  fontSize: FS.xs, textTransform: 'uppercase', letterSpacing: '0.6px',
  color: C.textMuted, fontWeight: 600,
};
const collapseHeadStyle: React.CSSProperties = {
  width: '100%', display: 'flex', alignItems: 'center', gap: SP.xs, cursor: 'pointer',
  padding: `${SP.md}px ${SP.md}px ${SP.sm}px`, background: 'transparent', border: 'none', fontFamily: 'inherit',
};
const kindBadgeStyle: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: SP.xs, fontSize: FS.xs, fontWeight: 600,
  textTransform: 'uppercase', letterSpacing: '0.5px', padding: `${SP.xxs}px ${SP.sm}px`, borderRadius: R.sm,
};

// Панель «Граф» в правой рельсе инструментов — инспектор отображения графа:
// поиск типа, фильтр по типу связи (чипы-тогглы), сворачиваемая легенда типов и
// god-узлов, паспорт выбранного типа. Деградирует при empty (только сборка),
// контролы блокируются при loading. Сама панель живёт в PanelShell рельсы
// (RightPanelStack), который даёт шапку острова и кнопку закрытия — здесь только тело.
import { useEffect, useMemo } from 'react';
import { Search, ChevronRight, FileCode, RefreshCw, AlertTriangle, Loader } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Button, Dot, IconField, EmptyState, WaitingIndicator, Toggle } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useCodeGraph, useCodeGraphActions, GRAPH_RELATIONS } from '../../lib/codeGraph';
import { layoutGraph } from './graphLayout';
import { focusNeighbours, isTestSourceFile } from './graphFocus';
import {
  EDGE_COLOR, EDGE_BG, KIND_COLOR, KIND_RING, KIND_GLYPH, RELATION_LABEL,
} from './graphTokens';
import type { CodeGraphRelation, CodeGraphNode, CodeGraph, CodeGraphEdge } from '../../types';

interface Props {
  projectId: string;
  // Открыт ли сейчас документ графа в центре — кнопка «Показать граф в центре»
  // нужна, только когда он закрыт (по макету сценария «граф закрыт»)
  graphOpen?: boolean;
  // Открыть документ графа в центре (если закрыт). Любое режимное действие панели
  // (фильтр, god-узел, поиск, переход в паспорте) открывает документ — правило макета.
  onEnsureGraphOpen: () => void;
  onOpenFile: (path: string, line?: number) => void;
  onBuild: () => void;
}

export function CodeGraphPanel({ projectId, graphOpen, onEnsureGraphOpen, onOpenFile, onBuild }: Props) {
  const s = useCodeGraph();
  const a = useCodeGraphActions();

  // Первичная загрузка при монтировании панели. loadCodeGraph идемпотентна —
  // безопасно вызывать на каждом рендере, сетевых дублей не будет
  useEffect(() => { a.load(projectId); }, [a, projectId]);

  const layout = useMemo(() => (s.data ? layoutGraph(s.data) : null), [s.data]);

  // Счётчики рёбер по типу связи — для подписи чипов
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
    if (!s.query.trim() || !s.data || !layout) return [];
    const q = s.query.trim().toLowerCase();
    return s.data.nodes.filter(n =>
      n.label.toLowerCase().includes(q) || n.fullyQualifiedName.toLowerCase().includes(q)
    );
  }, [s.query, s.data, layout]);

  // Счётчики скрытых узлов для подписи под фильтрами
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

  // Режимное действие (меняет вид графа) → помимо эффекта, открывает документ в центре
  const withEnsureOpen = (fn: () => void) => () => { fn(); onEnsureGraphOpen(); };

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
        icon={<AlertTriangle size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Не удалось загрузить"
        subtitle={s.error ?? 'Повторите позже.'}
        action={<Button variant="secondary" size="sm" fullWidth onClick={() => a.load(projectId, true)}>Повторить</Button>}
      />
    );
  }

  return (
    <div style={{
      display: 'flex', flexDirection: 'column', minHeight: 0, flex: 1,
      opacity: disabled ? 0.45 : 1, pointerEvents: disabled ? 'none' : 'auto',
    }}>
      {/* Stale-предупреждение: граф может отставать от кода */}
      {s.data?.metadata.isStale && (
        <div style={{ padding: SP.sm, background: C.warningBg, borderBottom: `1px solid ${C.borderLight}` }}>
          <div style={{ display: 'flex', gap: 6, alignItems: 'flex-start', fontSize: FS.xs, color: C.warningText, lineHeight: 1.45 }}>
            <AlertTriangle size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0, marginTop: 1 }} />
            <span>Код изменился после сборки — граф может отставать.</span>
          </div>
          <Button variant="primary" size="sm" fullWidth style={{ marginTop: SP.sm }}
            onClick={() => a.build(projectId)}
            leftIcon={<RefreshCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}>
            Перестроить
          </Button>
        </div>
      )}

      {/* Поиск типа */}
      <Section>
        <IconField
          icon={<Search size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          value={s.query}
          onChange={v => { a.setQuery(v); if (v) onEnsureGraphOpen(); }}
          placeholder="Поиск типа…"
          height={34}
          radius={R.xl}
          fontSize={FS.sm}
        />
        {/* Список результатов поиска — до 20, остальные счётчиком */}
        {s.query.trim() && searchResults.length > 0 && (
          <div style={{ marginTop: SP.sm, maxHeight: 240, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 2 }}>
            {searchResults.slice(0, SEARCH_LIMIT).map(n => (
              <div key={n.id} onClick={() => { a.select(n.id); onEnsureGraphOpen(); }}
                style={searchHitStyle}>
                <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 600, color: C.textHeading, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{n.label}</span>
                <span style={{ fontSize: 10, color: C.textMuted, fontFamily: FONT.mono, flexShrink: 0 }}>{KIND_GLYPH[n.kind]}</span>
                <span style={{ fontSize: 10, color: C.textMuted, flexShrink: 0, maxWidth: 100, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{n.sourceFile}:{n.sourceLocation}</span>
                <span style={{ fontSize: 10, color: C.textMuted, fontFamily: FONT.mono, flexShrink: 0 }}>{layout?.degree.get(n.id) ?? '?'}</span>
              </div>
            ))}
            {searchResults.length > SEARCH_LIMIT && (
              <div style={{ fontSize: FS.xs, color: C.textMuted, padding: '4px 8px', textAlign: 'center' }}>
                + ещё {searchResults.length - SEARCH_LIMIT}
              </div>
            )}
          </div>
        )}
        {s.query.trim() && searchResults.length === 0 && (
          <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.sm, padding: '4px 0' }}>
            Ничего не найдено
          </div>
        )}
      </Section>

      {/* Фильтры отображения — чипы-тогглы */}
      <Section title="Фильтры">
        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
          <Button variant={s.hideTestNodes ? 'ghostFilled' : 'ghost'} size="sm" pill
            style={{ padding: '4px 10px', minHeight: 26, fontSize: FS.xs }}
            onClick={withEnsureOpen(() => a.toggleHideTestNodes())}>
            <span style={{ color: s.hideTestNodes ? C.textHeading : C.textMuted }}>скрыть тесты</span>
          </Button>
          <Button variant={s.hideOrphanNodes ? 'ghostFilled' : 'ghost'} size="sm" pill
            style={{ padding: '4px 10px', minHeight: 26, fontSize: FS.xs }}
            onClick={withEnsureOpen(() => a.toggleHideOrphanNodes())}>
            <span style={{ color: s.hideOrphanNodes ? C.textHeading : C.textMuted }}>скрыть сироты</span>
          </Button>
        </div>
        {/* Подпись сколько скрыто */}
        {s.data && (s.hideTestNodes || s.hideOrphanNodes) && (
          <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: SP.xs }}>
            {[s.hideTestNodes ? hiddenTestCount : null, s.hideOrphanNodes ? hiddenOrphanCount : null]
              .filter(Boolean).join(' · ')}
          </div>
        )}
      </Section>

      {/* Связи — строка чипов-тогглов */}
      <Section
        title="Связи"
        aside={!(s.filters.Calls && s.filters.Implements && s.filters.References)
          ? <LinkAction onClick={() => a.resetFilters()}>сбросить</LinkAction> : undefined}
      >
        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
          {GRAPH_RELATIONS.map(rel => {
            const on = s.filters[rel];
            return (
              <Button key={rel} variant={on ? 'ghostFilled' : 'ghost'} size="sm" pill
                style={{ padding: '4px 10px', minHeight: 26, fontSize: FS.xs }}
                leftIcon={<Dot color={on ? EDGE_COLOR[rel] : C.textMuted} />}
                onClick={withEnsureOpen(() => a.toggleFilter(rel))}>
                <span style={{ color: on ? C.textHeading : C.textMuted }}>{RELATION_LABEL[rel]}</span>
                <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, marginLeft: 2 }}>{relCounts[rel]}</span>
              </Button>
            );
          })}
        </div>
      </Section>

      {/* Фокус холста — появляется, когда узел выбран (холст показывает его окрестность) */}
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
              filters={s.filters} hideTests={s.hideTestNodes} degree={layout?.degree}
              onSelect={id => { a.select(id); onEnsureGraphOpen(); }}
              onClose={() => a.setFocusTail(null)} />
          )}
        </Section>
      )}

      {/* Легенда и god-узлы — сворачиваемая секция (по умолчанию свёрнута) */}
      <div style={{ borderBottom: `1px solid ${C.borderLight}` }}>
        <button onClick={() => a.setLegendOpen(!s.legendOpen)} disabled={disabled}
          style={collapseHeadStyle}>
          <span style={sectionTitleStyle}>Легенда и god-узлы</span>
          <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE}
            style={{ marginLeft: 'auto', color: C.textMuted, transform: s.legendOpen ? 'rotate(90deg)' : 'none', transition: 'transform 0.15s' }} />
        </button>
        {s.legendOpen && (
          <div style={{ padding: `${SP.xs} ${SP.md} ${SP.md}` }}>
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
                {s.data.godNodes.slice(0, 15).map(id => {
                  const ln = layout?.byId.get(id);
                  const node = ln?.node ?? s.data!.nodes.find(n => n.id === id);
                  if (!node) return null;
                  const deg = layout?.degree.get(id) ?? 0;
                  return (
                    <div key={id} onClick={withEnsureOpen(() => a.select(id))} style={godItemStyle}>
                      <span style={{ width: 8, height: 8, borderRadius: R.full, background: C.accent, flexShrink: 0, boxShadow: `0 0 0 3px ${C.accentLight}` }} />
                      <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 600, color: C.textHeading, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{node.label}</span>
                      <span style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.mono }}>{deg}</span>
                    </div>
                  );
                })}
                <p style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 6, lineHeight: 1.5 }}>
                  Точки перегруза — кандидаты на разбиение.
                </p>
              </>
            )}
          </div>
        )}
      </div>

      {/* Паспорт выбранного типа — нижняя (и главная) секция */}
      <div style={{ padding: SP.md, flex: 1, minHeight: 0, overflowY: 'auto' }}>
        <div style={sectionTitleStyle}>Паспорт типа</div>
        {selected ? (
          <Passport node={selected} graph={s.data!} onSelect={id => { a.select(id); onEnsureGraphOpen(); }} onOpenFile={onOpenFile} />
        ) : (
          <p style={{ fontSize: FS.sm, color: C.textMuted, textAlign: 'center', padding: `${SP.md} ${SP.xs}`, lineHeight: 1.5 }}>
            Выберите узел на графе —<br />здесь появится его паспорт
          </p>
        )}
      </div>

      {/* Явное открытие документа — только когда граф в центре закрыт
          (по макету сценария «граф закрыт»: секция «Документ», кнопка во всю ширину
          с заливкой и рамкой — ghostFilled, системный аналог макетной .btn-secondary) */}
      {!graphOpen && (
        <div style={{ padding: SP.md, borderTop: `1px solid ${C.borderLight}` }}>
          <div style={{ ...sectionTitleStyle, marginBottom: SP.sm }}>Документ</div>
          <Button variant="ghostFilled" size="sm" fullWidth onClick={onEnsureGraphOpen}>
            Показать граф в центре
          </Button>
        </div>
      )}
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
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: SP.xs }}>
        <span style={sectionTitleStyle}>
          {side === 'in' ? 'Зависят от него' : 'От кого зависит он'} · {list.length}
        </span>
        <LinkAction onClick={onClose}>свернуть</LinkAction>
      </div>
      {list.length ? (
        <div style={{ maxHeight: 220, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 2 }}>
          {list.map(o => (
            <div key={o.node.id} onClick={() => onSelect(o.node.id)} style={searchHitStyle}>
              <span style={{ color: C.textMuted, fontSize: FS.xs, flexShrink: 0 }}>{side === 'in' ? '←' : '→'}</span>
              <Dot color={EDGE_COLOR[o.relations[0] ?? 'Calls']} />
              <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textHeading, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{o.node.label}</span>
              <span style={{ fontFamily: FONT.mono, fontSize: 10, color: C.textMuted, flexShrink: 0 }}>{o.degree}</span>
            </div>
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
      <div key={`${e.source}-${e.target}-${e.relation}`} onClick={() => onSelect(otherId)} style={relLinkStyle}>
        <span style={{ color: C.textMuted, fontSize: FS.xs, flexShrink: 0 }}>{out ? '→' : '←'}</span>
        <Dot color={EDGE_COLOR[e.relation]} />
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textHeading, flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{other.label}</span>
        <span style={{ fontSize: FS.xs, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.3px', fontStyle: e.confidence === 'Inferred' ? 'italic' : 'normal' }}>
          {RELATION_LABEL[e.relation]}{e.confidence === 'Inferred' ? ' · inferred' : ''}
        </span>
      </div>
    );
  };

  return (
    <div>
      {/* kind-бейдж + god-метка */}
      <div style={{ display: 'flex', gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
        <span style={{ ...kindBadgeStyle, background: EDGE_BG_forKind(node.kind), color: KIND_COLOR[node.kind] }}>
          <Dot color={KIND_COLOR[node.kind]} size={7} />{node.kind}
        </span>
        {isGod && (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4, fontSize: FS.xs, fontWeight: 600, color: C.accent, background: C.accentLight, padding: '2px 8px', borderRadius: R.sm }}>
            <Dot color={C.accent} size={7} />god-node
          </span>
        )}
      </div>
      <div style={{ fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, color: C.textHeading, marginTop: 6 }}>{node.label}</div>
      <div style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, marginTop: 3, wordBreak: 'break-all' }}>{node.fullyQualifiedName}</div>
      {/* Переход к исходнику — открываем на конкретной строке */}
      <div onClick={() => onOpenFile(node.sourceFile, parseSourceLine(node.sourceLocation))} title="Открыть во вкладке «Файлы»"
        style={{ display: 'flex', alignItems: 'flex-start', gap: 7, fontFamily: FONT.mono, fontSize: FS.xs, color: C.info, background: C.infoBg, borderRadius: R.lg, padding: '7px 9px', cursor: 'pointer', margin: '8px 0', wordBreak: 'break-all' }}>
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
function Section({ title, aside, children }: { title?: string; aside?: React.ReactNode; children: React.ReactNode }) {
  return (
    <div style={{ padding: SP.md, borderBottom: `1px solid ${C.borderLight}` }}>
      {title && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: SP.sm }}>
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
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.xs, color: C.textSecondary, padding: '3px 0' }}>
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
  return <div style={{ fontSize: FS.xs, color: C.textMuted, padding: `2px 7px` }}>нет</div>;
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

// === Общие стили секций (inline) ===
const sectionTitleStyle: React.CSSProperties = {
  fontSize: FS.xs, textTransform: 'uppercase', letterSpacing: '0.6px',
  color: C.textMuted, fontWeight: 600,
};
const collapseHeadStyle: React.CSSProperties = {
  width: '100%', display: 'flex', alignItems: 'center', gap: 6, cursor: 'pointer',
  padding: `${SP.sm} ${SP.md}`, background: 'transparent', border: 'none', fontFamily: 'inherit',
};
const godItemStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: SP.sm, padding: '6px 8px',
  borderRadius: R.lg, cursor: 'pointer', fontSize: FS.sm,
};
const relLinkStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 7, padding: '4px 7px',
  borderRadius: R.lg, fontSize: FS.xs, cursor: 'pointer',
};
const kindBadgeStyle: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: FS.xs, fontWeight: 600,
  textTransform: 'uppercase', letterSpacing: '0.5px', padding: '2px 8px', borderRadius: R.sm,
};
const searchHitStyle: React.CSSProperties = {
  display: 'flex', alignItems: 'center', gap: 6, padding: '5px 8px',
  borderRadius: R.lg, cursor: 'pointer', fontSize: FS.xs,
};

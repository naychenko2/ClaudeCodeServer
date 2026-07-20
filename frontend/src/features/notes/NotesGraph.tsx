import { useEffect, useMemo, useState } from 'react';
import type { NoteGraph } from '../../types';
import { api } from '../../lib/api';
import { useNotesVersion } from '../../lib/notes';
import { C, FONT } from '../../lib/design';
import { sourceColor } from './shared';
import { useGraphSettings, type GraphSettings } from './graph/graphSettings';
import { filterGraph } from './graph/graphFilter';
import { matchNode, parseQuery } from './graph/graphQuery';
import { useForceSimulation } from './graph/useForceSimulation';
import { useThemeColors } from './graph/useThemeColors';
import { GraphCanvas } from './graph/GraphCanvas';
import { GraphSettingsPanel } from './graph/GraphSettingsPanel';

export interface GraphStats { shown: number; total: number; edges: number }

// Граф заметок в стиле Obsidian Graph View: живая d3-force симуляция на canvas
// + настройки (фильтры/группы/отображение/силы). Один компонент на оба режима:
// глобальный (вся база) и локальный (focusId + окрестность по глубине).
//
// Настройки могут управляться извне (controlled — через settings/onSettingsChange,
// когда их рендерит внешний сайдбар раздела) или жить внутри (uncontrolled —
// локальный граф в карточке заметки). В uncontrolled-режиме показывается
// плавающая панель-шестерёнка; в controlled — её прячет hidePanel.
export function NotesGraph({
  selectedId, onSelectNode, focusId, maxNodes, settingsKey,
  settings: settingsProp, onSettingsChange, hidePanel, onStats,
}: {
  selectedId: string | null;
  onSelectNode: (id: string) => void;
  focusId?: string;      // локальный режим: заметка и её окрестность
  maxNodes?: number;     // ограничение числа узлов (мобильный): топ-N по числу связей
  settingsKey?: string;  // ключ персиста настроек для uncontrolled-режима
  settings?: GraphSettings;                                   // controlled: настройки извне
  onSettingsChange?: (updater: (s: GraphSettings) => GraphSettings) => void;
  hidePanel?: boolean;   // не показывать плавающую панель (настройки в внешнем сайдбаре)
  onStats?: (s: GraphStats) => void;   // статистика узлов/связей для внешнего сайдбара
}) {
  const version = useNotesVersion();
  const [graph, setGraph] = useState<NoteGraph | null>(null);
  // Внутренние настройки нужны только в uncontrolled-режиме; при controlled key=null
  // отключает persist, чтобы не конфликтовать с владельцем состояния.
  const [ownSettings, setOwnSettings] = useGraphSettings(
    settingsProp ? null : (settingsKey ?? (focusId ? 'cc_graph_local' : 'cc_graph_global')),
  );
  const settings = settingsProp ?? ownSettings;
  const setSettings = onSettingsChange ?? setOwnSettings;
  const colors = useThemeColors();

  useEffect(() => {
    let alive = true;
    api.notes.graph(settings.filters.showComments).then(g => { if (alive) setGraph(g); }).catch(() => {});
    return () => { alive = false; };
  }, [version, settings.filters.showComments]);

  const filtered = useMemo(
    () => graph ? filterGraph(graph, { filters: settings.filters, focusId, maxNodes }) : null,
    [graph, settings.filters, focusId, maxNodes],
  );

  // Статистика наверх (для внешнего сайдбара): показано из всего + связей
  useEffect(() => {
    if (graph && filtered && onStats)
      onStats({ shown: filtered.nodes.length, total: graph.nodes.length, edges: filtered.edges.length });
  }, [graph, filtered, onStats]);

  const simApi = useForceSimulation(filtered, settings.forces);

  // Радиус узла: от числа связей и слайдера размера; влияет на forceCollide
  useEffect(() => {
    const ns = settings.display.nodeSize;
    for (const n of simApi.nodes()) {
      const base = ns * (4 + 2.5 * Math.sqrt(n.degree ?? 0));
      const r = Math.max(3 * ns, Math.min(18 * ns, base));
      n.r = focusId && n.id === focusId ? r * 1.4 : r;
    }
    simApi.refreshCollide();
  }, [filtered, settings.display.nodeSize, focusId, simApi]);

  // Цвет узла: первая подошедшая группа, иначе цвет источника; резолвим токены для canvas
  const groupRules = useMemo(
    () => settings.groups
      .map(g => ({ terms: parseQuery(g.query), color: g.color }))
      .filter(g => g.terms.length > 0),
    [settings.groups],
  );
  const paintKey = useMemo(() => ({}), [filtered, groupRules, colors, settings.display.nodeSize]);
  useEffect(() => {
    for (const n of simApi.nodes()) {
      const rule = groupRules.find(g => matchNode(n, g.terms));
      // Комментарии красятся статусом (охра — открыт, зелёный — решён), ответы — приглушённо
      const kindColor = n.kind === 'comment'
        ? (n.status === 'open' ? C.warning : C.success)
        : n.kind === 'reply' ? C.textMuted : null;
      n.color = colors.resolve(rule ? rule.color : kindColor ?? sourceColor(n.source));
    }
  }, [paintKey, simApi, groupRules, colors]);

  // Источники и теги для панели фильтров — из сырого графа (не из отфильтрованного)
  const sources = useMemo(() => {
    const m = new Map<string, string>();
    graph?.nodes.forEach(n => { if (!n.ghost) m.set(n.source, n.sourceLabel); });
    return [...m.entries()].map(([key, label]) => ({ key, label }));
  }, [graph]);
  const tags = useMemo(() => {
    const s = new Set<string>();
    graph?.nodes.forEach(n => n.tags?.forEach(t => s.add(t)));
    return [...s].sort((a, b) => a.localeCompare(b, 'ru'));
  }, [graph]);

  if (!graph) return <div style={box}>Загрузка графа…</div>;
  if (graph.nodes.length === 0) return <div style={box}>Нет заметок для графа</div>;

  return (
    <div style={{ position: 'relative', height: '100%', overflow: 'hidden' }}>
      <GraphCanvas
        api={simApi}
        display={settings.display}
        selectedId={selectedId}
        focusId={focusId}
        colors={colors}
        onSelectNode={onSelectNode}
        redrawKey={paintKey}
      />
      {filtered && filtered.nodes.length === 0 && (
        <div style={{ ...box, position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', pointerEvents: 'none' }}>
          Ничего не подошло под фильтры
        </div>
      )}
      {!hidePanel && (
        <GraphSettingsPanel
          settings={settings}
          onChange={setSettings}
          sources={sources}
          tags={tags}
          localMode={!!focusId}
        />
      )}
    </div>
  );
}

const box: React.CSSProperties = { padding: 40, textAlign: 'center', color: C.textMuted, fontFamily: FONT.sans, fontSize: 13 };

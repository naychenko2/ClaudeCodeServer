// SVG-холсты Code Graph: «Фокус» (окрестность одного типа) и «Обзор» (группы
// неймспейсов по слоям). Полного графа больше нет — на 1020 узлах он был нечитаем
// (шаг между соседями кольца 1.3 px) и как точка входа ничего не давал выбрать; теперь
// «Фокус» — не отдельный режим, а место, куда приводит навигация из «Обзора» (см.
// CodeGraphDocument/lib/codeGraph.ts — единая цепочка крошек вместо тумблера режимов).
// Раскладки — из graphFocus.ts/graphOverview.ts, без внешних графовых библиотек.
import { useEffect, useState, type MouseEvent, type ReactNode } from 'react';
import { C, FONT, FS } from '../../lib/design';
import type { CodeGraphRelation } from '../../types';
import type { FocusModel, FocusSide } from './graphFocus';
import { EDGE_COLOR, KIND_RING, KIND_COLOR, KIND_GLYPH } from './graphTokens';
import type { OverviewScene, OverviewLayout, OverviewItem } from './graphOverview';
import { bundleWidth } from './graphOverview';

// Переход между сценами (Обзор ↔ Фокус, перефокус на соседа): узел «уезжает в центр,
// вокруг разворачивается окрестность» — масштаб + прозрачность за ~200мс. key меняется
// на каждый содержательный шаг навигации → React перемонтирует группу → анимация
// начинается заново из исходного (0.94/0) состояния без ручного управления rAF/таймерами.
// prefers-reduced-motion — переход мгновенный (entered сразу true, transition снят).
function SceneTransition({ animKey, children }: { animKey: string; children: ReactNode }) {
  return <SceneTransitionInner key={animKey}>{children}</SceneTransitionInner>;
}

function SceneTransitionInner({ children }: { children: ReactNode }) {
  const reduceMotion = typeof window !== 'undefined'
    && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
  const [entered, setEntered] = useState(reduceMotion);
  useEffect(() => {
    if (reduceMotion) return;
    const raf = requestAnimationFrame(() => setEntered(true));
    return () => cancelAnimationFrame(raf);
  }, [reduceMotion]);
  return (
    <g style={{
      opacity: entered ? 1 : 0,
      transform: entered ? 'scale(1)' : 'scale(0.94)',
      transformOrigin: '50% 50%',
      transition: reduceMotion ? undefined : 'opacity 200ms ease-out, transform 200ms ease-out',
    }}>
      {children}
    </g>
  );
}

// === «Фокус»: центр + соседи по сторонам ===
// Координаты приходят готовыми из buildFocusModel — здесь только отрисовка и клики.
// onRefocus — клик по соседу/центру (перефокус, дописывает шаг в конец цепочки крошек).
// onClear — клик по пустому холсту (полный сброс к корню «Обзора»).
export function CodeGraphFocusCanvas({ focus, onRefocus, onClear, onExpandTail }: {
  focus: FocusModel;
  onRefocus: (id: string) => void;
  onClear: () => void;
  onExpandTail?: (side: FocusSide) => void;
}) {
  const { viewW, viewH, mobile } = focus;

  const handleBackdropClick = (e: MouseEvent<SVGSVGElement>) => {
    if (e.target === e.currentTarget) onClear();
  };

  return (
    <svg
      viewBox={`0 0 ${viewW} ${viewH}`}
      preserveAspectRatio="xMidYMid meet"
      onClick={handleBackdropClick}
      style={{ width: '100%', height: '100%', display: 'block' }}
    >
      <SceneTransition animKey={focus.center.id}>
        {/* Рёбра центра и второго кольца */}
        <g>
          {focus.edges.map((e, i) => (
            <path key={i}
              d={`M${e.x1},${e.y1} Q${e.cx},${e.cy} ${e.x2},${e.y2}`}
              fill="none"
              stroke={e.relation ? EDGE_COLOR[e.relation] : C.dashed}
              strokeWidth={e.width}
              strokeDasharray={e.relation ? undefined : '4 3'}
              opacity={e.relation ? 0.75 : 0.9}
              pointerEvents="none"
            />
          ))}
        </g>

        {/* Узлы: центр крупнее и с полной подписью, соседи — с подписью наружу */}
        <g>
          {focus.nodes.map(p => {
            const main = p.side === 'center';
            const ring = main || p.isGod ? C.accent : KIND_RING[p.node.kind];
            const glyphFill = p.node.kind === 'Class' ? C.textHeading : KIND_COLOR[p.node.kind];
            // Подпись соседа уходит наружу по горизонтали — так они не наезжают
            // друг на друга и на линии; на мобиле раскладка вертикальная, подпись снизу.
            // У второго кольца подпись всегда под кружком: он стоит у края холста
            const outward = !main && !mobile && !p.second;
            return (
              <g key={p.node.id} transform={`translate(${p.x.toFixed(1)},${p.y.toFixed(1)})`}
                opacity={p.second ? 0.85 : 1}
                style={{ cursor: 'pointer' }}>
                <circle r={Math.max(p.r + 10, 20)} fill="transparent"
                  onClick={ev => { ev.stopPropagation(); onRefocus(p.node.id); }} />
                {p.isGod && !main && (
                  <circle r={p.r + 6} fill="none" stroke={C.accent} strokeWidth={2}
                    strokeDasharray="3 3" opacity={0.5} pointerEvents="none" />
                )}
                <circle r={p.r} fill={C.bgCard} stroke={ring}
                  strokeWidth={main ? 3.5 : 2.4} pointerEvents="none" />
                <text textAnchor="middle" dominantBaseline="central"
                  fontFamily={FONT.mono} fontSize={main ? FS.md : FS.xs} fontWeight={600}
                  fill={glyphFill} pointerEvents="none">{KIND_GLYPH[p.node.kind]}</text>
                <text
                  textAnchor={outward ? (p.side === 'in' ? 'end' : 'start') : 'middle'}
                  x={outward ? (p.side === 'in' ? -(p.r + 8) : p.r + 8) : 0}
                  y={outward ? 4 : p.r + 14}
                  fontFamily={FONT.mono}
                  fontSize={main ? FS.base : FS.xs}
                  fontWeight={main ? 600 : 400}
                  fill={main ? C.accent : C.textSecondary}
                  stroke={C.bgCard} strokeWidth={3} paintOrder="stroke"
                  pointerEvents="none">
                  {p.label}
                </text>
                {main && (
                  <text textAnchor="middle" y={p.r + 29} fontFamily={FONT.mono} fontSize={FS.xs}
                    fill={C.textMuted} stroke={C.bgCard} strokeWidth={3} paintOrder="stroke"
                    pointerEvents="none">
                    {focus.centerDegree} связей
                  </text>
                )}
              </g>
            );
          })}
        </g>

        {/* Заглушки хвоста: остаток соседей раскрывается списком в панели */}
        <g>
          {focus.stubs.map(stub => (
            <g key={stub.side} transform={`translate(${stub.x.toFixed(1)},${stub.y.toFixed(1)})`}
              style={{ cursor: 'pointer' }}
              onClick={ev => { ev.stopPropagation(); onExpandTail?.(stub.side); }}>
              <circle r={22} fill={C.bgCard} stroke={C.dashed} strokeWidth={2} strokeDasharray="4 3" />
              <text textAnchor="middle" dominantBaseline="central" fontFamily={FONT.mono}
                fontSize={FS.xs} fill={C.textMuted} pointerEvents="none">+{stub.hidden}</text>
              <text textAnchor="middle" y={38} fontFamily={FONT.mono} fontSize={FS.xs}
                fill={C.textMuted} stroke={C.bgCard} strokeWidth={3} paintOrder="stroke"
                pointerEvents="none">{mobile ? 'список' : 'ещё в списке'}</text>
            </g>
          ))}
        </g>

        {/* Подписи сторон — что слева, что справа (без них раскладку нужно угадывать) */}
        <g pointerEvents="none">
          {mobile ? (
            <>
              <text x={viewW / 2} y={20} textAnchor="middle" fontFamily={FONT.sans} fontSize={FS.xs}
                fill={C.textMuted} letterSpacing="0.6px">ЗАВИСЯТ ОТ НЕГО · {focus.incoming.length}</text>
              <text x={viewW / 2} y={viewH - 6} textAnchor="middle" fontFamily={FONT.sans} fontSize={FS.xs}
                fill={C.textMuted} letterSpacing="0.6px">ОТ КОГО ЗАВИСИТ ОН · {focus.outgoing.length}</text>
            </>
          ) : (
            <>
              <text x={24} y={26} fontFamily={FONT.sans} fontSize={FS.xs} fill={C.textMuted}
                letterSpacing="0.6px">← ЗАВИСЯТ ОТ НЕГО · {focus.incoming.length}</text>
              <text x={viewW - 24} y={26} textAnchor="end" fontFamily={FONT.sans} fontSize={FS.xs}
                fill={C.textMuted} letterSpacing="0.6px">ОТ КОГО ЗАВИСИТ ОН · {focus.outgoing.length} →</text>
            </>
          )}
        </g>
      </SceneTransition>
    </svg>
  );
}

// === «Обзор»: граф групп неймспейсов по слоям зависимостей ===
// Координаты и слои приходят готовыми из buildOverviewScene/layoutOverview —
// здесь только отрисовка и клики. Обратные пучки (нарушение слоистости) —
// пунктир в C.warning, толщина пучка — по логарифму веса (bundleWidth).
function clipOverviewLabel(label: string, max: number): string {
  return label.length > max ? `${label.slice(0, max - 1)}…` : label;
}

export function CodeGraphOverviewCanvas({ scene, layout, animKey, onItemClick, onItemDblClick }: {
  scene: OverviewScene;
  layout: OverviewLayout;
  animKey: string;
  onItemClick: (item: OverviewItem) => void;
  onItemDblClick: (item: OverviewItem) => void;
}) {
  const { viewW, viewH, mobile } = layout;
  const maxLabel = mobile ? 11 : 16;

  return (
    <svg viewBox={`0 0 ${viewW} ${viewH}`} preserveAspectRatio="xMidYMid meet"
      style={{ width: '100%', height: '100%', display: 'block' }}>
      <SceneTransition animKey={animKey}>
        {/* Подложки слоёв — чередующийся фон + подпись слоя слева */}
        <g pointerEvents="none">
          {layout.rows.map((row, i) => (
            <g key={row.layer}>
              {i % 2 === 1 && (
                <rect x={8} y={row.y0} width={viewW - 16} height={row.y1 - row.y0} rx={12}
                  fill={C.bgPanel} opacity={0.5} />
              )}
              <text x={18} y={row.y0 + 14} fontFamily={FONT.sans} fontSize={FS.xs}
                fill={C.textMuted} letterSpacing="0.6px">{row.title.toUpperCase()}</text>
            </g>
          ))}
        </g>

        {/* Рёбра-пучки: агрегированы между элементами, толщина — по логарифму веса.
            Обратные (нарушение слоистости — источник ниже приёмника) — пунктир C.warning.
            pointerEvents="none": иначе пучок, проходящий через центр узла, перехватывает
            клик — путь имеет собственную геометрию, а прозрачный hit-target узла под ним */}
        <g pointerEvents="none">
          {scene.bundles.map((b, i) => {
            const a = layout.positions.get(b.fromKey);
            const c = layout.positions.get(b.toKey);
            if (!a || !c) return null;
            const dominant = (['Calls', 'Implements', 'References'] as CodeGraphRelation[])
              .reduce((best, rel) => (b.byRelation[rel] > b.byRelation[best] ? rel : best), 'Calls' as CodeGraphRelation);
            const mx = (a.x + c.x) / 2 + (b.isBack ? 46 : 0);
            const my = (a.y + c.y) / 2;
            return (
              <path key={i} d={`M${a.x},${a.y} Q${mx},${my} ${c.x},${c.y}`} fill="none"
                stroke={b.isBack ? C.warning : EDGE_COLOR[dominant]}
                strokeWidth={bundleWidth(b.weight)}
                strokeDasharray={b.isBack ? '6 4' : undefined}
                opacity={0.6} />
            );
          })}
        </g>

        {/* Узлы: группы (кружок с числом типов) и типы (глиф вида) — раскрытая до
            типов группа показывает узлы вместо себя */}
        <g>
          {scene.items.map(it => {
            const p = layout.positions.get(it.key);
            if (!p) return null;
            const soft = it.kind === 'rest' || it.kind === 'small';
            const ring = it.kind === 'node' ? KIND_RING[it.node!.kind] : soft ? C.dashed : C.textSecondary;
            return (
              <g key={it.key} transform={`translate(${p.x.toFixed(1)},${p.y.toFixed(1)})`}
                style={{ cursor: soft ? 'default' : 'pointer' }}>
                {/* Прозрачный hit-target (как в «Фокусе»): у <g> нет своей геометрии, а у
                    видимых потомков pointerEvents="none" — без этого круга клик по узлу
                    уходил насквозь до пучка связи позади. Заглушкам rest/small hit-target
                    не нужен: кликом они не управляются намеренно */}
                {!soft && (
                  <circle r={Math.max(p.r + 10, 20)} fill="transparent"
                    onClick={ev => { ev.stopPropagation(); onItemClick(it); }}
                    onDoubleClick={ev => { ev.stopPropagation(); onItemDblClick(it); }} />
                )}
                {it.godCount > 0 && (
                  <circle r={p.r + 6} fill="none" stroke={C.accent} strokeWidth={2}
                    strokeDasharray="3 3" opacity={0.5} pointerEvents="none" />
                )}
                <circle r={p.r} fill={C.bgCard} stroke={ring}
                  strokeWidth={2.2} strokeDasharray={soft ? '4 3' : undefined} pointerEvents="none" />
                {it.kind === 'node' ? (
                  <text textAnchor="middle" dominantBaseline="central" fontFamily={FONT.mono}
                    fontSize={FS.base} fontWeight={600} fill={KIND_COLOR[it.node!.kind]} pointerEvents="none">
                    {KIND_GLYPH[it.node!.kind]}
                  </text>
                ) : (
                  <text textAnchor="middle" dominantBaseline="central" fontFamily={FONT.mono}
                    fontSize={FS.sm} fontWeight={600} fill={soft ? C.textMuted : C.textHeading} pointerEvents="none">
                    {soft ? '…' : it.count}
                  </text>
                )}
                <text textAnchor="middle" y={p.r + 14} fontFamily={FONT.mono} fontSize={FS.xs}
                  fill={C.textSecondary} pointerEvents="none">
                  {clipOverviewLabel(it.label, maxLabel)}
                </text>
              </g>
            );
          })}
        </g>
      </SceneTransition>
    </svg>
  );
}

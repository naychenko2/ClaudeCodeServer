// Граф процесса фичи (C3a, разрез 2 docs/research/ai-fabrika-workflow-2-cut-2026-11.md §1.9,
// макет feature-page.html §«граф: узлы и связи»). Рисует FeatureProcessGraphModel как есть:
// колонки — по полю column с бэка, рёбра — по after. Ничего не достраивает и не пересчитывает:
// состояния и тона показывает, как пришли (шаг Ф5, тот же контракт, что у FeatureStageLane).
// Библиотеки графов нет: колонок ≤ 16 (потолок шаблона, WorkflowGraph.cs:17), карточки
// фиксированного размера — позиции выводятся из геометрии напрямую, рёбра — SVG-кривые в
// слое ЗА карточками (пересекающие чужие колонки линии видны только в межколоночных
// зазорах — карточки их перекрывают).
//
// Десктоп: колонки слева направо, внутри колонки — вертикально, в порядке run.stages;
// контейнер прокручивается по горизонтали сам (16 узлов × 144 px шире экрана) — документ
// не переполняется. Мобила (≤600, useIsMobile): вертикальный список колонок; параллельные
// стадии колонки объединены сколкой «∥ параллельно» слева от группы (разрез §1.9).
//
// Выбор — по id стадии (контракт C3a): onSelect(id), подсветка выбранной — по selectedId.
// Своего состояния нет (тот же контракт, что у FeatureStageLane): изначальный выбор —
// defaultSelected модели, его держит вызывающий (C3b).
//
// Тона активной стадии (цвет графа, карточка §1.9): run — accent, wait — info,
// stopped — danger, loop — warning (тот же тон, что «стоит»/«доработка» в строке индекса,
// chatGroups.ts:512,527). done — success; pending/absent/skipped — пунктир.
// Слово о состоянии для aria-label — из единого источника stageStatusLabel; «готово» для
// done — подпись легенды макета (у done слова нет).
//
// Мобильные тач-цели: карточка 120×56 (≥40, контракт C3a); ширина графа ≤140 — документ
// на 360 не переполняется.

import type { CSSProperties } from 'react';
import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { laneWord } from '../../lib/roleLabel';
import { stageName, stageStatusLabel } from './FeatureTimelineBits';
import { graphColumns, graphEdges, stageNodeId, type GraphColumn } from './FeatureProcessGraphModel';
import type { FeatureRun, FeatureStage, StageTone } from '../../types/featureRun';

export interface FeatureProcessGraphProps {
  run: FeatureRun;
  // Id выбранной стадии (подсветка). Состояние — у вызывающего (C3b).
  selectedId?: string | null;
  // Клик/Enter по карточке: id стадии (не индекс).
  onSelect?: (id: string) => void;
}

// Фиксированная геометрия карточек (макет feature-page.html .node: min-width 120, min-height 56;
// 56 ≥ 40 — тач-цель контракта C3a). Позиции и рёбра выводятся из этих констант — замера DOM нет.
const NODE_W = 120;
const NODE_H = 56;
// Зазор между колонками — пространство для рёбер (макет .link 20 + запас на кривую).
const GAP_X = 24;
// Зазор между стадиями внутри колонки.
const GAP_Y = SP.sm;
// Сколка «параллельно» (мобила): ширина линии-скобки и подпись над группой.
const BRACKET_W = 10;
const BRACKET_INDENT = 20;
const PAR_LABEL_H = 16;

// Тон активной стадии → цвет графа (см. шапку). null — тон не задан (active без tone —
// бэк отдаёт tone только у active, но старый ответ может не несть): акцент, как run.
const toneColor = (tone?: StageTone | null): string =>
  tone === 'wait' ? C.info
    : tone === 'stopped' ? C.danger
      : tone === 'loop' ? C.warning
        : C.accent;

// Позиции карточ по id: десктоп — колонки по X, мобила — колонки по Y. Одна функция для
// обоих: раскладка — геометрия, не семантика (колонки и порядок даёт модель).
interface NodePos { x: number; y: number }
interface GraphLayout {
  pos: Map<string, NodePos>;
  width: number;
  height: number;
  // Мобильные скобки: для колонок с >1 стадии — координаты группы.
  brackets: { x: number; top: number; height: number }[];
}

function layout(cols: GraphColumn[], mobile: boolean): GraphLayout {
  const pos = new Map<string, NodePos>();
  const brackets: GraphLayout['brackets'] = [];
  if (mobile) {
    let y = 0;
    let hasParallel = false;
    for (const col of cols) {
      const parallel = col.stages.length > 1;
      if (parallel) {
        hasParallel = true;
        y += PAR_LABEL_H; // строка «∥ параллельно» над группой
      }
      const x = parallel ? BRACKET_INDENT : 0;
      col.stages.forEach((stage, k) => {
        pos.set(stageNodeId(stage), { x, y: y + k * (NODE_H + GAP_Y) });
      });
      if (parallel) {
        brackets.push({ x: 0, top: y, height: col.stages.length * (NODE_H + GAP_Y) - GAP_Y });
      }
      y += col.stages.length * (NODE_H + GAP_Y) - GAP_Y + GAP_X;
    }
    return {
      pos,
      width: (hasParallel ? BRACKET_INDENT : 0) + NODE_W,
      height: Math.max(0, y - GAP_X),
      brackets,
    };
  }
  let maxRows = 0;
  cols.forEach((col, ci) => {
    maxRows = Math.max(maxRows, col.stages.length);
    col.stages.forEach((stage, k) => {
      pos.set(stageNodeId(stage), { x: ci * (NODE_W + GAP_X), y: k * (NODE_H + GAP_Y) });
    });
  });
  return {
    pos,
    width: cols.length * (NODE_W + GAP_X) - GAP_X,
    height: maxRows * (NODE_H + GAP_Y) - GAP_Y,
    brackets,
  };
}

// Кривая ребра: десктоп — горизонтальная (выход с середины правой грани входа, вход в
// середину левой), мобила — вертикальная (низ источника → верх цели). Разные колонки —
// разные координаты, поэтому кривая есть всегда; прямая была бы только вырожденным случаем.
function edgePath(a: NodePos, b: NodePos, mobile: boolean): string {
  if (mobile) {
    const x1 = a.x + NODE_W / 2;
    const y1 = a.y + NODE_H;
    const x2 = b.x + NODE_W / 2;
    const y2 = b.y;
    const my = (y1 + y2) / 2;
    return `M ${x1} ${y1} C ${x1} ${my}, ${x2} ${my}, ${x2} ${y2}`;
  }
  const x1 = a.x + NODE_W;
  const y1 = a.y + NODE_H / 2;
  const x2 = b.x;
  const y2 = b.y + NODE_H / 2;
  const mx = (x1 + x2) / 2;
  return `M ${x1} ${y1} C ${mx} ${y1}, ${mx} ${y2}, ${x2} ${y2}`;
}

// Имя карточки — с бэка (доработка C3a по вердикту R-C3a, контракт a5d0c76e/G7):
// FeatureStage.Title подтверждённого маршрута; бэк гарантирует различимость имён у этапов
// с общей парой (role, lane) — «Параллельный цикл» даёт qa-plan «Проверка плана» /
// qa «Проверка», и оба qa-узла различимы подписью и aria-label. Фолбэк (легаси и этап
// без имени, как и раньше): общий STAGE_NAME через stageName (своей таблицы имён нет)
// плюс линия для параллельных этапов одной роли (step-back/step-front дают одинаковое
// имя «Производство»; различаются они только линией — без неё две одинаковые карточки).
function nodeTitle(stage: FeatureStage): string {
  if (stage.title) return stage.title;
  const lane = laneWord(stage.lane ?? null);
  const name = stageName(stage.key);
  return lane ? `${name} · ${lane}` : name;
}

// Слово состояния для aria-label — из единого источника (stageStatusLabel, доработка Ш2b);
// для done слова нет — подпись легенды макета «готово».
function nodeAria(stage: FeatureStage): string {
  return `${nodeTitle(stage)}: ${stageStatusLabel(stage)?.text ?? 'готово'}`;
}

// Круг-маркер состояния (16 px, форма .ico макета): done — залитый success с «✓»;
// active — обводка и точка тона; pending/absent/skipped — пустое кольцо (у отсутствующей —
// пунктирное).
function MarkerIcon({ stage }: { stage: FeatureStage }) {
  const base: CSSProperties = {
    width: 16, height: 16, borderRadius: R.full, flexShrink: 0,
    display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
    boxSizing: 'border-box',
  };
  if (stage.state === 'done') {
    return (
      <span aria-hidden="true" style={{ ...base, background: C.success, border: `2px solid ${C.success}`, color: C.onAccent, fontSize: FS.xxs, fontWeight: 700 }}>
        ✓
      </span>
    );
  }
  if (stage.state === 'active') {
    const color = toneColor(stage.tone);
    const bg = stage.tone === 'wait' ? C.infoBg
      : stage.tone === 'stopped' ? C.dangerBg
        : stage.tone === 'loop' ? C.warningBg
          : C.accentLight;
    return (
      <span aria-hidden="true" style={{ ...base, border: `2px solid ${color}`, background: bg }}>
        <span style={{ width: 6, height: 6, borderRadius: R.full, background: color }} />
      </span>
    );
  }
  // pending — сплошное кольцо; absent/skipped — пунктирное (той же логики, что stageDotStyle).
  const dashed = stage.state !== 'pending';
  return (
    <span aria-hidden="true" style={{ ...base, border: `2px ${dashed ? 'dashed' : 'solid'} ${dashed ? C.dashed : C.progressTrack}` }} />
  );
}

export function FeatureProcessGraph({ run, selectedId = null, onSelect }: FeatureProcessGraphProps) {
  const isMobile = useIsMobile();
  const cols = graphColumns(run);
  if (cols.length === 0) return null; // стадий нет — графа нет (empty-state страницы — решение C3b)

  const edges = graphEdges(run);
  const lay = layout(cols, isMobile);
  const stagesById = new Map<string, FeatureStage>();
  for (const col of cols) {
    for (const s of col.stages) stagesById.set(stageNodeId(s), s);
  }

  return (
    <div
      data-testid="feature-process-graph"
      style={{
        // Десктоп: широкий граф прокручивается внутри себя (16 узлов × 144 px шире экрана);
        // документ не переполняется. Мобила — вертикальный список шириной ≤140, прокрутки нет.
        ...(isMobile ? {} : { overflowX: 'auto' as const, paddingBottom: SP.xxs }),
      }}
    >
      <div style={{ position: 'relative', width: lay.width, height: lay.height }}>
        <svg
          data-testid="feature-process-graph-edges"
          aria-hidden="true"
          width={lay.width}
          height={lay.height}
          style={{ position: 'absolute', left: 0, top: 0, pointerEvents: 'none' }}
        >
          {edges.map((e) => {
            const a = lay.pos.get(e.from);
            const b = lay.pos.get(e.to);
            if (!a || !b) return null; // нессылающийся id — не рисуется (фильтр модели — второй раз)
            const from = stagesById.get(e.from);
            // Пройденное ребро — зелёное (макет .link.done): источник done — поток прошёл.
            const done = from?.state === 'done';
            return (
              <path
                key={`${e.from}->${e.to}`}
                data-testid={`pgraph-edge-${e.from}-${e.to}`}
                d={edgePath(a, b, isMobile)}
                fill="none"
                stroke={done ? C.success : C.progressTrack}
                strokeWidth={2}
              />
            );
          })}
        </svg>

        {lay.brackets.map((b, i) => (
          // Сколка группы параллельных стадий (мобила, разрез §1.9): линия с отворотами,
          // открытыми в сторону карточек, и подпись над группой.
          <span key={`br-${i}`} aria-hidden="true">
            <span
              style={{
                position: 'absolute', left: b.x, top: b.top, width: BRACKET_W, height: b.height,
                border: `2px solid ${C.textMuted}`, borderRight: 'none', boxSizing: 'border-box',
              }}
            />
            <span
              style={{
                position: 'absolute', left: b.x, top: b.top - PAR_LABEL_H, height: PAR_LABEL_H,
                display: 'inline-flex', alignItems: 'center', fontFamily: FONT.mono,
                fontSize: FS.xxs, color: C.textMuted, whiteSpace: 'nowrap',
              }}
            >
              ∥ параллельно
            </span>
          </span>
        ))}

        {[...lay.pos.entries()].map(([id, p]) => {
          const stage = stagesById.get(id)!;
          const selected = selectedId === id;
          const active = stage.state === 'active';
          const quiet = stage.state !== 'done' && !active; // pending/absent/skipped
          return (
            <button
              key={id}
              type="button"
              data-testid={`pgraph-node-${id}`}
              data-state={stage.state}
              data-tone={active ? stage.tone ?? 'run' : undefined}
              aria-pressed={selected}
              aria-label={nodeAria(stage)}
              onClick={() => onSelect?.(id)}
              style={{
                position: 'absolute', left: p.x, top: p.y, width: NODE_W, height: NODE_H,
                boxSizing: 'border-box', display: 'flex', alignItems: 'center', gap: SP.sm,
                padding: `0 ${SP.sm}`, textAlign: 'left', cursor: 'pointer', zIndex: 1,
                borderRadius: R.lg,
                background: quiet ? 'transparent' : C.bgWhite,
                border: `1px ${quiet ? 'dashed' : 'solid'} ${
                  active ? toneColor(stage.tone)
                    : quiet ? C.dashed
                      : C.borderLight}`,
                ...(selected ? { boxShadow: SHADOW.selected } : {}),
              }}
            >
              <MarkerIcon stage={stage} />
              <span style={{ fontSize: FS.sm, fontWeight: 600, color: quiet ? C.textSecondary : C.textHeading }}>
                {nodeTitle(stage)}
              </span>
            </button>
          );
        })}
      </div>
    </div>
  );
}

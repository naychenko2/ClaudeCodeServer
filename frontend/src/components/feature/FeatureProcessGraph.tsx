// Граф процесса фичи (C3a, разрез 2 docs/research/ai-fabrika-workflow-2-cut-2026-11.md §1.9,
// макет feature-page.html §«граф: узлы и связи»). Рисует FeatureProcessGraphModel как есть:
// колонки — по полю column с бэка, рёбра — по after. Ничего не достраивает и не пересчитывает:
// состояния и тона показывает, как пришли (шаг Ф5, тот же контракт, что у FeatureStageLane).
// Библиотеки графов нет: колонок ≤ 16 (потолок шаблона, WorkflowGraph.cs:17), карточки
// фиксированного размера — позиции выводятся из геометрии напрямую, рёбра — SVG-кривые в
// слое ЗА карточками (пересекающие чужие колонки линии видны только в межколоночных
// зазорах — карточки их перекрывают).
//
// Раскладка (F07, макет feature-page.html P14, P23–P25, P27, P29–P31): колонки слева направо,
// внутри колонки — вертикально, порядок run.stages. Десктоп — колонки растянуты на ширину
// страницы (не уже 116), параллельная колонка в пунктирной рамке «⇉ параллельно»; мобила —
// горизонтальная лента узлов 104 px, группа — скобкой, выбранный узел прокручивается в центр.
// Дальнее ребро (через колонку) — обходом под графом пунктиром. Над графом — «Процесс» и
// подпись шаблона, под ним — легенда.
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
// Тач-цель узла — вся карточка 88 px высотой (≥40, контракт C3a); лента прокручивается внутри
// блока — документ на 360 не переполняется. То, что лента обрезана краем, видно по самой
// ленте: край-подсказка (дефект 1f6df093) — см. «Признак прокрутки» ниже.
//
// Узел (F06, макет feature-page.html P16–P22, materials-review-placement.html M14–M19):
// имя переносится целиком, без обрезки (узел 152×88 вмещает до трёх строк имени вместе со
// строками меты); вторая строка — «Персона · время» / «ждёт ответа вас» (warning) /
// «ждёт обе ветки»; третья — метка роли mono, чип линии (бэк/фронт), полоска задач ветки и
// «2/3»; бейдж «↺N» над узлом при возвратах. Всё рисуется по данным бэка (B01): состояние
// «ждёт» берётся из stage.waiting, а не из примера макета. Полное имя — ещё и в title/aria-label.

import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { laneWord } from '../../lib/roleLabel';
import { formatDuration } from '../../lib/chatGroups';
import { stageName, stageStatusLabel } from './FeatureTimelineBits';
import {
  graphColumns, graphEdges, stageNodeId, columnRanks, isFarEdge, graphCaption, PARALLEL_LABEL,
  type GraphColumn,
} from './FeatureProcessGraphModel';
import type { FeatureRun, FeatureStage, StageTone } from '../../types/featureRun';

export interface FeatureProcessGraphProps {
  run: FeatureRun;
  // Id выбранной стадии (подсветка). Состояние — у вызывающего (C3b).
  selectedId?: string | null;
  // Клик/Enter по карточке: id стадии (не индекс).
  onSelect?: (id: string) => void;
  // Имя шаблона подтверждённого маршрута для подписи секции (F07); нет — в подписи id.
  templateName?: string | null;
}

// Геометрия карточек (макет feature-page.html .node; 88 ≥ 40 — тач-цель контракта C3a).
// Высота фиксирована, ширина: десктоп — колонки растянуты на ширину страницы (flex:1 макета,
// не уже NODE_MIN_W), мобила — лента узлов NODE_W_MOBILE. Позиции и рёбра выводятся из этих
// чисел; ширина контейнера — единственное, что замеряется (ResizeObserver).
const NODE_H = 88;
const NODE_MIN_W = 116;
const NODE_W_MOBILE = 104;
// Строк имени этапа в узле (при большем числе — многоточие, полное имя в title).
const NAME_LINES = 3;
// Ширина узла без замера (первый кадр, тесты без раскладки): прежний размер карточки.
const NODE_W_DEFAULT = 152;
// Зазор между колонками — пространство для рёбер (макет .link 20 + запас на кривую).
const GAP_X = 24;
// Зазор между стадиями внутри колонки.
const GAP_Y = SP.sm;
// Рамка/скобка «параллельно»: вылет за узлы и высота подписи на верхней границе.
const FRAME_PAD = 6;
const PAR_LABEL_H = 14;
// Дуга far-ребра под графом: отступ первой полосы и шаг между полосами.
const ARC_GAP = 12;
const ARC_STEP = 8;

// Признак прокрутки (дефект 1f6df093): лента шире контейнера — невидимую часть обозначает
// сам край: затухание к фону страницы и шеврон. Правый — пока не доехал до конца ленты,
// левый — когда отъехал от начала. Тот же приём края, что у ленты чата (ChatPanel.tsx:111,
// AgentContentBlocks.tsx:46), только по горизонтали. Без замера контейнера (первый кадр,
// тесты без раскладки) признака нет: он говорит о фактической прокрутке, а не о предполагаемой.
const EDGE_W = 24;
const NO_EDGES = { left: false, right: false };

// Тон активной стадии → цвет графа (см. шапку). null — тон не задан (active без tone —
// бэк отдаёт tone только у active, но старый ответ может не несть): акцент, как run.
const toneColor = (tone?: StageTone | null): string =>
  tone === 'wait' ? C.info
    : tone === 'stopped' ? C.danger
      : tone === 'loop' ? C.warning
        : C.accent;

// Позиции карточек по id. Десктоп и мобила — одна раскладка (колонки слева направо, внутри
// колонки — вертикально, порядок даёт модель); различаются шириной узла и видом группы
// «параллельно» (рамка / скобка).
interface NodePos { x: number; y: number }
interface GraphLayout {
  pos: Map<string, NodePos>;
  nodeW: number;
  width: number;
  // Низ карточек (и рамок) — от него идут полосы дуг far-рёбер.
  bottom: number;
  height: number;
  // Параллельные группы (колонки с >1 стадии): прямоугольник рамки.
  groups: { x: number; y: number; w: number; h: number }[];
}

// avail — доступная ширина контейнера (null — не замерена).
function layout(cols: GraphColumn[], mobile: boolean, avail: number | null, farCount: number): GraphLayout {
  const pos = new Map<string, NodePos>();
  const groups: GraphLayout['groups'] = [];
  const hasParallel = cols.some(c => c.stages.length > 1);
  const padX = hasParallel ? FRAME_PAD : 0;
  const top = hasParallel ? PAR_LABEL_H : 0;
  const n = cols.length;
  const nodeW = mobile
    ? NODE_W_MOBILE
    : avail != null
      ? Math.max(NODE_MIN_W, Math.floor((avail - 2 * padX + GAP_X) / n) - GAP_X)
      : NODE_W_DEFAULT;
  let maxRows = 0;
  cols.forEach((col, ci) => {
    const x = padX + ci * (nodeW + GAP_X);
    maxRows = Math.max(maxRows, col.stages.length);
    col.stages.forEach((stage, k) => {
      pos.set(stageNodeId(stage), { x, y: top + k * (NODE_H + GAP_Y) });
    });
    if (col.stages.length > 1) {
      const h = col.stages.length * (NODE_H + GAP_Y) - GAP_Y;
      groups.push({ x: x - FRAME_PAD, y: top - FRAME_PAD, w: nodeW + 2 * FRAME_PAD, h: h + 2 * FRAME_PAD });
    }
  });
  const bottom = top + maxRows * (NODE_H + GAP_Y) - GAP_Y + (hasParallel ? FRAME_PAD : 0);
  return {
    pos,
    nodeW,
    width: 2 * padX + n * (nodeW + GAP_X) - GAP_X,
    bottom,
    height: bottom + (farCount > 0 ? ARC_GAP + ARC_STEP * (farCount - 1) + SP.xs : 0),
    groups,
  };
}

// Кривая ребра: выход с середины правой грани источника, вход в середину левой грани цели.
// Дальнее ребро (через колонку, макет P25) идёт обходом под графом — по полосе `lane` ниже
// карточек: чужие колонки его не перекрывают.
function edgePath(a: NodePos, b: NodePos, nodeW: number, far: { bottom: number; lane: number } | null): string {
  const x1 = a.x + nodeW;
  const y1 = a.y + NODE_H / 2;
  const x2 = b.x;
  const y2 = b.y + NODE_H / 2;
  if (far) {
    const yb = far.bottom + ARC_GAP + far.lane * ARC_STEP;
    return `M ${x1} ${y1} C ${x1 + 10} ${y1}, ${x1 + 10} ${yb}, ${x1 + 20} ${yb} L ${x2 - 20} ${yb} C ${x2 - 10} ${yb}, ${x2 - 10} ${y2}, ${x2} ${y2}`;
  }
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
export function nodeTitle(stage: FeatureStage): string {
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

// Вторая строка узла (B01): ждёт человека — warning («ждёт ответа вас»), ждёт ветки —
// приглушённо; иначе «Персона · время» (части без данных пропускаются); пусто — строки нет.
// F33a (макет .nm): у незавершённого proposed-узла пометка «предложено» живёт здесь же:
// при пустой мете — на её месте, при занятой — рядом через « · »; в нижней строке с ролью её нет.
// R-F33a (решение Майи, 14.11): «предложено» говорит только о том, чего ещё не было, —
// завершённый узел предложенного маршрута в этой пометке не нуждается; вместо неё в мете
// суффикс «до маршрута» (макет feature-page.html:447): с исполнителем — после « · »,
// без него — просто суффикс, без висящего разделителя.
export function nodeMeta(
  stage: FeatureStage,
): { text: string; color: string; proposed?: boolean } | null {
  const asProposed = stage.proposed === true && stage.state !== 'done';
  if (stage.waiting === 'human') return { text: 'ждёт ответа вас', color: C.warning, proposed: asProposed };
  if (stage.waiting === 'branches') return { text: 'ждёт обе ветки', color: C.textMuted, proposed: asProposed };
  const parts: string[] = [];
  const names = (stage.personaNames ?? []).filter(Boolean);
  if (names.length > 0) parts.push(names.join(', '));
  if (stage.elapsedMinutes != null) parts.push(formatDuration(stage.elapsedMinutes));
  if (stage.proposed === true && stage.state === 'done') parts.push('до маршрута');
  if (parts.length > 0) return { text: parts.join(' · '), color: C.textSecondary, proposed: asProposed };
  // Мета пуста, но есть пометка — строка нужна ради неё (цвет .nm макета — text-muted).
  return asProposed ? { text: '', color: C.textMuted, proposed: true } : null;
}

// Сегменты полоски задач ветки: готово (success) → в работе (accent) → остальное (трек).
function SegmentBar({ done, running, total }: { done: number; running: number; total: number }) {
  const kind = (i: number) => (i < done ? 'done' : i < done + running ? 'run' : 'todo');
  return (
    <span aria-hidden="true" data-testid="pgraph-segments" style={{ display: 'inline-flex', gap: SP.xxs, flex: '1 1 auto', minWidth: 0 }}>
      {Array.from({ length: total }, (_, i) => (
        <span
          key={i}
          data-kind={kind(i)}
          style={{
            flex: '1 1 0', height: 4, borderRadius: R.full,
            background: kind(i) === 'done' ? C.success : kind(i) === 'run' ? C.accent : C.progressTrack,
          }}
        />
      ))}
    </span>
  );
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

// Легенда графа (макет P30): значки — те же, что рисует узел. Строка про proposed (макет
// :463) — onlyWithProposed: показывается только когда на графе есть proposed-узел (F33a).
const LEGEND: { mark: string; text: string; color?: string; onlyWithProposed?: boolean }[] = [
  { mark: '✓', text: 'готово' },
  { mark: '●', text: 'в работе' },
  { mark: '◐', text: 'ждёт человека' },
  { mark: '┄', text: 'ещё не начато' },
  { mark: '┄', text: 'фиолетовый пунктир — предложено, не подтверждено', color: C.plan, onlyWithProposed: true },
  { mark: '↺', text: 'возвраты на доработку' },
  { mark: '⇉', text: 'рамка — параллельно' },
];

export function FeatureProcessGraph({ run, selectedId = null, onSelect, templateName = null }: FeatureProcessGraphProps) {
  const isMobile = useIsMobile();
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const nodeRefs = useRef(new Map<string, HTMLElement>());
  const [avail, setAvail] = useState<number | null>(null);
  const [edgeHint, setEdgeHint] = useState<{ left: boolean; right: boolean }>(NO_EDGES);

  // Ширина контейнера (для растяжения колонок, десктоп) и состояние краев ленты: что из неё
  // выступает за левый и правый край. Нет замера (тест без раскладки) — прежняя ширина
  // карточки и признака прокрутки нет.
  const measure = () => {
    const el = wrapRef.current;
    if (!el) return;
    if (el.clientWidth > 0) setAvail(el.clientWidth);
    const over = el.scrollWidth - el.clientWidth;
    const next = over > 1 ? { left: el.scrollLeft > 1, right: el.scrollLeft < over - 1 } : NO_EDGES;
    setEdgeHint((p) => (p.left === next.left && p.right === next.right ? p : next));
  };

  // Мобила мерится так же, как десктоп: ширина узла на ленте задаётся не контейнером
  // (NODE_W_MOBILE), а вот признак прокрутки нужен именно на ней.
  useEffect(() => {
    const el = wrapRef.current;
    if (!el) return undefined;
    measure();
    el.addEventListener('scroll', measure);
    if (typeof ResizeObserver === 'undefined') return () => el.removeEventListener('scroll', measure);
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => { ro.disconnect(); el.removeEventListener('scroll', measure); };
  }, [isMobile]);

  // Лента меняет ширину и без ресайза окна (пришли стадии, колонки растянулись под уже
  // измеренный контейнер) — замер повторяем после каждого кадра раскладки.
  useEffect(() => { measure(); });

  // Мобила: выбранный узел — в центр ленты.
  useEffect(() => {
    if (!isMobile || selectedId == null) return;
    nodeRefs.current.get(selectedId)?.scrollIntoView?.({ inline: 'center', block: 'nearest', behavior: 'smooth' });
  }, [isMobile, selectedId]);

  const cols = graphColumns(run);
  if (cols.length === 0) return null; // стадий нет — графа нет (empty-state страницы — решение C3b)

  const edges = graphEdges(run);
  const ranks = columnRanks(cols);
  const farCount = edges.filter(e => isFarEdge(ranks, e)).length;
  const lay = layout(cols, isMobile, avail, farCount);
  const nodeW = lay.nodeW;
  const stagesById = new Map<string, FeatureStage>();
  for (const col of cols) {
    for (const s of col.stages) stagesById.set(stageNodeId(s), s);
  }
  // Строка легенды «предложено» — только при незавершённом proposed-узле на графе
  // (F33a, макет :463; R-F33a·дельта: та же формула, что у узла и ребра — «предложено»
  // только у незавершённых; при полностью выполненном маршруте фиолетовых узлов нет).
  const hasProposed = [...stagesById.values()].some(s => s.proposed === true && s.state !== 'done');
  let farLane = 0;

  // Обёртка скроллера — та, чьим краем мерится прокрутка: она же держит край-подсказку
  // (position:absolute внутри скроллера уехал бы вместе с лентой, а не остался на краю).
  return (
    <div data-testid="feature-process-section" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xxs, letterSpacing: 0.6, textTransform: 'uppercase', color: C.textMuted }}>
          Процесс
        </span>
        <span data-testid="pgraph-caption" style={{ fontSize: FS.xs, color: C.textSecondary }}>
          {graphCaption(run, templateName)}
        </span>
      </div>
      <div data-testid="pgraph-scroll" style={{ position: 'relative', minWidth: 0 }}>
        <div
          ref={wrapRef}
          data-testid="feature-process-graph"
          style={{
            // Колонки растянуты на ширину страницы; не влезают (min 116) или мобильная лента —
            // прокрутка внутри блока, документ не переполняется.
            overflowX: 'auto', paddingBottom: SP.xxs,
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
                // Ребро предложенного маршрута (макет P26) — фиолетовый пунктир: ещё не подтверждено.
                // R-F33a·дельта: та же формула, что у узла (см. рендер узла): «предложено» только
                // у незавершённой цели — к завершённому узлу предложенного маршрута ребро обычное
                // (макет :374).
                const to = stagesById.get(e.to);
                const proposed = to?.proposed === true && to.state !== 'done';
                const far = isFarEdge(ranks, e);
                return (
                  <path
                    key={`${e.from}->${e.to}`}
                    data-testid={`pgraph-edge-${e.from}-${e.to}`}
                    data-far={far ? 'true' : undefined}
                    d={edgePath(a, b, nodeW, far ? { bottom: lay.bottom, lane: farLane++ } : null)}
                    fill="none"
                    data-proposed={proposed ? 'true' : undefined}
                    stroke={proposed ? C.planBorder : done ? C.success : C.progressTrack}
                    strokeWidth={2}
                    strokeDasharray={proposed || far ? '5 4' : undefined}
                  />
                );
              })}
            </svg>

            {lay.groups.map((g, i) => (
              // Группа параллельных стадий колонки (макет P23): на десктопе — пунктирная рамка,
              // на мобиле — скобка (левая грань с отворотами); подпись «⇉ параллельно» везде.
              <span key={`grp-${i}`} aria-hidden="true" data-testid={`pgraph-group-${i}`} data-kind={isMobile ? 'bracket' : 'frame'}>
                <span
                  style={{
                    position: 'absolute', left: g.x, top: g.y, width: g.w, height: g.h, boxSizing: 'border-box',
                    borderRadius: R.lg, border: `1px dashed ${C.textMuted}`,
                    ...(isMobile ? { borderStyle: 'solid', borderRightStyle: 'none', borderRadius: `${R.sm}px 0 0 ${R.sm}px` } : {}),
                  }}
                />
                <span
                  style={{
                    position: 'absolute', left: g.x + SP.sm, top: g.y - PAR_LABEL_H / 2, height: PAR_LABEL_H,
                    padding: `0 ${SP.xs}px`, display: 'inline-flex', alignItems: 'center', fontFamily: FONT.mono,
                    fontSize: FS.xxs, color: C.textMuted, whiteSpace: 'nowrap', background: C.bgMain,
                  }}
                >
                  {PARALLEL_LABEL}
                </span>
              </span>
            ))}

            {[...lay.pos.entries()].map(([id, p]) => {
              const stage = stagesById.get(id)!;
              const selected = selectedId === id;
              const active = stage.state === 'active';
              const quiet = stage.state !== 'done' && !active; // pending/absent/skipped
              // R-F33a: оформление proposed — только у незавершённой стадии; рамка и цвет узла
              // читают его состояние: завершённый узел предложенного маршрута — обычный done.
              const proposed = stage.proposed === true && stage.state !== 'done';
              const returns = stage.returns ?? 0;
              const meta = nodeMeta(stage);
              const line = laneWord(stage.line ?? null);
              const seg = stage.taskSegments;
              return (
                <button
                  key={id}
                  type="button"
                  ref={(el) => { if (el) nodeRefs.current.set(id, el); else nodeRefs.current.delete(id); }}
                  data-testid={`pgraph-node-${id}`}
                  data-state={stage.state}
                  data-proposed={proposed ? 'true' : undefined}
                  data-tone={active ? stage.tone ?? 'run' : undefined}
                  aria-pressed={selected}
                  aria-label={nodeAria(stage)}
                  onClick={() => onSelect?.(id)}
                  style={{
                    position: 'absolute', left: p.x, top: p.y, width: nodeW, height: NODE_H,
                    boxSizing: 'border-box', display: 'flex', flexDirection: 'column', justifyContent: 'center',
                    gap: SP.xs, padding: `${SP.xs}px ${SP.sm}px`, textAlign: 'left', cursor: 'pointer', zIndex: 1,
                    borderRadius: R.lg, font: 'inherit',
                    // Фон proposed-узла — прозрачный (макет .node.prop, feature-page.html:280):
                    // предложенный этап — «призрак» за пунктиром, а не залитая плашка.
                    background: proposed || quiet ? 'transparent' : C.bgWhite,
                    // Рамка proposed — «1px dashed c-plan» целиком (макет .node.prop, F33a).
                    border: proposed && !selected
                      ? `1px dashed ${C.plan}`
                      : `1px ${(quiet || proposed) && !selected ? 'dashed' : 'solid'} ${
                        selected ? C.accent
                          : active ? toneColor(stage.tone)
                            : quiet ? C.dashed
                              : C.borderLight}`,
                    ...(selected ? { boxShadow: SHADOW.focus } : {}),
                  }}
                >
                  {returns > 0 && (
                    <span
                      data-testid={`pgraph-returns-${id}`}
                      title={`возвратов на доработку: ${returns}`}
                      style={{
                        position: 'absolute', top: -8, right: SP.sm, padding: `0 ${SP.xs}px`, borderRadius: R.full,
                        background: C.warningBg, border: `1px solid ${C.warning}`, color: C.warning,
                        fontFamily: FONT.mono, fontSize: FS.xxs, fontWeight: 700, lineHeight: '14px',
                      }}
                    >
                      ↺{returns}
                    </span>
                  )}
                  <span style={{ display: 'flex', alignItems: 'flex-start', gap: SP.sm }}>
                    <span style={{ marginTop: 1, display: 'inline-flex' }}><MarkerIcon stage={stage} /></span>
                    {/* Узел фиксированной высоты вмещает три строки имени: длиннее — многоточие
                        (clamp 3), чтобы подпись не наезжала на мету и нижнюю строку. Полное
                        имя не теряется: title (тултип) и aria-label. */}
                    <span
                      data-testid={`pgraph-title-${id}`}
                      title={nodeTitle(stage)}
                      style={{
                        // Дефект 20e96078: 'anywhere' рвал слово по букве («Производст|во») без знака
                        // переноса — это читается как опечатка. hyphens рвёт по слогам со знаком
                        // (язык текста — lang="ru" в frontend/index.html:2); break-word — крайний
                        // случай для слова, которое не переносится ни целиком, ни по слогам.
                        flex: '1 1 auto', minWidth: 0, lineHeight: 1.25, overflowWrap: 'break-word', hyphens: 'auto',
                        display: '-webkit-box', WebkitBoxOrient: 'vertical', WebkitLineClamp: NAME_LINES, overflow: 'hidden',
                        // Заголовок proposed-узла — plan-text (макет .node.prop .nn, F33a).
                        fontSize: FS.sm, fontWeight: 600,
                        color: proposed ? C.planText : quiet ? C.textSecondary : C.textHeading,
                      }}
                    >
                      {nodeTitle(stage)}
                    </span>
                  </span>
                  {meta && (
                    <span
                      data-testid={`pgraph-meta-${id}`}
                      style={{ fontSize: FS.xs, color: meta.color, lineHeight: 1.2, overflowWrap: 'anywhere' }}
                    >
                      {meta.text}
                      {meta.text && meta.proposed ? ' · ' : ''}
                      {meta.proposed === true && (
                        <span data-testid={`pgraph-proposed-${id}`} style={{ color: C.textMuted }}>предложено</span>
                      )}
                    </span>
                  )}
                  <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
                    {stage.role && (
                      <span data-testid={`pgraph-role-${id}`} style={{ fontFamily: FONT.mono, fontSize: FS.xxs, color: C.textMuted }}>
                        {stage.role}
                      </span>
                    )}
                    {line && (
                      <span
                        data-testid={`pgraph-line-${id}`}
                        style={{
                          padding: `0 ${SP.xs}px`, borderRadius: R.full, border: `1px solid ${C.border}`,
                          fontSize: FS.xxs, color: C.textSecondary, lineHeight: '14px',
                        }}
                      >
                        {line}
                      </span>
                    )}
                    {seg && seg.total > 0 && (
                      <>
                        <SegmentBar done={seg.done} running={seg.running} total={seg.total} />
                        <span data-testid={`pgraph-segcount-${id}`} style={{ fontFamily: FONT.mono, fontSize: FS.xxs, color: C.textMuted }}>
                          {seg.done}/{seg.total}
                        </span>
                      </>
                    )}
                  </span>
                </button>
              );
            })}
          </div>
        </div>
        {edgeHint.right && (
          <span
            aria-hidden="true" data-testid="pgraph-scroll-right"
            style={{
              position: 'absolute', right: 0, top: 0, bottom: SP.xxs, width: EDGE_W, zIndex: 2, pointerEvents: 'none',
              display: 'flex', alignItems: 'center', justifyContent: 'flex-end', padding: `0 ${SP.xs}px`,
              background: `linear-gradient(to right, transparent, ${C.bgMain})`,
              fontFamily: FONT.mono, fontSize: FS.md, color: C.textMuted,
            }}
          >›</span>
        )}
        {edgeHint.left && (
          <span
            aria-hidden="true" data-testid="pgraph-scroll-left"
            style={{
              position: 'absolute', left: 0, top: 0, bottom: SP.xxs, width: EDGE_W, zIndex: 2, pointerEvents: 'none',
              display: 'flex', alignItems: 'center', justifyContent: 'flex-start', padding: `0 ${SP.xs}px`,
              background: `linear-gradient(to left, transparent, ${C.bgMain})`,
              fontFamily: FONT.mono, fontSize: FS.md, color: C.textMuted,
            }}
          >‹</span>
        )}
      </div>
      <div
        data-testid="pgraph-legend"
        style={{ display: 'flex', flexWrap: 'wrap', gap: `${SP.xs}px ${SP.md}px`, fontSize: FS.xxs, color: C.textMuted }}
      >
        {LEGEND.filter(l => !l.onlyWithProposed || hasProposed).map(l => (
          <span key={l.text} data-testid={l.onlyWithProposed ? 'pgraph-legend-proposed' : undefined} style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs }}>
            <span aria-hidden="true" style={{ fontFamily: FONT.mono, ...(l.color ? { color: l.color } : {}) }}>{l.mark}</span>
            {l.text}
          </span>
        ))}
      </div>
    </div>
  );
}

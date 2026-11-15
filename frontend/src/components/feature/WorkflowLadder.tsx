// Лестница маршрута для блока подтверждения (feature-workflows, F02; макет route-confirm.html,
// .lad/.ck/.lsk/.lslot): колонки слева направо через «→», параллельные этапы одной колонки в рамке
// «∥ параллельно», ребро через колонку — пунктирная дуга под лестницей. Чип этапа — пилюля-чекбокс:
// галочка и клик у съёмных, 🔒 с тултипом у несъёмных, пунктир у presence='optional'
// («по решению»), зелёная рамка у новых. Снятые этапы — в строке «Снято:», клик возвращает.
// Раскладка — модель workflowLadderModel (F01); здесь только вид. Дуга измеряется по DOM (как в
// макете): координаты чипов в layout-эффекте, до замера путь пустой.
// Режимы: interactive и readonly («шаблон изменился»). Мобила — вертикаль с «↓». Чип визуально
// 32 (десктоп) / 36 (мобила), но кнопка 40 по высоте: тач-цель по конвенции.

import { useLayoutEffect, useMemo, useRef, useState } from 'react';
import { C, FONT, FS, R, SP, TB } from '../../lib/design';
import { laneWord } from '../../lib/roleLabel';
import { buildLadder } from '../../lib/workflowLadderModel';
import { STAGE_ROLE_LABEL } from '../../features/workflowTemplates/WorkflowStageRow';
import { isNonSkippable } from '../../features/workflowTemplates/workflowTemplateModel';
import type { WorkflowStage } from '../../features/workflowTemplates/types';

export const LADDER_LOCK_ROLE_TIP = 'Шаги, финал и слияние не снимаются';
export const LADDER_LOCK_TEMPLATE_TIP = 'В этом шаблоне этап всегда';
export const LADDER_PLACEHOLDER = 'Этапы появятся здесь, когда вы выберете шаблон';
export const LADDER_PARALLEL = '∥ параллельно';

export interface WorkflowLadderProps {
  // Этапы шаблона в порядке маршрута (normalizeStages); null/пусто — слот-заглушка.
  stages: WorkflowStage[] | null;
  // Снятые человеком этапы (id).
  removedIds: string[];
  mode?: 'interactive' | 'readonly';
  // Этапы, появившиеся в новой версии шаблона: зелёная рамка.
  newIds?: string[];
  onToggle?: (id: string) => void;
  isMobile: boolean;
}

const CHIP_VISUAL_H = 32;
const CHIP_VISUAL_H_MOBILE = 36;
const TOUCH = TB.iconHitMobile;
const SLOT_MIN_H = 158;

interface ArcPath { key: string; d: string; ex: number; ey: number }

export function WorkflowLadder({
  stages, removedIds, mode = 'interactive', newIds, onToggle, isMobile,
}: WorkflowLadderProps): React.ReactElement {
  const readonly = mode === 'readonly';
  const list = useMemo(() => stages ?? [], [stages]);
  const byId = useMemo(() => new Map(list.map(s => [s.id ?? '', s])), [list]);
  const newSet = useMemo(() => new Set(newIds ?? []), [newIds]);
  const ladder = useMemo(
    () => buildLadder(
      list.map(s => ({
        id: s.id ?? '',
        after: s.after ?? [],
        presence: (s.presence ?? '').trim(),
        skippable: !isNonSkippable((s.role ?? '').trim()),
      })),
      new Set(removedIds),
    ),
    [list, removedIds],
  );
  const farEdges = useMemo(() => ladder.edges.filter(e => e.far), [ladder]);

  const ladRef = useRef<HTMLDivElement | null>(null);
  const [arcs, setArcs] = useState<ArcPath[]>([]);
  const [svgSize, setSvgSize] = useState({ w: 0, h: 0 });

  // Дуги по замеру: горизонталь — под лестницей, вертикаль — справа. Нет раскладки (тест, скрытый
  // блок) — путь пустой, дуга остаётся в DOM.
  useLayoutEffect(() => {
    const lad = ladRef.current;
    if (!lad || farEdges.length === 0) { setArcs([]); return; }
    const measurable = typeof lad.getBoundingClientRect === 'function';
    const L = measurable ? lad.getBoundingClientRect() : null;
    const box = (el: Element | null) => {
      if (!el || !L) return null;
      const r = el.getBoundingClientRect();
      return { l: r.left - L.left + lad.scrollLeft, r: r.right - L.left + lad.scrollLeft, t: r.top - L.top, b: r.bottom - L.top };
    };
    const chips = Array.from(lad.querySelectorAll?.('[data-ladder-chip]') ?? []).map(box).filter(Boolean) as NonNullable<ReturnType<typeof box>>[];
    const next = farEdges.map(e => {
      const key = `${e.from}>${e.to}`;
      const A = box(lad.querySelector?.(`[data-ladder-chip="${e.from}"]`) ?? null);
      const B = box(lad.querySelector?.(`[data-ladder-chip="${e.to}"]`) ?? null);
      if (!A || !B || chips.length === 0) return { key, d: '', ex: 0, ey: 0 };
      if (isMobile) {
        const side = Math.max(...chips.map(c => c.r)) + 16;
        const ay = (A.t + A.b) / 2;
        const by = (B.t + B.b) / 2;
        return { key, d: `M${A.r} ${ay} C${side} ${ay} ${side} ${by} ${B.r} ${by}`, ex: B.r, ey: by };
      }
      const base = Math.max(...chips.map(c => c.b)) + 20;
      const ax = (A.l + A.r) / 2;
      const bx = (B.l + B.r) / 2;
      return { key, d: `M${ax} ${A.b} C${ax} ${base} ${bx} ${base} ${bx} ${B.b}`, ex: bx, ey: B.b };
    });
    setArcs(next);
    setSvgSize({ w: lad.scrollWidth || 0, h: lad.scrollHeight || 0 });
  }, [farEdges, isMobile, ladder]);

  if (list.length === 0) {
    return (
      <div
        data-testid="workflow-ladder-slot"
        style={{
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          minHeight: isMobile ? undefined : SLOT_MIN_H, padding: SP.md, textAlign: 'center',
          border: `1px dashed ${C.border}`, borderRadius: R.xl,
          fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted,
        }}
      >
        {LADDER_PLACEHOLDER}
      </div>
    );
  }

  const chip = (id: string, checked: boolean) => {
    const s = byId.get(id);
    if (!s) return null;
    const label = STAGE_ROLE_LABEL[s.role] ?? s.role;
    const titleText = (s.title ?? '').trim();
    const name = titleText || label;
    const lane = laneWord(s.lane ?? null);
    const lock = ladder.locked[id];
    const off = (s.presence ?? '').trim() === 'off';
    const isNew = newSet.has(id);
    // «По решению» в данных — presence='optional'.
    const decision = (s.presence ?? '').trim() === 'optional';
    // Снимается только съёмный optional; presence=off и «обязательно в шаблоне» — только показ.
    const canToggle = !readonly && !lock && !off;
    const title = lock === 'role' ? LADDER_LOCK_ROLE_TIP
      : lock === 'template' ? LADDER_LOCK_TEMPLATE_TIP
        : off ? 'Не в маршруте'
          : checked ? 'По решению — нажмите, чтобы снять' : 'Снят — нажмите, чтобы вернуть';
    const aria = `Этап «${name}» ${checked ? 'включён в маршрут' : 'снят'}${isNew ? ', новый этап' : ''}`;
    const visualH = isMobile ? CHIP_VISUAL_H_MOBILE : CHIP_VISUAL_H;

    const pill = (
      <span
        style={{
          display: 'inline-flex', alignItems: 'center', gap: SP.xs,
          height: visualH, padding: `0 ${SP.sm}px`, borderRadius: R.max, boxSizing: 'border-box',
          border: `1px ${decision && !lock ? 'dashed' : 'solid'} ${isNew ? C.success : C.border}`,
          background: isNew ? C.successBg : checked ? C.bgWhite : 'transparent',
          color: isNew ? C.successText : checked ? C.textPrimary : C.textMuted,
          fontFamily: FONT.sans, fontSize: FS.sm, whiteSpace: 'nowrap',
        }}
      >
        {lock ? (
          <span aria-hidden="true" style={{ fontSize: FS.xxs }}>🔒</span>
        ) : (
          <span
            aria-hidden="true"
            style={{
              width: 14, height: 14, borderRadius: 4, boxSizing: 'border-box', flex: 'none',
              display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
              border: `1.5px solid ${checked ? C.accent : C.border}`,
              background: checked ? C.accent : C.bgWhite,
              color: C.onAccent, fontSize: FS.xxs, fontWeight: 700, lineHeight: 1,
            }}
          >
            {checked ? '✓' : ''}
          </span>
        )}
        <span style={{ textDecoration: checked ? undefined : 'line-through' }}>{name}</span>
        {lane && (
          <span style={{ fontSize: FS.xxs, fontWeight: 500, padding: `0 ${SP.xs}px`, borderRadius: R.sm, background: C.bgInset, color: C.textSecondary }}>
            {lane}
          </span>
        )}
        {titleText && (
          <span style={{ fontFamily: FONT.mono, fontSize: FS.xxs, color: C.textMuted }}>{s.role}</span>
        )}
      </span>
    );

    const common = {
      'data-ladder-chip': id,
      role: 'checkbox' as const,
      'aria-checked': checked,
      'aria-label': aria,
      title,
      style: {
        display: 'inline-flex', alignItems: 'center', minHeight: TOUCH, padding: 0,
        border: 'none', background: 'none', font: 'inherit', textAlign: 'left' as const,
        cursor: canToggle ? 'pointer' : 'default',
      },
    };
    return canToggle ? (
      <button key={id} type="button" {...common} onClick={() => onToggle?.(id)}>{pill}</button>
    ) : (
      <span key={id} {...common} aria-disabled="true">{pill}</span>
    );
  };

  const arrow = (key: string) => (
    <span key={key} aria-hidden="true" style={{ flex: 'none', textAlign: 'center', fontSize: FS.xs, color: C.textMuted }}>
      {isMobile ? '↓' : '→'}
    </span>
  );

  const nodes: React.ReactNode[] = [];
  ladder.columns.forEach((col, i) => {
    if (i > 0) nodes.push(arrow(`arr-${i}`));
    nodes.push(
      <div
        key={`col-${col.index}`}
        data-testid="workflow-ladder-column"
        style={{
          position: 'relative', flex: 'none', display: 'flex', flexDirection: 'column', gap: SP.xs,
          ...(col.parallel
            ? { padding: `18px ${SP.xs}px ${SP.xs}px`, border: `1px dashed ${C.border}`, borderRadius: R.xl }
            : {}),
        }}
      >
        {col.parallel && (
          <span
            style={{
              position: 'absolute', top: 2, left: SP.sm, right: SP.sm, fontSize: FS.xxs, lineHeight: '14px',
              color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            }}
          >
            {LADDER_PARALLEL}
          </span>
        )}
        {col.stageIds.map(id => chip(id, true))}
      </div>,
    );
  });

  return (
    <div data-testid="workflow-ladder" style={{ fontFamily: FONT.sans }}>
      <div
        ref={ladRef}
        role="group"
        aria-label="Маршрут по колонкам"
        style={{
          position: 'relative', display: 'flex', alignItems: isMobile ? 'stretch' : 'center',
          flexDirection: isMobile ? 'column' : 'row', gap: SP.xs, padding: SP.xxs,
          overflowX: isMobile ? 'visible' : 'auto',
          ...(farEdges.length > 0 ? (isMobile ? { paddingRight: SP.xl } : { paddingBottom: SP.xl }) : {}),
        }}
      >
        {farEdges.length > 0 && (
          <svg
            aria-hidden="true"
            width={svgSize.w}
            height={svgSize.h}
            style={{ position: 'absolute', left: 0, top: 0, pointerEvents: 'none', overflow: 'visible' }}
          >
            {arcs.length > 0 ? arcs.map(a => (
              <g key={a.key}>
                <path
                  data-testid="workflow-ladder-arc"
                  d={a.d}
                  fill="none"
                  stroke={C.textMuted}
                  strokeWidth={1.5}
                  strokeDasharray="3 4"
                  strokeLinecap="round"
                />
                {a.d && <circle cx={a.ex} cy={a.ey} r={2.5} fill={C.textMuted} />}
              </g>
            )) : farEdges.map(e => (
              <path
                key={`${e.from}>${e.to}`}
                data-testid="workflow-ladder-arc"
                d=""
                fill="none"
                stroke={C.textMuted}
                strokeWidth={1.5}
                strokeDasharray="3 4"
                strokeLinecap="round"
              />
            ))}
          </svg>
        )}
        {nodes}
      </div>

      {ladder.removed.length > 0 && (
        <div
          data-testid="workflow-ladder-removed"
          style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: SP.xs, marginTop: SP.sm }}
        >
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>Снято:</span>
          {ladder.removed.map(id => chip(id, false))}
        </div>
      )}

      <div data-testid="workflow-ladder-legend" style={{ marginTop: SP.sm, fontSize: FS.xs, color: C.textMuted }}>
        ☑ можно снять · пунктир — по решению · 🔒 — всегда в маршруте · «Снято» — нажмите, чтобы вернуть
      </div>
    </div>
  );
}

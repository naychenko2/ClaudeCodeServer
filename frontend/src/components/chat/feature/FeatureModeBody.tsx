import { useState, type ReactNode } from 'react';
import { ArrowRight } from 'lucide-react';
import { C, FONT, SHADOW, Z } from '../../../lib/design';
import { openTaskInSection } from '../../../lib/tasks';
import type { FeatureRun } from '../../../types/featureRun';
import type { TodoItem } from '../../../hooks/useSessionArtifacts';
import { useTurnPlanMode, useSelectedFeatureId, setTurnPlanMode, setSelectedFeatureId } from '../../../lib/featureRuns';
import { ModeSwitcher } from './ModeSwitcher';
import { FeaturePicker } from './FeaturePicker';
import { FeatureRunView } from './FeatureRunView';
import { TodoList } from '../TodoList';

// Общее ТЕЛО режима «План хода / Исполнение фичи» — одно и то же в двух поверхностях:
// в меню пилюли (TurnPlanPill) и в карточке плана в ленте (TodoPlanView). Как TodoList
// сейчас общий у меню и карточки: шапка (переключатель + счётчик) + выбор фичи + тело
// (FeatureRunView в режиме фичи, TodoList в режиме плана). Поверхности не расходятся
// в трактовке состояний.
//
// Режим и выбранная фича — из стора Ф1 (useTurnPlanMode/useSelectedFeatureId per sessionId),
// не из локального useState: выбор, сделанный в карточке, виден в меню пилюли и наоборот.
//
// variant:
//   'pill' — в меню: шапка (переключатель + счётчик) ЛИПКА (position: sticky, top 0),
//     тело (выбор фичи + FeatureRunView/TodoList) скроллится внутри меню;
//   'card' — в ленте: обычная колонка, шапка — часть карточки (не липкая), в режиме
//     фичи внизу футер «Задачи фичи в трекере →» (стиль футера TaskCreatedView).
//
// title — опциональная заставка перед переключателем в шапке: у карточки это
// SquareCheck + «План хода» (всегда), у пилюли — «План хода» только без фич
// (с фичами заголовок заменяет переключатель).
//
// Счётчик в шапке считает САМ по variant: у пилюли — доля решённых витков (%),
// у карточки — шаги принято/всего (stepsAccepted/stepsTotal); ↺ — из verdicts.loops.
// headerPad/bodyPad/headerBorder — геометрия шапки и тела: у пилюли и карточки
// отступы разные (9px 12px 11px против 10px 13px / 7px 13px 10px).
export function FeatureModeBody({ sessionId, todos, runs, isMobile, title, variant, headerPad, bodyPad, headerBorder }: {
  sessionId: string | null;
  todos: TodoItem[];
  runs: FeatureRun[];
  isMobile: boolean;
  title?: ReactNode;
  variant: 'pill' | 'card';
  headerPad?: string;
  bodyPad?: string;
  headerBorder?: string;
}) {
  const mode = useTurnPlanMode(sessionId);
  const selectedId = useSelectedFeatureId(sessionId);
  const hasFeatures = runs.length > 0;
  const featureMode = hasFeatures && mode === 'feature';

  // Выбранная фича: индекс по id (клампится, если фич стало меньше); нет выбора — runs[0].
  const sel = selectedId ? Math.max(0, runs.findIndex(r => r.featureId === selectedId)) : 0;
  const run = runs[Math.min(sel, Math.max(0, runs.length - 1))];

  // Счётчик шапки — по режиму и поверхности.
  const done = todos.filter(t => t.status === 'completed').length;
  let counter: string;
  if (featureMode && run) {
    const loops = run.verdicts.loops;
    if (variant === 'card') {
      // Карточка: шаги принято/всего; при нуле шагов «0/—».
      const total = run.stepsTotal ?? 0;
      counter = total > 0
        ? `${run.stepsAccepted ?? 0}/${total}${loops ? ` · ↺${loops}` : ''}`
        : `0/—${loops ? ` · ↺${loops}` : ''}`;
    } else {
      // Пилюля: доля решённых витков (%).
      counter = `${Math.round(run.progress * 100)}%${loops ? ` · ↺${loops}` : ''}`;
    }
  } else {
    counter = `${done}/${todos.length}`;
  }

  const body = featureMode && run
    ? (
      <>
        <FeaturePicker runs={runs} selected={sel} isMobile={isMobile} onSelect={i => {
          const r = runs[i];
          if (r && sessionId) setSelectedFeatureId(sessionId, r.featureId);
        }} />
        <FeatureRunView run={run} />
      </>
    )
    : <TodoList todos={todos} />;

  const controls = (
    <div style={{
      display: 'flex', alignItems: 'center', gap: 8, minWidth: 0,
      padding: headerPad,
      ...(headerBorder ? { borderBottom: headerBorder } : {}),
    }}>
      {title}
      <ModeSwitcher
        mode={mode}
        onMode={m => { if (sessionId) setTurnPlanMode(sessionId, m); }}
        hasFeatures={hasFeatures}
        isMobile={isMobile}
      />
      <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: 11, color: C.textMuted, flexShrink: 0 }}>
        {counter}
      </span>
    </div>
  );

  if (variant === 'pill') {
    // В меню: шапка (переключатель + счётчик) ЛИПКА (position: sticky, top 0) — прямой
    // потомок скролл-контейнера (карточки Menu, overflow: auto + maxHeight), тело под ней
    // прокручивается. Колонка без фиксированной высоты: высота = контенту, потолок — maxHeight.
    return (
      <div style={{ display: 'flex', flexDirection: 'column' }}>
        <div style={{ position: 'sticky', top: 0, background: C.bgWhite, zIndex: Z.dropdown }}>
          {controls}
        </div>
        <div style={{ padding: bodyPad }}>
          {body}
        </div>
      </div>
    );
  }

  // Карточка: обычная колонка, шапка — часть карточки (не липкая). В режиме фичи
  // внизу футер «Задачи фичи в трекере →» (стиль футера TaskCreatedView).
  return (
    <div style={{ display: 'flex', flexDirection: 'column' }}>
      {controls}
      <div style={{ padding: bodyPad }}>
        {body}
      </div>
      {featureMode && run && <FeatureCardFooter run={run} />}
    </div>
  );
}

// Футер карточки в режиме фичи: «Задачи фичи в трекере →» — стиль футера TaskCreatedView
// (12/600 C.accent, ArrowRight 13, верхняя граница 1px C.divider, фокус SHADOW.focus).
// Действие — openTaskInSection на эпик фичи (фильтр трекера по фиче — новый экран, за волной).
function FeatureCardFooter({ run }: { run: FeatureRun }) {
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);
  const open = () => openTaskInSection({ id: run.epic.id, projectId: run.epic.projectId ?? undefined });
  return (
    <button
      onClick={open}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      onFocus={() => setFocused(true)}
      onBlur={() => setFocused(false)}
      title="Задачи фичи в трекере"
      style={{
        display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 5,
        width: '100%', border: 'none', borderTop: `1px solid ${C.divider}`,
        padding: '8px 13px', background: 'transparent', cursor: 'pointer',
        fontSize: 12, fontWeight: 600, color: C.accent, fontFamily: 'inherit',
        boxShadow: focused ? SHADOW.focus : 'none',
      }}
    >
      Задачи фичи в трекере
      <span style={{ display: 'flex', transform: hovered ? 'translateX(3px)' : 'none', transition: 'transform 0.15s' }}>
        <ArrowRight size={13} strokeWidth={2} />
      </span>
    </button>
  );
}

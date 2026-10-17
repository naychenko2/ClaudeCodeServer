import { useState } from 'react';
import { ListChecks, Route } from 'lucide-react';
import { C, FONT } from '../../lib/design';
import { Menu } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { TodoList } from './TodoList';
import { planHint, type TodoItem } from '../../hooks/useSessionArtifacts';
import { useIsMobile } from '../../lib/breakpoints';
import type { FeatureRun } from '../../types/featureRun';
import { ModeSwitcher, type TurnPlanMode } from './feature/ModeSwitcher';
import { FeaturePicker } from './feature/FeaturePicker';
import { FeatureRunView } from './feature/FeatureRunView';

// Пилюля прогресса «Плана хода» и, когда у чата есть фичи, режима «Исполнение фичи».
//
// Два режима, один переключатель в шапке меню:
//   • «План хода» — прежний чек-лист (TodoList), байт-в-байт как раньше;
//   • «Исполнение фичи» — сводка по фиче: конвейер, шаги, витки.
// Режим по умолчанию — «фича», если она есть, иначе «план». Без фичи в чате
// переключатель не рендерится, и пилюля/меню остаются прежними.
//
// `runs` — агрегаты фич чата (из useFeatureRuns); `todos` — план хода. Пилюля
// показывается, когда есть хоть что-то: todos.length > 0 ИЛИ runs.length > 0.

// Подпись пилюли в режиме фичи: текущая стадия + прогресс + ↺.
function featurePillLabel(run: FeatureRun): string {
  if (run.state === 'wait') return 'ждёт вас · мерж';
  if (run.state === 'done' && run.merged) return 'Фича смержена';
  if (run.state === 'closed') return 'Спайк закрыт';
  const running = run.stages.find(s => s.status === 'run');
  const who = run.running.map(r => r.who).filter((w, i, a) => a.indexOf(w) === i).join(', ');
  return `${running?.name ?? 'Производство'}${who ? ` · ${who}` : ''}`;
}

export function TurnPlanPill({ todos, runs = [] }: { todos: TodoItem[]; runs?: FeatureRun[] }) {
  const isMobile = useIsMobile();
  const hasFeatures = runs.length > 0;
  // Режим по умолчанию: «фича», если она есть, иначе «план».
  const [mode, setMode] = useState<TurnPlanMode>(hasFeatures ? 'feature' : 'plan');
  const [selected, setSelected] = useState(0);
  const [menu, setMenu] = useState<DOMRect | null>(null);

  if (!todos.length && !hasFeatures) return null;

  const hint = planHint(todos);
  const done = todos.filter(t => t.status === 'completed').length;
  const planLabel = hint?.text ?? 'План выполнен';

  // Выбранная фича (индекс клампится, если фич стало меньше).
  const sel = Math.min(selected, Math.max(0, runs.length - 1));
  const run = runs[sel];
  const featureMode = hasFeatures && mode === 'feature';

  // Счётчик в шапке: по режиму.
  const counter = featureMode && run
    ? `${run.progress.done}/${run.progress.total}${run.ret ? ` · ↺${run.ret}` : ''}`
    : `${done}/${todos.length}`;

  // Текст кнопки-пилюли.
  let label = planLabel;
  let iconColor: string = C.accent;
  if (featureMode && run) {
    label = featurePillLabel(run);
    iconColor = run.state === 'wait' ? C.info : C.accent;
  } else if (!hint) {
    iconColor = C.textMuted;
  }

  // Потолок меню: режим фичи выше (560 мобила / 520 десктоп), план — как раньше (340).
  const menuMaxHeight = isMobile ? (featureMode ? 560 : 340) : (featureMode ? 520 : 340);

  return (
    <>
      <button
        onClick={e => setMenu(menu ? null : e.currentTarget.getBoundingClientRect())}
        title={featureMode ? 'Показать исполнение фичи' : 'Показать план хода'}
        style={{
          fontSize: 11, color: C.textMuted, fontFamily: FONT.mono,
          background: C.bgSelected, border: 'none', borderRadius: 8, padding: '4px 11px',
          display: 'flex', alignItems: 'center', gap: 7, cursor: 'pointer',
          maxWidth: 300, minWidth: 0, flexShrink: 1,
        }}
      >
        {featureMode ? (
          <Route size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={iconColor} style={{ flexShrink: 0 }} />
        ) : (
          <ListChecks size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={iconColor} style={{ flexShrink: 0 }} />
        )}
        <span style={{ flexShrink: 0 }}>{counter}</span>
        <span style={{ opacity: 0.45, flexShrink: 0 }}>·</span>
        <span style={{ minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {label}
        </span>
      </button>
      {menu && (
        <Menu onClose={() => setMenu(null)} anchor={menu} minWidth={280} maxWidth={380} maxHeight={menuMaxHeight}>
          <div style={{ padding: '9px 12px 11px' }}>
            <div style={{
              display: 'flex', alignItems: 'center', gap: 8, paddingBottom: 7, marginBottom: 5,
              borderBottom: `1px solid ${C.divider}`,
            }}>
              {hasFeatures ? (
                <ModeSwitcher mode={mode} onMode={setMode} hasFeatures={hasFeatures} />
              ) : (
                <span style={{ fontFamily: FONT.serif, fontSize: 13, fontWeight: 700, color: C.textHeading }}>
                  План хода
                </span>
              )}
              <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: 11, color: C.textMuted }}>
                {counter}
              </span>
            </div>
            {featureMode && run ? (
              <>
                <FeaturePicker runs={runs} selected={sel} onSelect={setSelected} />
                <FeatureRunView run={run} />
              </>
            ) : (
              <TodoList todos={todos} />
            )}
          </div>
        </Menu>
      )}
    </>
  );
}

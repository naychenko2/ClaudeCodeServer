import { useEffect, useState } from 'react';
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
// `runs` — агрегаты фич чата (из useFeatureRuns, бэковый FeatureRun); `todos` — план хода.
// Пилюля показывается, когда есть хоть что-то: todos.length > 0 ИЛИ runs.length > 0.

// Текущая стадия для подписи: по идущим задачам ролей (бэк уже сгруппировал).
function activeStageName(run: FeatureRun): string {
  if (run.steps.some(t => t.status === 'inProgress')) return 'Производство';
  if (run.reviews.some(t => t.status === 'inProgress')) return 'Ревью';
  if (run.qa.some(t => t.status === 'inProgress')) return 'Проверка';
  if (run.final && run.final.status === 'inProgress') return 'Финальное ревью';
  return 'План';
}

// Подпись пилюли в режиме фичи: состояние (ждёт вас / смержена / финал) или стадия.
function featurePillLabel(run: FeatureRun): string {
  if (run.awaitingHumanSince) return 'ждёт вас · мерж';
  if (run.merge && run.merge.status === 'done') return 'Фича смержена';
  if (run.ready) return 'Финал зелёный';
  return activeStageName(run);
}

export function TurnPlanPill({ todos, runs = [] }: { todos: TodoItem[]; runs?: FeatureRun[] }) {
  const isMobile = useIsMobile();
  const hasFeatures = runs.length > 0;
  // Режим по умолчанию: «фича», если она есть, иначе «план».
  const [mode, setMode] = useState<TurnPlanMode>(hasFeatures ? 'feature' : 'plan');
  // M4: пересчёт режима при изменении НАЛИЧИЯ фич (не только на mount): фичи
  // появились — дефолт «фича», исчезли — «план». Ручной выбор пользователя
  // сохраняется, пока наличие фич не меняется.
  useEffect(() => {
    setMode(hasFeatures ? 'feature' : 'plan');
  }, [hasFeatures]);
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

  // Счётчик в шапке: по режиму. Прогресс фичи — доля решённых витков (0..1 → %),
  // ↺ — число витков возврата (бэк: verdicts.loops).
  const counter = featureMode && run
    ? `${Math.round(run.progress * 100)}%${run.verdicts.loops ? ` · ↺${run.verdicts.loops}` : ''}`
    : `${done}/${todos.length}`;

  // Текст кнопки-пилюли.
  let label = planLabel;
  let iconColor: string = C.accent;
  if (featureMode && run) {
    label = featurePillLabel(run);
    iconColor = run.awaitingHumanSince ? C.info : C.accent;
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

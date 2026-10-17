import { useState } from 'react';
import { ListChecks, Route } from 'lucide-react';
import { C, FONT } from '../../lib/design';
import { Menu } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { planHint, type TodoItem } from '../../hooks/useSessionArtifacts';
import { useIsMobile } from '../../lib/breakpoints';
import { useTurnPlanMode } from '../../lib/featureRuns';
import type { FeatureRun } from '../../types/featureRun';
import { TodoList } from './TodoList';
import { FeatureModeBody } from './feature/FeatureModeBody';

// Пилюля прогресса «Плана хода» и, когда у чата есть фичи, режима «Исполнение фичи».
//
// Кнопка-пилюля всегда показывает runs[0] (порядок отсортировал бэк) и НЕ слушает
// выбор фичи в теле (Ф1): выбранный в меню/карточке режим и фичу она не отражает.
// Режим по умолчанию — «фича», если она есть, иначе «план»; хранится в сторе Ф1
// (useTurnPlanMode per sessionId), поэтому выбор в карточке виден и здесь, и наоборот.
// Без фичи в чате переключатель не рендерится, и пилюля/меню остаются прежними.
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

export function TurnPlanPill({ todos, runs = [], sessionId }: {
  todos: TodoItem[];
  runs?: FeatureRun[];
  sessionId: string | null;
}) {
  const isMobile = useIsMobile();
  const mode = useTurnPlanMode(sessionId);
  const hasFeatures = runs.length > 0;
  const [menu, setMenu] = useState<DOMRect | null>(null);

  if (!todos.length && !hasFeatures) return null;

  const hint = planHint(todos);
  const done = todos.filter(t => t.status === 'completed').length;
  const planLabel = hint?.text ?? 'План выполнен';

  // Кнопка-пилюля: всегда runs[0] (порядок отсортировал бэк), выбор в теле не слушает.
  const run0 = runs[0];
  const featureMode = hasFeatures && mode === 'feature';

  // Счётчик кнопки: по режиму. Прогресс фичи — доля решённых витков (%), ↺ — возвраты.
  const counter = featureMode && run0
    ? `${Math.round(run0.progress * 100)}%${run0.verdicts.loops ? ` · ↺${run0.verdicts.loops}` : ''}`
    : `${done}/${todos.length}`;

  // Текст кнопки-пилюли.
  let label = planLabel;
  let iconColor: string = C.accent;
  if (featureMode && run0) {
    label = featurePillLabel(run0);
    iconColor = run0.awaitingHumanSince ? C.info : C.accent;
  } else if (!hint) {
    iconColor = C.textMuted;
  }

  // Потолок меню: режим фичи выше (520 десктоп / 560 мобила, во всю ширину left/right 8),
  // план — как раньше (340, обычная ширина).
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
        hasFeatures ? (
          // Есть фичи: общее тело (переключатель + выбор фичи + FeatureRunView/TodoList),
          // шапка липкая, в режиме фичи меню выше и во всю ширину.
          <Menu
            onClose={() => setMenu(null)}
            anchor={menu}
            minWidth={280}
            maxWidth={380}
            maxHeight={menuMaxHeight}
            fullWidth={featureMode}
          >
            <FeatureModeBody
              sessionId={sessionId}
              todos={todos}
              runs={runs}
              isMobile={isMobile}
              variant="pill"
              headerPad="9px 12px 11px"
              bodyPad="9px 12px 11px"
              headerBorder={`1px solid ${C.divider}`}
            />
          </Menu>
        ) : (
          // Без фич — прежняя разметка меню (байт-в-байт): заголовок «План хода» + чек-лист.
          <Menu onClose={() => setMenu(null)} anchor={menu} minWidth={280} maxWidth={380} maxHeight={340}>
            <div style={{ padding: '9px 12px 11px' }}>
              <div style={{
                display: 'flex', alignItems: 'center', gap: 8, paddingBottom: 7, marginBottom: 5,
                borderBottom: `1px solid ${C.divider}`,
              }}>
                <span style={{ fontFamily: FONT.serif, fontSize: 13, fontWeight: 700, color: C.textHeading }}>
                  План хода
                </span>
                <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: 11, color: C.textMuted }}>
                  {counter}
                </span>
              </div>
              <TodoList todos={todos} />
            </div>
          </Menu>
        )
      )}
    </>
  );
}

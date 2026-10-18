import { useEffect, useRef, useState } from 'react';
import { ListChecks, Route } from 'lucide-react';
import { C, FONT } from '../../lib/design';
import { Menu } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { planHint, type TodoItem } from '../../hooks/useSessionArtifacts';
import { useIsMobile } from '../../lib/breakpoints';
import { useTurnPlanMode, usePillMenuOpen } from '../../lib/featureRuns';
import type { FeatureRun } from '../../types/featureRun';
import { TodoList } from './TodoList';
import { FeatureModeBody } from './feature/FeatureModeBody';
import { buildPillModel, type PillModel } from './feature/pillModel';
import { FeatureGlyph } from './feature/FeatureGlyphs';

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
//
// Вся логика 5 состояний (идёт/ждёт вас/исполнитель упал/закрыта/все закрыты) — в чистой
// функции buildPillModel (feature/pillModel.ts): компонент только рисует модель.

// Значок пилюли в режиме фичи: ромб (wait) / квадрат (stopped) / Route (идёт/закрыта).
function FeaturePillIcon({ model }: { model: PillModel }) {
  if (model.icon === 'diamond') return <FeatureGlyph kind="wait" />;
  if (model.icon === 'square') return <FeatureGlyph kind="stopped" />;
  return <Route size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={model.iconColor} />;
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
  const pillBtnRef = useRef<HTMLButtonElement>(null);

  // F9: одноразовый сигнал «открыть меню» (стор Ф1, per chat): чип «Шаг N фичи»
  // (TaskCreatedView) шлёт его, когда в ленте нет живой карточки плана. Потреблённый
  // seq запоминается — ререндер сам по себе меню не переоткрывает.
  const pillMenu = usePillMenuOpen(sessionId);
  const pillMenuSeq = useRef(0);
  useEffect(() => {
    if (!pillMenu || pillMenu.seq === pillMenuSeq.current) return;
    pillMenuSeq.current = pillMenu.seq;
    const rect = pillBtnRef.current?.getBoundingClientRect();
    if (rect) setMenu(rect);
  }, [pillMenu]);

  if (!todos.length && !hasFeatures) return null;

  const hint = planHint(todos);
  const done = todos.filter(t => t.status === 'completed').length;
  const planLabel = hint?.text ?? 'План выполнен';

  // Кнопка-пилюля: всегда runs[0] (порядок отсортировал бэк), выбор в теле не слушает.
  const featureMode = hasFeatures && mode === 'feature';
  const model = buildPillModel(runs);

  // Счётчик: по режиму. Фича — шаги принято/всего (8/9) + ↺ + +N; план — done/total.
  const planCounter = `${done}/${todos.length}`;
  const featureCounter = model && model.counter != null
    ? `${model.counter}${model.loops > 0 ? ` ↺${model.loops}` : ''}`
    : null;
  const counter = featureMode && featureCounter != null ? featureCounter : planCounter;

  // Текст/цвет/вес кнопки-пилюли: по модели (фича) или по плану.
  const label = featureMode && model ? model.text : planLabel;
  const iconColor = featureMode && model ? model.iconColor : (hint ? C.accent : C.textMuted);
  const textColor = featureMode && model ? model.textColor : null;
  const textWeight = featureMode && model ? model.textWeight : 400;
  const loops = featureMode && model ? model.loops : 0;
  const loopsHot = featureMode && model ? model.loopsHot : false;
  const more = featureMode && model ? model.more : 0;
  const word = featureMode && model ? model.word : null;

  // Точка-сигнал: пилюля в режиме «План хода», а фича ждёт/упала — сигнал не теряется.
  const showDot = !featureMode && model != null && (model.state === 'wait' || model.state === 'stopped');
  const dotColor = model?.iconColor ?? C.info;
  const ariaLabel = (featureMode ? 'Показать исполнение фичи' : 'Показать план хода')
    + (showDot && model?.ariaExtra ? ` · ${model.ariaExtra}` : '');

  // Потолок меню: режим фичи выше (520 десктоп / 560 мобила, во всю ширину left/right 8),
  // план — как раньше (340, обычная ширина).
  const menuMaxHeight = isMobile ? (featureMode ? 560 : 340) : (featureMode ? 520 : 340);

  return (
    <>
      <button
        ref={pillBtnRef}
        onClick={e => setMenu(menu ? null : e.currentTarget.getBoundingClientRect())}
        title={featureMode ? 'Показать исполнение фичи' : 'Показать план хода'}
        aria-label={ariaLabel}
        style={{
          fontSize: 11, color: C.textMuted, fontFamily: FONT.mono,
          background: C.bgSelected, border: 'none', borderRadius: 8, padding: '4px 11px',
          display: 'flex', alignItems: 'center', gap: 7, cursor: 'pointer',
          maxWidth: 300, minWidth: 0, flexShrink: 1,
        }}
      >
        {/* Значок (+ точка-сигнал, когда пилюля в режиме плана, а фича ждёт/упала). */}
        <span style={{ position: 'relative', display: 'flex', flexShrink: 0 }}>
          {featureMode && model
            ? <FeaturePillIcon model={model} />
            : <ListChecks size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={iconColor} />}
          {showDot && (
            <span style={{
              position: 'absolute', right: -3, top: -3, width: 6, height: 6,
              borderRadius: '50%', background: dotColor, boxShadow: `0 0 0 1.5px ${C.bgSelected}`,
            }} />
          )}
        </span>
        {/* Слово-метка режима: на мобиле (360 px) скрыто — значок и так отличает режим. */}
        {word && !isMobile && (
          <>
            <span style={{ flexShrink: 0 }}>{word}</span>
            <span style={{ opacity: 0.45, flexShrink: 0 }}>·</span>
          </>
        )}
        {/* Счётчик шагов + ↺ + +N (без счётчика — «все закрыты», N>1). */}
        {counter != null && (
          <>
            <span style={{ flexShrink: 0 }}>{counter}</span>
            {loops > 0 && (
              <span style={{
                flexShrink: 0,
                ...(loopsHot ? { color: C.warningText, fontWeight: 700 } : {}),
              }}>↺{loops}</span>
            )}
            {more > 0 && (
              <span style={{ fontFamily: FONT.mono, fontSize: 11, opacity: 0.8, flexShrink: 0 }}>+{more}</span>
            )}
            <span style={{ opacity: 0.45, flexShrink: 0 }}>·</span>
          </>
        )}
        {/* Текст состояния: обрезается многоточием, цвет/вес — по модели. */}
        <span style={{
          minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          ...(textColor ? { color: textColor } : {}),
          fontWeight: textWeight,
        }}>
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
            <div data-testid="turn-plan-menu" style={{ padding: '9px 12px 11px' }}>
              <div style={{
                display: 'flex', alignItems: 'center', gap: 8, paddingBottom: 7, marginBottom: 5,
                borderBottom: `1px solid ${C.divider}`,
              }}>
                <span style={{ fontFamily: FONT.serif, fontSize: 13, fontWeight: 700, color: C.textHeading }}>
                  План хода
                </span>
                <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: 11, color: C.textMuted }}>
                  {planCounter}
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

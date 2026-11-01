import { useEffect, useRef, useState } from 'react';
import { ListChecks, Route } from 'lucide-react';
import { C, FONT, FS } from '../../lib/design';
import { Menu } from '../ui';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { planHint, type TodoItem } from '../../hooks/useSessionArtifacts';
import { useIsMobile } from '../../lib/breakpoints';
import { useTurnPlanMode, usePillMenuOpen, useFeatureRunsLoading, useFeatureRunsError } from '../../lib/featureRuns';
import type { FeatureRun } from '../../types/featureRun';
import { TodoList } from './TodoList';
import { FeatureModeBody } from './feature/FeatureModeBody';
import { buildPillModel, type PillTone } from './feature/pillModel';

// Пилюля прогресса «Плана хода» и, когда у чата есть фичи, режима «Исполнение фичи».
//
// Кнопка-пилюля всегда показывает runs[0] (порядок отсортировал бэк) и НЕ слушает
// выбор фичи в теле (Ф1): выбранный в меню/карточке режим и фичу она не отражает.
// Режим по умолчанию — «фича», если она есть, иначе «план»; хранится в сторе Ф1
// (useTurnPlanMode per sessionId), поэтому выбор в карточке виден и здесь, и наоборот.
// Без фичи в чате переключатель не рендерится, и пилюля/меню остаются прежними.
//
// `runs` — агрегаты фич чата (из useFeatureRuns, бэковый FeatureRun); `todos` — план хода.
// Пилюля показывается, когда есть хоть что-то: todos, фичи, или загрузка/ошибка фич.
//
// F2 (макет v4 §П2 «Мой шаг»): в режиме фичи пилюля говорит про ШАГ ЭТОГО ЧАТА
// («Ш8 · идёт · 24 мин») — фича целиком уже показана в строке шапки В2, повторять её
// пилюля не должна. Чат без шага — «фича · {pct} %». Тревога фичи заменяет текст
// («Ждёт вас · {Имя}» warn / «Стоит · {Имя}» danger) и ставит точку-сигнал у значка.
// Загрузка/ошибка (run ещё нет) — «фича · …». Вся логика — в чистой функции
// buildPillModel (feature/pillModel.ts): компонент только рисует модель.

// Цвет текста по тону — тоны В2: «ждёт вас» — warningText, «стоит» — dangerText
// (синий C.info был языком старой плашки; в языке фичи «нужен ответ» — warn).
function toneColor(tone: PillTone): string | null {
  if (tone === 'wait') return C.warningText;
  if (tone === 'stop') return C.dangerText;
  if (tone === 'ok') return C.successText;
  return null;
}

// n-2/n-3: на десктопе меню пилюли — фиксированная ширина 380 в обоих режимах
// («План хода» и «Исполнение фичи»), чтобы переключатель режима не дёргал ширину
// (раньше фича 336, план 280–296 — по содержимому). min=max → ровно 380. На мобиле —
// прежние min/max (280–380); фактическая ширина на мобиле — во всю (pillMenuFullWidth).
export function pillMenuWidths(isMobile: boolean): { minWidth: number; maxWidth: number } {
  return isMobile ? { minWidth: 280, maxWidth: 380 } : { minWidth: 380, maxWidth: 380 };
}

// r-3: fullWidth меню (left/right 8, min/max не действуют): на МОБИЛЕ — в обоих режимах
// (фича и план), чтобы переключение режима не дёргало ширину (раньше фича fullWidth,
// план по содержимому 280–296); на ДЕСКТОПЕ — false (фикс. 380 из pillMenuWidths).
export function pillMenuFullWidth(isMobile: boolean, _featureMode: boolean): boolean {
  return isMobile;
}

export function TurnPlanPill({ todos, runs = [], sessionId, chatTaskId }: {
  todos: TodoItem[];
  runs?: FeatureRun[];
  sessionId: string | null;
  // Задача-владелец чата (Session.TaskId): по ней роль «этот чат — …» в мете попапа (R2 major 5).
  chatTaskId?: string | null;
}) {
  const isMobile = useIsMobile();
  const mode = useTurnPlanMode(sessionId);
  const loading = useFeatureRunsLoading(sessionId);
  const error = useFeatureRunsError(sessionId);
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

  // Пилюля видна, когда есть хоть что-то: план, фичи, или загрузка/ошибка запроса фич.
  if (!todos.length && !hasFeatures && !loading && error == null) return null;

  const hint = planHint(todos);
  const done = todos.filter(t => t.status === 'completed').length;
  const planLabel = hint?.text ?? 'План выполнен';

  // Кнопка-пилюля: всегда runs[0] (порядок отсортировал бэк), выбор в теле не слушает.
  // Режим фичи — когда есть фичи ИЛИ идёт загрузка/ошибка (скелет «фича · …»).
  const featureMode = (hasFeatures || loading || error != null) && mode === 'feature';
  const model = buildPillModel(runs, chatTaskId, loading, error);

  // Счётчик плана хода (done/total) — только в режиме «План хода»; в режиме фичи
  // счётчика нет (макет: пилюля — значок + текст состояния).
  const planCounter = `${done}/${todos.length}`;

  // Точка-сигнал: пилюля в режиме «План хода», а фича ждёт/упала — сигнал не теряется.
  const showDot = !featureMode && model != null && (model.tone === 'wait' || model.tone === 'stop');
  const dotColor = model?.tone === 'stop' ? C.danger : C.warning;
  const ariaLabel = (featureMode ? 'Показать исполнение фичи' : 'Показать план хода')
    + (showDot && model?.ariaExtra ? ` · ${model.ariaExtra}` : '');
  // Тон текста в режиме фичи (null — нейтральный, цвет кнопки).
  const featureTone = featureMode && model ? toneColor(model.tone) : null;

  // Потолок меню: режим фичи выше (520 десктоп / 560 мобила; во всю ширину left/right 8 —
  // только мобила), план — как раньше (340).
  const menuMaxHeight = isMobile ? (featureMode ? 560 : 340) : (featureMode ? 520 : 340);
  // n-2/n-3: ширина меню — одна у обоих режимов (380 на десктопе, 280–380 на мобиле).
  const { minWidth: menuMin, maxWidth: menuMax } = pillMenuWidths(isMobile);

  return (
    <>
      <button
        ref={pillBtnRef}
        onClick={e => setMenu(menu ? null : e.currentTarget.getBoundingClientRect())}
        title={featureMode ? 'Показать исполнение фичи' : 'Показать план хода'}
        aria-label={ariaLabel}
        style={{
          // R-7: в режиме фичи текст и счётчик пилюли — C.textSecondary (4,02:1), не C.textMuted.
          fontSize: FS.xs, color: featureMode ? C.textSecondary : C.textMuted, fontFamily: FONT.mono,
          background: C.bgSelected, border: 'none', borderRadius: 8, padding: '4px 11px',
          display: 'flex', alignItems: 'center', gap: 7, cursor: 'pointer',
          maxWidth: 300, minWidth: 0, flexShrink: 1,
          // R-8: высота пилюли на 360 — 28 (макет .app.m .tpp); на десктопе — по контенту.
          ...(isMobile ? { minHeight: 28 } : {}),
        }}
      >
        {/* Значок: Route (режим фичи, макет) / ListChecks (режим плана) + точка-сигнал. */}
        <span style={{ position: 'relative', display: 'flex', flexShrink: 0 }}>
          {featureMode
            ? <Route size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            : <ListChecks size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} color={hint ? C.accent : C.textMuted} />}
          {showDot && (
            <span style={{
              position: 'absolute', right: -3, top: -3, width: 6, height: 6,
              borderRadius: '50%', background: dotColor, boxShadow: `0 0 0 1.5px ${C.bgSelected}`,
            }} />
          )}
        </span>
        {/* Режим фичи: текст по state (префикс + жирная часть + суффикс, тон).
            Режим плана: счётчик + приглушённый «·» + подпись (как прежде). Обрезается многоточием. */}
        {featureMode && model ? (
          <span style={{
            minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            ...(featureTone ? { color: featureTone } : {}),
          }}>
            {model.pre}
            {model.bold && <b style={{ color: C.textHeading, fontWeight: 600 }}>{model.bold}</b>}
            {model.post}
          </span>
        ) : (
          <>
            <span style={{ flexShrink: 0 }}>{planCounter}</span>
            <span style={{ opacity: 0.45, flexShrink: 0 }}>·</span>
            <span style={{
              minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            }}>{planLabel}</span>
          </>
        )}
      </button>
      {menu && (
        // Контекст фичи (есть фичи ИЛИ идёт загрузка/ошибка): общее тело
        // (переключатель + выбор фичи + FeatureRunView/скелет/TodoList); шапка липкая,
        // в режиме фичи меню выше и во всю ширину. Без контекста — простой план (байт-в-байт).
        (hasFeatures || loading || error != null) ? (
          <Menu
            onClose={() => setMenu(null)}
            anchor={menu}
            minWidth={menuMin}
            maxWidth={menuMax}
            maxHeight={menuMaxHeight}
            // r-3: во всю ширину (left/right 8) — на мобиле в обоих режимах (одна ширина);
            // на десктопе — фикс. 380 (fullWidth=false).
            fullWidth={pillMenuFullWidth(isMobile, featureMode)}
          >
            <FeatureModeBody
              sessionId={sessionId}
              todos={todos}
              runs={runs}
              isMobile={isMobile}
              // R2 major 5: роль «этот чат — …» и цепочка шага — по задаче-владельцу чата.
              chatTaskId={chatTaskId}
              // R2 major 3: переход (чат/задача) в теле закрывает попап.
              onClose={() => setMenu(null)}
              // M-2: шапка padding-bottom 7 + (margin-bottom 5 даёт bodyPad-top 5); тело top 0 (макет .hd/.in).
              headerPad="9px 12px 7px"
              bodyPad="5px 12px 11px"
              headerBorder={`1px solid ${C.divider}`}
            />
          </Menu>
        ) : (
          // Без фич — прежняя разметка меню (байт-в-байт): заголовок «План хода» + чек-лист.
          // r-3: на мобиле — во всю ширину (одна ширина с режимом фичи); на десктопе — фикс. 380.
          <Menu onClose={() => setMenu(null)} anchor={menu} minWidth={menuMin} maxWidth={menuMax} maxHeight={340} fullWidth={pillMenuFullWidth(isMobile, false)}>
            <div data-testid="turn-plan-menu" style={{ padding: '9px 12px 11px' }}>
              <div style={{
                display: 'flex', alignItems: 'center', gap: 8, paddingBottom: 7, marginBottom: 5,
                borderBottom: `1px solid ${C.divider}`,
              }}>
                <span style={{ fontFamily: FONT.serif, fontSize: FS.base, fontWeight: 700, color: C.textHeading }}>
                  План хода
                </span>
                <span style={{ marginLeft: 'auto', fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>
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

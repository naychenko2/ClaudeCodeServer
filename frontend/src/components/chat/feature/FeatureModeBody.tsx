import { useEffect, useRef, useState, type ReactNode } from 'react';
import { ArrowRight } from 'lucide-react';
import { C, FS, SHADOW, TB, Z } from '../../../lib/design';
import { openTaskInSection } from '../../../lib/tasks';
import type { FeatureRun } from '../../../types/featureRun';
import type { TodoItem } from '../../../hooks/useSessionArtifacts';
import { useTurnPlanMode, useSelectedFeatureId, setTurnPlanMode, setSelectedFeatureId, useFeatureStepFocus, useFeatureRunsLoading, useFeatureRunsError, refetchFeatureRuns } from '../../../lib/featureRuns';
import { ModeSwitcher } from './ModeSwitcher';
import { FeaturePicker } from './FeaturePicker';
import { FeatureRunView } from './FeatureRunView';
import { TodoList } from '../TodoList';

// F-pop-a: заглушка FeatureRun для состояния загрузки/ошибки (run ещё не пришёл):
// тело рисует скелет/ошибку, run не читается.
const EMPTY_RUN: FeatureRun = {
  featureId: '',
  state: 'not_started',
  steps: [], reviews: [], qa: [], final: null, merge: null,
  verdicts: { accepted: 0, acceptedWithRemarks: 0, returned: 0, withdrawn: 0, loops: 0 },
  progress: 0, ready: false,
};

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
export function FeatureModeBody({ sessionId, todos, runs, isMobile, title, variant, headerPad, bodyPad, headerBorder, chatTaskId }: {
  sessionId: string | null;
  todos: TodoItem[];
  runs: FeatureRun[];
  isMobile: boolean;
  title?: ReactNode;
  variant: 'pill' | 'card';
  headerPad?: string;
  bodyPad?: string;
  headerBorder?: string;
  // Задача текущего чата (Session.TaskId): по ней роль чата в мете (F-pop-a).
  chatTaskId?: string | null;
}) {
  const mode = useTurnPlanMode(sessionId);
  const selectedId = useSelectedFeatureId(sessionId);
  // F-pop-a: состояния запроса фич (загрузка → скелет, ошибка → «Повторить»).
  const loading = useFeatureRunsLoading(sessionId);
  const error = useFeatureRunsError(sessionId);
  const hasFeatures = runs.length > 0;
  // Режим фичи — когда есть фичи ИЛИ идёт загрузка/ошибка (скелет/ошибка в теле).
  const featureMode = (hasFeatures || loading || error != null) && mode === 'feature';

  // F9: сигнал фокуса чипа «Шаг N фичи» (стор Ф1, per chat): переключить режим на
  // «Исполнение фичи» и выбрать фичу из сигнала. Одноразовый: потреблённый seq
  // запоминается — ререндер не сбрасывает ручной выбор пользователя, сигнал другого
  // чата (другой sessionId) сюда не доходит.
  const focus = useFeatureStepFocus(sessionId);
  const focusSeq = useRef(0);
  useEffect(() => {
    if (!focus || focus.seq === focusSeq.current) return;
    focusSeq.current = focus.seq;
    if (sessionId) {
      setTurnPlanMode(sessionId, 'feature');
      setSelectedFeatureId(sessionId, focus.featureId);
    }
  }, [focus, sessionId]);

  // Выбранная фича: индекс по id (клампится, если фич стало меньше); нет выбора — runs[0].
  const sel = selectedId ? Math.max(0, runs.findIndex(r => r.featureId === selectedId)) : 0;
  const run = runs[Math.min(sel, Math.max(0, runs.length - 1))];

  // F-pop-a: подвал «Открыть страницу фичи →» — на «Хронологию» (cc-open-feature,
  // WorkspacePage слушает; fromChat — крошка «← К чату»).
  const onOpenPage = () => {
    if (!run) return;
    window.dispatchEvent(new CustomEvent('cc-open-feature', {
      detail: { featureId: run.featureId, fromChatId: sessionId ?? undefined, fromChat: true },
    }));
  };
  // «Повторить» (ошибка запроса): явный перезапрос фич чата.
  const onRetry = () => { if (sessionId) refetchFeatureRuns(sessionId); };

  const body = featureMode && run
    ? (
      <>
        <FeaturePicker runs={runs} selected={sel} isMobile={isMobile} inMenu={variant === 'pill'} onSelect={i => {
          const r = runs[i];
          if (r && sessionId) setSelectedFeatureId(sessionId, r.featureId);
        }} />
        <FeatureRunView
          run={run}
          chatTaskId={chatTaskId}
          onOpenPage={onOpenPage}
        />
      </>
    )
    : featureMode
    ? (
      // Загрузка/ошибка без готового run: скелет или «Повторить» (тело режима фичи).
      <FeatureRunView
        run={runs[0] ?? EMPTY_RUN}
        chatTaskId={chatTaskId}
        loading={loading}
        error={error}
        onRetry={onRetry}
      />
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
      {featureMode && run && <FeatureCardFooter run={run} isMobile={isMobile} />}
    </div>
  );
}

// Футер карточки в режиме фичи: «Задачи фичи в трекере →» — стиль футера TaskCreatedView
// (12/600 C.accent, ArrowRight 13, верхняя граница 1px C.divider, фокус SHADOW.focus).
// Действие — openTaskInSection на эпик фичи (фильтр трекера по фиче — новый экран, за волной).
function FeatureCardFooter({ run, isMobile }: { run: FeatureRun; isMobile: boolean }) {
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);
  // п.32: epic nullable-терпимо — без эпика действие недоступно (guard, не каст).
  const open = () => { if (!run.epic) return; openTaskInSection({ id: run.epic.id, projectId: run.epic.projectId ?? undefined }); };
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
        // M-7: на 360 тач-цель 40 (TB.iconHitMobile); отступы 8px 13px не трогаем.
        minHeight: isMobile ? TB.iconHitMobile : undefined,
        padding: '8px 13px', background: 'transparent', cursor: 'pointer',
        fontSize: FS.sm, fontWeight: 600, color: C.accent, fontFamily: 'inherit',
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

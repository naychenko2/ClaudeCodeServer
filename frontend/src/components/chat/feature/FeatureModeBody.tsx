import { useEffect, useRef, type ReactNode } from 'react';
import { C, FS, R, SP, Z } from '../../../lib/design';
import type { FeatureRun } from '../../../types/featureRun';
import type { TodoItem } from '../../../hooks/useSessionArtifacts';
import { useTurnPlanMode, useSelectedFeatureId, setTurnPlanMode, setSelectedFeatureId, useFeatureStepFocus, useFeatureRunsLoading, useFeatureRunsError, refetchFeatureRuns } from '../../../lib/featureRuns';
import { ModeSwitcher } from './ModeSwitcher';
import { FeaturePicker } from './FeaturePicker';
import { FeatureStepView } from './FeatureStepView';
import { FeatureTruthBody } from './FeatureTruthBody';
import { chatRoleFromRun, findChatChain } from './featureTruthModel';
import { TodoList } from '../TodoList';

// Общее ТЕЛО режима «План хода / Исполнение фичи» — в меню пилюли (TurnPlanPill):
// шапка (переключатель режима) + выбор фичи + тело (в режиме фичи — П2 «Мой шаг» для
// чата шага или П1-сводка для чата без шага; в режиме плана — TodoList).
//
// F2 (макет v4): FeatureRunView с аккордеоном этапов удалён — фича в чате живёт в трёх
// неповторяющихся поверхностях: плашка С1 сравнивает фичи, шапка В2 показывает фичу
// этого чата, пилюля и этот попап — шаг этого чата.
//
// Режим и выбранная фича — из стора Ф1 (useTurnPlanMode/useSelectedFeatureId per sessionId),
// не из локального useState: выбор, сделанный в карточке, виден в меню пилюли и наоборот.
//
// title — опциональная заставка перед переключателем в шапке: «План хода» только без фич
// (с фичами заголовок заменяет переключатель).
// headerPad/bodyPad/headerBorder — геометрия шапки и тела (9px 12px 11px).
export function FeatureModeBody({ sessionId, todos, runs, isMobile, title, headerPad, bodyPad, headerBorder, chatTaskId, onClose }: {
  sessionId: string | null;
  todos: TodoItem[];
  runs: FeatureRun[];
  isMobile: boolean;
  title?: ReactNode;
  headerPad?: string;
  bodyPad?: string;
  headerBorder?: string;
  // Задача текущего чата (Session.TaskId): по ней роль чата в мете и цепочка его шага.
  chatTaskId?: string | null;
  // R2 major 3: переход (чат/задача) в теле закрывает попап; передаёт вызывающий
  // (попап пилюли).
  onClose?: () => void;
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

  // Тело режима фичи (F2, макет v4): чат ШАГА — П2 «Мой шаг» (FeatureStepView: карточка
  // шага, его цепочка, одна строка фичи); чат БЕЗ шага (оркестратор, ревью, свободный) —
  // П1-сводка (FeatureTruthBody: «Сейчас», оценка, трекер, расхождения). Аккордеона
  // этапов нет нигде: этапы — на странице фичи («Хронология»), туда ведёт подвал.
  const chain = run ? findChatChain(run, chatTaskId) : null;
  const body = featureMode && run
    ? (
      <>
        <FeaturePicker runs={runs} selected={sel} isMobile={isMobile} inMenu onSelect={i => {
          const r = runs[i];
          if (r && sessionId) setSelectedFeatureId(sessionId, r.featureId);
        }} />
        {chain ? (
          <FeatureStepView
            run={run}
            chain={chain}
            sessionId={sessionId}
            chatTaskId={chatTaskId}
            isMobile={isMobile}
            onOpenPage={onOpenPage}
            onClose={onClose ?? (() => {})}
          />
        ) : (
          <FeatureTruthBody
            run={run}
            sessionId={sessionId}
            isMobile={isMobile}
            roleText={chatRoleFromRun(run, chatTaskId)}
            onOpenPage={onOpenPage}
          />
        )}
      </>
    )
    : featureMode
    ? (
      // Загрузка/ошибка без готового run: скелет 3 строк или «Повторить» (состояния
      // режима — здесь, тела их не дублируют; EMPTY_RUN не читается).
      loading ? (
        <div data-testid="feature-mode-skeleton" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, padding: '2px 0' }}>
          {[0, 1, 2].map(i => <div key={i} style={{ height: 14, borderRadius: R.sm, background: C.bgInset }} />)}
        </div>
      ) : (
        <div data-testid="feature-mode-error" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.sm, color: C.textSecondary, padding: '2px 0' }}>
          Не загрузилось
          <button type="button" onClick={onRetry} style={{ border: 'none', background: 'transparent', cursor: 'pointer', fontFamily: 'inherit', fontSize: FS.sm, fontWeight: 600, color: C.accent, padding: 0 }}>Повторить</button>
        </div>
      )
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
        // R2 major 4: переключатель — когда есть контекст фичи: фичи в списке
        // ИЛИ загрузка/ошибка запроса (иначе на скелете/ошибке переключиться нечем).
        show={hasFeatures || loading || error != null}
        isMobile={isMobile}
      />
    </div>
  );

  // В меню: шапка (переключатель) ЛИПКА (position: sticky, top 0) — прямой
  // потомок скролл-контейнера (карточки Menu, overflow: auto + maxHeight), тело под ней
  // прокручивается. Колонка без фиксированной высоты: высота = контенту, потолок — maxHeight.
  // (Второй поверхности — «карточки панели А» — больше нет: F2 убрал её вместе со старой
  // плашкой; variant и футер «Задачи фичи в трекере →» сняты как мёртвый код.)
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

// Вкладка «Разбор» одной страницей (волна 4, FS-FR; записка docs/mockups/feature-page-simplify-v4-clickable.md
// §1 сц. 5, §5, §7 FS-FR): одна прокручиваемая страница без сегментов — итог → источник и
// полнота цифр → якоря → шесть секций по порядку REVIEW_SECTIONS. Шаг СОБИРАЕТ готовые части
// (FS-FR0–FS-FR6): своих блоков, своих расчётов и своего состояния нет — данные приходят пропами.
// Порядок и заголовки секций — из REVIEW_SECTIONS (FeatureReviewParts.tsx:18), не дублируются здесь:
// якорь и секция ссылаются одним ключом (контракт «rz-<ключ REVIEW_SECTIONS>»).
// Пустые состояния — как у «Статистики» (FeatureStatsTab.tsx:236–267): причина отсутствия, а не нули.

import { FONT } from '../../lib/design';
import { EmptyState } from '../ui/EmptyState';
import { usePersonas } from '../../lib/personas';
import { type NavMarks, type FeatureNavCtx } from '../../lib/featureNavMarks';
import { FeatureAnalysisLine } from './FeatureAnalysisLine';
import { AccuracyBadges } from './FeatureStatsTab';
import { buildTimelineModel } from './FeatureStatsBits';
import { REVIEW_SECTIONS, ReviewAnchors, ReviewSection, type ReviewSectionId } from './FeatureReviewParts';
import { FeatureReviewTime } from './FeatureReviewTime';
import { FeatureReviewEtaScale } from './FeatureReviewEtaScale';
import { FeatureReviewVolume } from './FeatureReviewVolume';
import { FeatureReviewReturns } from './FeatureReviewReturns';
import { FeatureReviewWho } from './FeatureReviewWho';
import { FeatureReviewSpend } from './FeatureReviewSpend';
import { FeatureReviewHistory } from './FeatureReviewHistory';
import { type PersonaNameResolver } from './FeatureBriefModel';
import type { FeatureStats } from '../../types/featureStats';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureBrief } from '../../lib/featurePage';
import type { FeatureEvent } from '../../types/featureIndex';

export interface FeatureReviewTabProps {
  // Конвейер фичи (features/{id} → run): строка итога и все шесть секций.
  run: FeatureRun;
  // Статистика фичи (features/{id} → stats); null — бэк ещё не посчитал (старые данные).
  stats: FeatureStats | null;
  // Сводка страницы — для строк «по оценке» в шкале срока (FS-FR1b); null — шкала только «типичная фича».
  brief: FeatureBrief | null;
  // Лента событий страницы (at desc) и их общее число — единственный источник «Хронологии» (FS-FR6).
  events: FeatureEvent[];
  eventsTotal: number;
  // Фича и её название: точка возврата переходов «Хронологии» и «Возвратов».
  featureId: string;
  featureTitle?: string | null;
  // Проект фичи: задачи её эпика открываются в разделе проекта (дефект ee6825ff).
  projectId?: string;
  // Пометки «в архиве»/«задача закрыта» (buildNavMarks страницы).
  marks?: NavMarks;
  // Тач-цели ≥ 40 на 360: без этого пропа якоря тихо получают 32 px вместо 40 (контракт ревью FS-FR0).
  isMobile?: boolean;
}

export function FeatureReviewTab({
  run, stats, brief, events, eventsTotal, featureId, featureTitle = null, projectId,
  marks, isMobile = false,
}: FeatureReviewTabProps) {
  // Хук выше ранних return: имя оценщика нужно шкале срока, хук не может быть условным.
  // Подписи персон — тот же источник, что на «Статистике» (FeatureStatsTab.tsx:228).
  const personas = usePersonas();
  const nameOf: PersonaNameResolver = (id) => personas.find((p) => p.id === id)?.name ?? null;
  const nav: FeatureNavCtx = { featureId, featureTitle, marks };

  // Строка итога (с кнопкой «Резюме фичи» внутри) — показываем и в пустых состояниях вкладки,
  // поэтому до ранних return (тот же порядок, что FeatureStatsTab.tsx:233).
  const line = <FeatureAnalysisLine run={run} marks={marks} isMobile={isMobile} />;

  // Пустое состояние: stats нет (старые данные) — причина, а не нули. Якорей и секций нет:
  // считать нечего, якорить нечего.
  if (!stats) {
    return (
      <>
        {line}
        <EmptyState
          compact
          icon="📊"
          title="Статистики пока нет"
          subtitle="Появится, когда будет посчитано время фичи."
        />
      </>
    );
  }
  const st = stats;

  // Пустое состояние: модель без данных (ни сегментов, ни текущего состояния) — тот же
  // признак hasData, что у «Статистики» (FeatureStatsTab.tsx:252–267).
  const model = buildTimelineModel(st.timeline, st.source, st.truncated, st.timeline.current == null);
  if (!model.hasData) {
    return (
      <>
        {line}
        <EmptyState
          compact
          icon="📊"
          title="Считать пока нечего"
          subtitle="Статистика появится с первой задачей исполнителя."
        />
      </>
    );
  }

  // Клик якоря — только прокрутка к секции: вкладка и стор не меняются, «Разбор» всегда
  // открывается сверху (номера якоря нигде не хранятся).
  const jump = (id: ReviewSectionId) => {
    document.getElementById('rz-' + id)?.scrollIntoView({ block: 'start' });
  };

  // Тело секции — по её ключу; заголовок и порядок приходят из REVIEW_SECTIONS.
  const body = (id: ReviewSectionId) => {
    switch (id) {
      case 'time':
        return (
          <FeatureReviewTime
            run={run}
            stats={st}
            isMobile={isMobile}
            etaScale={<FeatureReviewEtaScale brief={brief} stats={st} nameOf={nameOf} isMobile={isMobile} />}
          />
        );
      case 'volume':
        return <FeatureReviewVolume run={run} stats={st} isMobile={isMobile} />;
      case 'returns':
        return <FeatureReviewReturns run={run} stats={st} nav={nav} isMobile={isMobile} />;
      case 'who':
        return <FeatureReviewWho run={run} stats={st} nav={nav} isMobile={isMobile} />;
      case 'spend':
        return <FeatureReviewSpend run={run} stats={st} isMobile={isMobile} />;
      case 'history':
        return (
          <FeatureReviewHistory
            run={run}
            stats={st}
            events={events}
            eventsTotal={eventsTotal}
            featureId={featureId}
            featureTitle={featureTitle}
            projectId={projectId}
            marks={marks}
            isMobile={isMobile}
          />
        );
    }
  };

  return (
    <div data-testid="feature-review-tab" style={{ fontFamily: FONT.sans }}>
      {line}
      <AccuracyBadges source={st.source} truncated={st.truncated} />
      <ReviewAnchors items={REVIEW_SECTIONS} onJump={jump} isMobile={isMobile} />
      {REVIEW_SECTIONS.map((s) => (
        <ReviewSection key={s.id} id={s.id} title={s.label}>
          {body(s.id)}
        </ReviewSection>
      ))}
    </div>
  );
}

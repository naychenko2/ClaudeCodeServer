// Первый экран страницы фичи (FS-FS, макет v4 §1 сц. 2 и 2′): плашка «от тебя»,
// «Объём | Срок» (только на «Ходе») или «Итоги» закрытой фичи; на загрузке — скелет,
// на сбое — «Не загрузилось · Повторить». Только раскладка и состояния: данные —
// из brief/run как есть, переходы приходят пропами от страницы.

import { useEffect, useRef, useState, type ReactNode } from 'react';
import { AlertTriangle, RotateCw } from 'lucide-react';
import { C, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import type { FeatureBrief, FeatureMainTab } from '../../lib/featurePage';
import { ensurePersonasLoaded, getPersonaById, usePersonasVersion } from '../../lib/personas';
import { Button } from '../ui/Button';
import { Notice } from '../ui/Notice';
import { FeatureAskBlock } from './FeatureAskBlock';
import { FeatureVolumeCard } from './FeatureVolumeCard';
import { FeatureEtaCard } from './FeatureEtaCard';
import { FeatureClosedSummary } from './FeatureClosedSummary';
import type { FeatureRun } from '../../types/featureRun';

export interface FeatureFirstScreenProps {
  status: 'loading' | 'error' | 'ok';
  brief?: FeatureBrief | null;
  run?: FeatureRun | null;
  mainTab: FeatureMainTab;
  isMobile: boolean;
  // «Ответить …»; не передан — кнопки на плашке нет.
  onAnswer?: () => void;
  // «из чего N ›» у «Объёма» → вкладка «Ход».
  onOpenSteps?: () => void;
  // «Разбор ›» у «Итогов».
  onOpenReview?: () => void;
  onRetry: () => void;
  // Блок состояния «пауза / черновик / очередь» (со своими действиями) — вместо плашки «от тебя».
  stateBlock?: ReactNode;
}

const SKELETON_BLOCK = { background: C.bgSelected, borderRadius: R.lg } as const;

function Skeleton({ isMobile }: { isMobile: boolean }) {
  return (
    <div data-testid="feature-page-skeleton" style={{ display: 'flex', flexDirection: 'column', gap: isMobile ? SP.sm : SP.md }}>
      <div className="cc-feature-page-skeleton" style={{ ...SKELETON_BLOCK, height: 56 }} />
      <div style={{ display: 'grid', gridTemplateColumns: isMobile ? 'minmax(0, 1fr)' : 'repeat(2, minmax(0, 1fr))', gap: isMobile ? SP.sm : SP.md }}>
        <div className="cc-feature-page-skeleton" style={{ ...SKELETON_BLOCK, height: 96 }} />
        <div className="cc-feature-page-skeleton" style={{ ...SKELETON_BLOCK, height: 96 }} />
      </div>
    </div>
  );
}

export function FeatureFirstScreen({
  status, brief, run, mainTab, isMobile, onAnswer, onOpenSteps, onOpenReview, onRetry, stateBlock,
}: FeatureFirstScreenProps) {
  // getPersonaById не реактивен — подписка на версию стора, как у FeatureBriefWithNames.
  usePersonasVersion();
  useEffect(() => { void ensurePersonasLoaded(); }, []);

  // Повтор: перезапрос страницы сам двойной клик не гасит — блокируем до смены данных.
  const [retrying, setRetrying] = useState(false);
  const retryGuard = useRef(false);
  useEffect(() => { retryGuard.current = false; setRetrying(false); }, [status, brief]);
  const retry = () => {
    if (retryGuard.current) return;
    retryGuard.current = true;
    setRetrying(true);
    onRetry();
  };

  if (status === 'loading') return <Skeleton isMobile={isMobile} />;

  if (status === 'error' || !run) {
    return (
      <Notice
        tone="danger"
        icon={AlertTriangle}
        title="Не загрузилось"
        action={
          <Button size="xs" variant="ghost" disabled={retrying} dataTestId="first-screen-retry"
            style={{ background: C.bgWhite, ...(isMobile ? { minHeight: 40 } : {}) }} onClick={retry}>
            <RotateCw size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
            Повторить
          </Button>
        }
      >
        Сервер не ответил; список и чаты работают как обычно.
      </Notice>
    );
  }

  // Старый ответ без brief: плашку и карточки строить не из чего — только блок состояния.
  if (!brief) return <>{stateBlock}</>;

  if (brief.kind === 'closed' || brief.kind === 'merged') {
    return run.summary ? (
      <FeatureClosedSummary
        summary={run.summary}
        initialEtaMinutes={brief.initialEtaMinutes}
        tailsCount={run.postMergeTails?.length ?? 0}
        followUp={run.stepProgress?.followUpTotal ? { total: run.stepProgress.followUpTotal, accepted: run.stepProgress.followUpAccepted ?? 0 } : null}
        isMobile={isMobile}
        onOpenReview={onOpenReview}
      />
    ) : null;
  }

  const nameOf = (id: string) => getPersonaById(id)?.name ?? '';
  const gap = isMobile ? SP.sm : SP.md;
  return (
    <div data-testid="feature-first-screen" style={{ display: 'flex', flexDirection: 'column', gap }}>
      {stateBlock ?? <FeatureAskBlock ask={brief.ask} nameOf={nameOf} isMobile={isMobile} onAnswer={onAnswer} />}
      {mainTab === 'progress' && (
        <div
          data-testid="feature-first-screen-cards"
          style={{ display: 'grid', gridTemplateColumns: isMobile ? 'minmax(0, 1fr)' : 'repeat(2, minmax(0, 1fr))', gap }}
        >
          {run.stepProgress && <FeatureVolumeCard progress={run.stepProgress} isMobile={isMobile} onOpenSteps={onOpenSteps} />}
          <FeatureEtaCard
            eta={brief.eta ?? null}
            noteAt={brief.note?.at}
            noteByPersonaId={brief.note?.byPersonaId}
            noteStale={brief.note?.stale}
            elapsedMinutes={brief.elapsedMinutes}
            nameOf={(id) => getPersonaById(id)?.name ?? null}
            isMobile={isMobile}
          />
        </div>
      )}
    </div>
  );
}

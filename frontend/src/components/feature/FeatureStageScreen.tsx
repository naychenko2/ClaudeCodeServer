// Экран выбранного этапа (D1c) под графом процесса: выбор задачи этапа, затем её экран по
// GET /api/tasks/{id}/stage — артефакты, вход и транскрипт, ревью, метрики.
//
// Устаревший ответ: состояние помечено taskId, запрос несёт AbortSignal, а ответ отменённого
// запроса не применяется — быстрое переключение A → B не покажет данные A, а данные A не
// затрут уже пришедшее B. Пока состояние чужой задачи, рисуется загрузка. Отказ запроса показывается в области экрана текстом
// с «Повторить», необработанных rejection нет.

import { useEffect, useState } from 'react';
import { AlertTriangle } from 'lucide-react';
import { SP, FS, C } from '../../lib/design';
import { Button } from '../ui/Button';
import { Notice } from '../ui/Notice';
import { api } from '../../lib/api';
import { FeatureStageArtifacts } from './FeatureStageArtifacts';
import { FeatureStageInput } from './FeatureStageInput';
import { FeatureStageReview } from './FeatureStageReview';
import { FeatureStageMetrics } from './FeatureStageMetrics';
import type { StageScreen } from '../../types/featureStage';

export interface StageScreenTask { id: string; title: string; stepRef?: string | null }

type LoadState = { taskId: string } & (
  | { status: 'loading' }
  | { status: 'ok'; screen: StageScreen }
  | { status: 'error'; message: string });

function StageBody({ taskId, isMobile }: { taskId: string; isMobile: boolean }) {
  const [loaded, setState] = useState<LoadState>({ taskId, status: 'loading' });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const { signal } = controller;
    setState({ taskId, status: 'loading' });
    api.tasks.stage(taskId, signal)
      .then(screen => {
        if (signal.aborted) return;
        setState({ taskId, status: 'ok', screen });
      })
      .catch((e: unknown) => {
        if (signal.aborted) return;
        setState({ taskId, status: 'error', message: (e instanceof Error && e.message) || 'Не удалось загрузить экран этапа' });
      });
    return () => controller.abort();
  }, [taskId, attempt]);

  const state: LoadState = loaded.taskId === taskId ? loaded : { taskId, status: 'loading' };
  if (state.status === 'loading') {
    return <div data-testid="stage-screen-loading" style={{ fontSize: FS.sm, color: C.textMuted }}>Загружаю экран этапа…</div>;
  }
  if (state.status === 'error') {
    return (
      <Notice tone="danger" icon={AlertTriangle} data-testid="stage-screen-error"
        action={<Button size="xs" variant="ghost" style={isMobile ? { minHeight: 40 } : undefined} onClick={() => setAttempt(n => n + 1)}>Повторить</Button>}>
        {state.message}
      </Notice>
    );
  }
  const s = state.screen;
  return (
    <div data-testid="stage-screen-body" data-task-id={s.taskId} style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
      <FeatureStageArtifacts key={s.taskId} taskId={s.taskId} artifacts={s.artifacts} isMobile={isMobile} />
      <FeatureStageInput input={s.input} isMobile={isMobile} />
      <FeatureStageReview review={s.review} />
      <FeatureStageMetrics metrics={s.metrics} />
    </div>
  );
}

export function FeatureStageScreen({ tasks, isMobile }: { tasks: StageScreenTask[]; isMobile: boolean }) {
  // Выбор задачи этапа: явный — только если такая задача ещё в списке, иначе первая
  const [picked, setPicked] = useState<string | null>(null);
  if (tasks.length === 0) return null;
  const taskId = tasks.some(t => t.id === picked) ? picked! : tasks[0].id;
  return (
    <div data-testid="stage-screen" style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
      {tasks.length > 1 && (
        <div data-testid="stage-screen-picker" role="group" aria-label="Задача этапа" style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
          {tasks.map(t => (
            <Button key={t.id} size="xs" variant={t.id === taskId ? 'primary' : 'ghost'}
              style={isMobile ? { minHeight: 40 } : undefined}
              dataTestId={`stage-screen-pick-${t.id}`}
              onClick={() => setPicked(t.id)}>
              {t.stepRef || t.title}
            </Button>
          ))}
        </div>
      )}
      <StageBody taskId={taskId} isMobile={isMobile} />
    </div>
  );
}

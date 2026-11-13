// Блок «Артефакты» экрана этапа (D1c): коммиты шага и доработок как версии (sha, сообщение,
// файлы), дифф коммита по клику (ленивая загрузка), linkedFiles и resultMarkdown.
//
// Дифф: один запрос на коммит — пока он идёт, повторный клик ничего не шлёт (inFlight);
// готовый дифф кэшируется в состоянии, повторное раскрытие запроса не делает. Отказ
// (404/сеть) показывается под своим коммитом, остальные коммиты и блоки живут.
// Устаревший ответ: при уходе с задачи (размонтирование, родитель ключует по taskId)
// запросы отменяются, а ответ отменённого запроса не применяется.

import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { Button } from '../ui/Button';
import { Badge } from '../ui/Badge';
import { Notice } from '../ui/Notice';
import { AlertTriangle } from 'lucide-react';
import { DiffView } from '../DiffView';
import { api } from '../../lib/api';
import { formatDateTime } from './FeatureStatsBits';
import type { StageArtifacts, StageCommit, StageCommitDiff } from '../../types/featureStage';

export const SEC_LABEL: CSSProperties = {
  fontSize: FS.xxs, textTransform: 'uppercase', letterSpacing: 0.6, color: C.textMuted, fontFamily: FONT.sans,
};

type DiffState =
  | { status: 'loading' }
  | { status: 'ok'; diff: StageCommitDiff }
  | { status: 'error'; message: string };

function errorText(e: unknown): string {
  const m = e instanceof Error ? e.message : '';
  return m || 'Не удалось загрузить дифф';
}

function CommitRow({ commit, state, open, isMobile, onToggle }: {
  commit: StageCommit; state: DiffState | undefined; open: boolean; isMobile: boolean; onToggle: () => void;
}) {
  const at = commit.at ? formatDateTime(commit.at) : '';
  return (
    <div data-testid={`stage-commit-${commit.sha}`} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <Badge tone="neutral" size="xs">v{commit.version}</Badge>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{commit.shortSha}</span>
        <span style={{ flex: '1 1 160px', minWidth: 0, fontSize: FS.sm, color: C.textHeading, overflowWrap: 'anywhere' }}>
          {commit.subject}
        </span>
        {at && <span style={{ fontSize: FS.xs, color: C.textMuted }}>{at}</span>}
        <Button size="xs" variant="ghost" style={isMobile ? { minHeight: 40 } : undefined}
          dataTestId={`stage-commit-toggle-${commit.sha}`}
          ariaLabel={`${open ? 'Скрыть' : 'Показать'} дифф коммита ${commit.shortSha}`}
          loading={state?.status === 'loading'}
          onClick={onToggle}>
          {open ? 'Скрыть дифф' : 'Дифф'}
        </Button>
      </div>
      {commit.files.length > 0 && (
        <div data-testid={`stage-commit-files-${commit.sha}`}
          style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, overflowWrap: 'anywhere' }}>
          {commit.files.join(', ')}
        </div>
      )}
      {open && state?.status === 'error' && (
        <Notice tone="danger" icon={AlertTriangle} data-testid={`stage-commit-error-${commit.sha}`}>{state.message}</Notice>
      )}
      {open && state?.status === 'ok' && (
        <div data-testid={`stage-commit-diff-${commit.sha}`} style={{ overflowX: 'auto', minWidth: 0 }}>
          {state.diff.truncated && (
            <div style={{ fontSize: FS.xs, color: C.warningText }}>
              Дифф обрезан на {state.diff.maxChars} знаках.
            </div>
          )}
          <DiffView diff={state.diff.diff} />
        </div>
      )}
    </div>
  );
}

export function FeatureStageArtifacts({ taskId, artifacts, isMobile }: {
  taskId: string; artifacts: StageArtifacts; isMobile: boolean;
}) {
  const [diffs, setDiffs] = useState<Record<string, DiffState>>({});
  const [openSha, setOpenSha] = useState<Record<string, boolean>>({});
  // Запросы в полёте: повторный клик не шлёт второй. Ref, а не состояние — проверка синхронная.
  const inFlight = useRef(new Set<string>());
  const controller = useRef<AbortController | null>(null);

  useEffect(() => () => { controller.current?.abort(); }, []);

  const toggle = (sha: string) => {
    const nowOpen = !openSha[sha];
    setOpenSha(p => ({ ...p, [sha]: nowOpen }));
    if (!nowOpen) return;
    // Готовый дифф не перезапрашиваем; упавший — перезапрашиваем (повтор по клику)
    if (inFlight.current.has(sha) || diffs[sha]?.status === 'ok') return;
    inFlight.current.add(sha);
    controller.current ??= new AbortController();
    const { signal } = controller.current;
    setDiffs(p => ({ ...p, [sha]: { status: 'loading' } }));
    api.tasks.stageCommitDiff(taskId, sha, signal)
      .then(diff => {
        if (signal.aborted) return;
        setDiffs(p => ({ ...p, [sha]: { status: 'ok', diff } }));
      })
      .catch((e: unknown) => {
        if (signal.aborted) return;
        setDiffs(p => ({ ...p, [sha]: { status: 'error', message: errorText(e) } }));
      })
      .finally(() => { inFlight.current.delete(sha); });
  };

  const { commits, commitsAvailable, linkedFiles, resultMarkdown } = artifacts;
  return (
    <section data-testid="stage-artifacts" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      <div style={SEC_LABEL}>Артефакты</div>
      {commits.length === 0 ? (
        <div data-testid="stage-commits-empty" style={{ fontSize: FS.sm, color: C.textSecondary }}>
          {commitsAvailable ? 'Коммитов с меткой шага пока нет.' : 'Коммиты недоступны: дерево задачи не найдено.'}
        </div>
      ) : (
        commits.map(c => (
          <CommitRow key={c.sha} commit={c} state={diffs[c.sha]} open={!!openSha[c.sha]} isMobile={isMobile}
            onToggle={() => toggle(c.sha)} />
        ))
      )}
      {linkedFiles.length > 0 && (
        <div data-testid="stage-linked-files" style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
          <div style={SEC_LABEL}>Файлы задачи</div>
          {linkedFiles.map(f => (
            <span key={f} style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, overflowWrap: 'anywhere' }}>{f}</span>
          ))}
        </div>
      )}
      {resultMarkdown && (
        <div data-testid="stage-result" style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
          <div style={SEC_LABEL}>Итог</div>
          <div style={{
            fontSize: FS.sm, color: C.textPrimary, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere',
            padding: SP.sm, background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
          }}>
            {resultMarkdown}
          </div>
        </div>
      )}
    </section>
  );
}

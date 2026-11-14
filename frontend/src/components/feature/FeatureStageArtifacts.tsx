// Блок «Артефакты» экрана этапа (D1c): коммиты шага и доработок как версии (sha, сообщение,
// файлы), дифф коммита по клику (ленивая загрузка), linkedFiles и resultMarkdown.
// Артефакт — файл коммита, привязанный задачей файл или итог (D1): привязанные файлы и итог
// видны и без коммитов; пустое состояние — только когда нет ничего.
//
// Дифф: один запрос на коммит — пока он идёт, повторный клик ничего не шлёт (inFlight);
// готовый дифф кэшируется в состоянии, повторное раскрытие запроса не делает. Отказ
// (404/сеть) показывается под своим коммитом, остальные коммиты и блоки живут; упавший
// дифф перезапрашивается без смены ключа — кнопкой «Повторить».
// Устаревший ответ: при уходе с задачи (размонтирование, родитель ключует по taskId)
// запросы отменяются, а ответ отменённого запроса не применяется.

import { useEffect, useRef, useState, type CSSProperties } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { Button } from '../ui/Button';
import { Badge } from '../ui/Badge';
import { Notice } from '../ui/Notice';
import { EmptyState } from '../ui/EmptyState';
import { InlineSegmented } from '../ui/InlineSegmented';
import { AlertTriangle, FileText } from 'lucide-react';
import { DiffView } from '../DiffView';
import { api } from '../../lib/api';
import { formatDateTime } from './FeatureStatsBits';
import type { StageArtifacts, StageCommit, StageCommitDiff, StageFile, StageFileDiff } from '../../types/featureStage';

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

// Привязанные задачей файлы (без версий и диффов): видны и при отсутствии коммитов (D1)
function LinkedFilesBlock({ files }: { files: string[] }) {
  return (
    <div data-testid="stage-linked-files" style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
      <div style={SEC_LABEL}>Файлы задачи</div>
      {files.map(f => (
        <span key={f} style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, overflowWrap: 'anywhere' }}>{f}</span>
      ))}
    </div>
  );
}

// Прежний вид (сервер без artifacts.files): коммиты с диффом на каждый
function LegacyArtifacts({ taskId, artifacts, isMobile }: {
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
      {linkedFiles.length > 0 && <LinkedFilesBlock files={linkedFiles} />}
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

// ── Новый вид: файлы с версиями слева, дифф выбранного файла справа ─────────────────────────────

const FILES_COLLAPSED = 10;

function filesWord(n: number): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return 'файл';
  if (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) return 'файла';
  return 'файлов';
}

type FileDiffState =
  | { status: 'loading' }
  | { status: 'ok'; diff: StageFileDiff }
  | { status: 'error'; message: string };

interface DiffMode { id: string; label: string; from: string; to: string }

// Режимы разницы по числу версий: «v1» = изменение первой версии, «v(N-1) → vN» = между двумя
// последними, «всё» = всё изменение файла за этап
export function diffModes(file: StageFile): DiffMode[] {
  const n = file.versions.length;
  const modes: DiffMode[] = [];
  if (n >= 2) {
    modes.push({ id: 'first', label: 'v1', from: 'all', to: '1' });
    modes.push({ id: 'last', label: `v${n - 1} → v${n}`, from: String(n - 1), to: String(n) });
  }
  modes.push({ id: 'all', label: 'всё', from: 'all', to: 'all' });
  return modes;
}

const diffKey = (path: string, from: string, to: string) => `${path}\u0000${from}\u0000${to}`;

function ResultCard({ markdown }: { markdown: string }) {
  return (
    <div data-testid="stage-result" style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
      <div style={SEC_LABEL}>Итог исполнителя</div>
      <div style={{
        fontSize: FS.sm, color: C.textPrimary, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere',
        padding: SP.sm, background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
      }}>
        {markdown}
      </div>
    </div>
  );
}

// Список коммитов (shortSha + subject): вспомогательный блок при файлах и при их отсутствии
function CommitsList({ commits, style }: { commits: StageCommit[]; style?: CSSProperties }) {
  return (
    <div data-testid="stage-commits" style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs, ...style }}>
      <div style={SEC_LABEL}>Коммиты</div>
      {commits.map(c => (
        <div key={c.sha} style={{ display: 'flex', gap: SP.xs, alignItems: 'baseline', minWidth: 0 }}>
          <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}>{c.shortSha}</span>
          <span style={{ fontSize: FS.xs, color: C.textSecondary, overflowWrap: 'anywhere', minWidth: 0 }}>{c.subject}</span>
        </div>
      ))}
    </div>
  );
}

function FilesArtifacts({ taskId, artifacts, files, isMobile }: {
  taskId: string; artifacts: StageArtifacts; files: StageFile[]; isMobile: boolean;
}) {
  const [selPath, setSelPath] = useState<string | null>(null);
  const [modeId, setModeId] = useState('all');
  const [expanded, setExpanded] = useState(false);
  const [diffs, setDiffs] = useState<Record<string, FileDiffState>>({});
  // Счётчик повтора: упавший ключ перезапрашивается без смены ключа (кнопка «Повторить»)
  const [reloadNonce, setReloadNonce] = useState(0);
  const inFlight = useRef(new Set<string>());
  const controller = useRef<AbortController | null>(null);
  useEffect(() => () => { controller.current?.abort(); }, []);

  const file = files.find(f => f.path === selPath) ?? files[0] ?? null;
  const modes = file ? diffModes(file) : [];
  const mode = modes.find(m => m.id === modeId) ?? modes[modes.length - 1];
  const key = file && mode ? diffKey(file.path, mode.from, mode.to) : null;
  const state = key ? diffs[key] : undefined;

  // Ответ кладётся под СВОИМ ключом (path+from+to): быстрый переход на другой файл или режим
  // не даёт запоздавшему ответу попасть в чужой дифф — читаем всегда по текущему ключу.
  useEffect(() => {
    if (!file || !mode || !key) return;
    if (inFlight.current.has(key) || diffs[key]?.status === 'ok') return;
    inFlight.current.add(key);
    controller.current ??= new AbortController();
    const { signal } = controller.current;
    setDiffs(p => ({ ...p, [key]: { status: 'loading' } }));
    api.tasks.stageFileDiff(taskId, file.path, mode.from, mode.to, signal)
      .then(diff => { if (!signal.aborted) setDiffs(p => ({ ...p, [key]: { status: 'ok', diff } })); })
      .catch((e: unknown) => { if (!signal.aborted) setDiffs(p => ({ ...p, [key]: { status: 'error', message: errorText(e) } })); })
      .finally(() => { inFlight.current.delete(key); });
    // diffs читаем только как кэш; повторный запуск эффекта по нему не нужен.
    // reloadNonce — повтор по кнопке «Повторить» на том же ключе (упавший дифф).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, taskId, reloadNonce]);

  const visible = expanded ? files : files.slice(0, FILES_COLLAPSED);
  const hidden = files.length - visible.length;
  const lastVersion = file?.versions[file.versions.length - 1];
  const caption = lastVersion?.afterReturn
    ? `v${lastVersion.n} — после возврата Глеба. Версия файла = коммит этапа, который его тронул`
    : 'Версия файла = коммит этапа, который его тронул';

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
      <div style={{ display: 'flex', flexDirection: isMobile ? 'column' : 'row', gap: SP.md, alignItems: 'stretch', minWidth: 0 }}>
        <div style={{ flex: isMobile ? undefined : '0 0 280px', display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
          <div style={SEC_LABEL}>Файлы</div>
          {visible.map(f => {
            const active = f.path === file?.path;
            return (
              <button key={f.path} type="button" data-testid={`stage-file-${f.path}`} aria-pressed={active}
                onClick={() => { setSelPath(f.path); setModeId('all'); }}
                style={{
                  display: 'flex', flexDirection: 'column', gap: SP.xxs, textAlign: 'left', cursor: 'pointer',
                  padding: SP.sm, minHeight: isMobile ? 40 : undefined, minWidth: 0,
                  background: active ? C.accentLight : C.bgCard, borderRadius: R.md,
                  border: `1px solid ${active ? C.accent : C.borderLight}`, fontFamily: FONT.sans,
                }}>
                <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textHeading, overflowWrap: 'anywhere' }}>{f.path}</span>
                <span style={{ display: 'flex', gap: SP.xxs, flexWrap: 'wrap' }}>
                  {[...f.versions].reverse().map(v => (
                    <Badge key={v.n} tone={v.afterReturn ? 'warning' : 'neutral'} size="xs">v{v.n}</Badge>
                  ))}
                </span>
              </button>
            );
          })}
          {hidden > 0 && (
            <Button size="xs" variant="ghost" dataTestId="stage-files-more" style={isMobile ? { minHeight: 40 } : undefined}
              onClick={() => setExpanded(true)}>ещё {hidden} {filesWord(hidden)}</Button>
          )}
          {artifacts.commits.length > 0 && <CommitsList commits={artifacts.commits} style={{ marginTop: SP.sm }} />}
        </div>
        {file && (
          <div data-testid="stage-file-diff-pane" style={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
              <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading, overflowWrap: 'anywhere', minWidth: 0 }}>{file.path}</span>
              <InlineSegmented value={mode?.id ?? null} isMobile={isMobile}
                options={modes.map(m => ({ value: m.id, label: m.label }))} onChange={setModeId} />
            </div>
            <div data-testid="stage-file-caption" style={{ fontSize: FS.xs, color: C.textMuted }}>{caption}</div>
            {state?.status === 'loading' && <div style={{ fontSize: FS.sm, color: C.textSecondary }}>Загружаю дифф…</div>}
            {state?.status === 'error' && (
              <Notice tone="danger" icon={AlertTriangle} data-testid="stage-file-diff-error"
                action={
                  <Button size="xs" variant="ghost" dataTestId="stage-file-diff-retry"
                    style={isMobile ? { minHeight: 40 } : undefined}
                    onClick={() => setReloadNonce(n => n + 1)}>Повторить</Button>
                }>{state.message}</Notice>
            )}
            {state?.status === 'ok' && (
              <div data-testid="stage-file-diff" style={{ overflowX: 'auto', minWidth: 0 }}>
                {state.diff.truncated && (
                  <div style={{ fontSize: FS.xs, color: C.warningText }}>Дифф обрезан на {state.diff.maxChars} знаках.</div>
                )}
                <DiffView diff={state.diff.diff} />
              </div>
            )}
          </div>
        )}
      </div>
      {artifacts.resultMarkdown && <ResultCard markdown={artifacts.resultMarkdown} />}
    </div>
  );
}

export function FeatureStageArtifacts({ taskId, artifacts, isMobile }: {
  taskId: string; artifacts: StageArtifacts; isMobile: boolean;
}) {
  if (!artifacts.files) return <LegacyArtifacts taskId={taskId} artifacts={artifacts} isMobile={isMobile} />;
  const { files, linkedFiles, resultMarkdown, commits } = artifacts;
  // Артефакт — файл коммита, привязанный задачей файл или итог (D1): пустое состояние —
  // только когда нет ни коммитов, ни привязанных файлов, ни итога
  const hasArtifacts = files.length > 0 || linkedFiles.length > 0 || commits.length > 0 || !!resultMarkdown;
  return (
    <section data-testid="stage-artifacts" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      <div style={SEC_LABEL}>Артефакты</div>
      {files.length > 0 ? (
        <FilesArtifacts taskId={taskId} artifacts={artifacts} files={files} isMobile={isMobile} />
      ) : hasArtifacts ? (
        <>
          {linkedFiles.length > 0 && <LinkedFilesBlock files={linkedFiles} />}
          {commits.length > 0 && <CommitsList commits={commits} />}
          {resultMarkdown && <ResultCard markdown={resultMarkdown} />}
        </>
      ) : (
        <div data-testid="stage-artifacts-empty">
          <EmptyState compact inline icon={<FileText size={20} />} title="Артефактов пока нет"
            subtitle="Появятся с первым коммитом (CCS-Task) или итогом задачи" />
        </div>
      )}
    </section>
  );
}

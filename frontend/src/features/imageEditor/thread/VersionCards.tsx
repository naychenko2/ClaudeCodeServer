// Версии картинки в ленте (изменение 27.09 к ADR-019, прототип полос, вариант C): каждый
// запуск ИИ — строка запуска внизу ленты и по карточке на каждый вариант. «В работе» —
// рамка на версии, от которой пойдёт следующая правка; старые версии выше живые:
// «Открыть», «Продолжить от неё», «Скачать», «Сохранить в проект». В history.json лежат
// только якоря module_record — image_thread { threadId, versionId } и
// image_launch_versions { threadId, jobId, … }; всё остальное — живьём из стора нитей.

import { useState, type ReactNode } from 'react';
import {
  AlertTriangle, Download, Expand, RotateCcw, Save, Sparkles, Target, Undo2, X,
} from 'lucide-react';
import {
  Badge, Button, Dot, IconButton, ProgressBar, C, FS, R, SP, ICON_SIZE, ICON_STROKE, isCardPick, useIsMobile,
} from 'aihome_shell/kit';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import { isFreeUnit, money, variantsWord } from '../format';
import { enterScope, isPersonalScope } from '../scope';
import { continueFrom, pickByHuman, saveToProject, versionSaved } from './actions';
import { download, PERSONAL_DOWNLOAD_HINT } from './download';
import {
  downloadName, findVersion, fromVersion, isEmptyThread, launchEndNote, launchOf, launchVersions, ORIGIN, saveFolder,
  threadName, versionHasImage, versionMeta, versionName, versionPrimary, versionStep,
} from './model';
import { recordOf } from './records';
import { openEditor, useThreads } from './threadStore';
import type { ImageThread, ImageThreadVersion } from './threadsApi';
import { launchThread, versionSrc } from './useThreadLaunch';
import { queueText, useJobStatus, useProgress } from './useJobStatus';

const ic = (I: typeof X, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

const CARD_GAP = SP.md;
// Не больше двух карточек в ряд: ширина — половина ленты за вычетом зазора. Одиночная
// карточка (и любая на мобиле) — на всю ленту: крупное превью 5:3 решено осознанно.
const cardWidth = (mobile: boolean, solo: boolean) => (mobile || solo ? '100%' : `calc((100% - ${CARD_GAP}px) / 2)`);
// Пропорция превью: общая у карточки и её скелетона, иначе лента прыгает при загрузке.
const PREVIEW_RATIO = '5 / 3';
// Потолок высоты превью: на широкой, но невысокой ленте карточка не должна занимать весь
// экран — рамка тогда становится шире 5:3, картинка вписывается через contain.
const PREVIEW_MAX_HEIGHT = '60vh';

function Note({ children }: { children: ReactNode }) {
  return <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{children}</div>;
}

function Shell({ current, mobile, solo, children, testId, onPick }: {
  current: boolean; mobile: boolean; solo: boolean; children: ReactNode; testId?: string; onPick?: () => void;
}) {
  return (
    <div data-image-version={testId} data-current={current ? 'true' : 'false'}
      // Клик по карточке, а не по её кнопке или картинке, — выбор человеком: панель следует за ним
      onClick={onPick ? e => { if (isCardPick(e.target, e.currentTarget)) onPick(); } : undefined}
      style={{
      width: cardWidth(mobile, solo), maxWidth: '100%', boxSizing: 'border-box', padding: SP.sm,
      display: 'flex', flexDirection: 'column', gap: SP.xs,
      background: C.bgCard, border: `1px solid ${current ? C.accent : C.border}`, borderRadius: R.xl,
      boxShadow: current ? `0 0 0 3px ${C.accentLight}` : 'none',
    }}>
      {children}
    </div>
  );
}

function Picture({ src, onOpen }: { src: string | null; onOpen?: () => void }) {
  const [failed, setFailed] = useState<string | null>(null);
  const frame = {
    aspectRatio: PREVIEW_RATIO, maxHeight: PREVIEW_MAX_HEIGHT, boxSizing: 'border-box', borderRadius: R.md, border: `1px solid ${C.borderLight}`, background: C.bgInset, overflow: 'hidden',
  } as const;
  if (!src || failed === src) {
    return (
      <div style={{ ...frame, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: SP.xs, color: C.textMuted, fontSize: FS.sm, padding: SP.sm, textAlign: 'center' }}>
        {ic(AlertTriangle)} Картинки уже нет в рабочей папке
      </div>
    );
  }
  return (
    <div role={onOpen ? 'button' : undefined} tabIndex={onOpen ? 0 : undefined} title={onOpen ? 'Открыть в редакторе' : undefined}
      onClick={onOpen} onKeyDown={e => { if (onOpen && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); onOpen(); } }}
      style={{ ...frame, cursor: onOpen ? 'zoom-in' : 'default', display: 'flex', justifyContent: 'center' }}>
      <img src={src} alt="" onError={() => setFailed(src)}
        style={{ display: 'block', width: '100%', height: '100%', objectFit: 'contain' }} />
    </div>
  );
}

// Карточка версии: шапка, картинка, подпись «вариант 1 из 2 · от исходника · модель», действия.
// solo — карточка в блоке одна (якорь нити, единственный вариант запуска).
export function VersionCard({ projectId, sessionId, thread, version, focused, model, solo = true }: {
  projectId: string; sessionId: string; thread: ImageThread; version: ImageThreadVersion; focused: boolean;
  model?: string | null; solo?: boolean;
}) {
  const mobile = useIsMobile();
  const [busy, setBusy] = useState(false);
  const current = focused && thread.currentVersionId === version.id;
  const src = versionSrc(projectId, thread, version);
  const saved = versionSaved(thread, version);
  const name = `${threadName(thread)} · ${versionName(version)}`;
  const meta = versionMeta(thread, version, model);
  const personal = isPersonalScope(projectId);
  const primary = versionPrimary(thread, version, focused, saved, personal);
  const save = () => { if (src) void download(src, mime => downloadName(thread, version, mime)); };
  const run = async (fn: () => Promise<unknown>) => { setBusy(true); await fn(); setBusy(false); };
  const open = () => openEditor(sessionId, thread.id, version.id);

  return (
    <Shell current={current} mobile={mobile} solo={solo} testId={String(version.number)}
      onPick={() => { void pickByHuman(projectId, sessionId, thread.id, focused); }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
        {current && <Badge size="xs" tone="accent" icon={ic(Target)}>в работе</Badge>}
        <span title={name} style={{
          fontSize: FS.sm, fontWeight: 600, color: C.textHeading, minWidth: 0, flex: 1,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{name}</span>
        {!personal && <Badge size="xs" tone={saved ? 'success' : 'warning'}>{saved ? 'в проекте' : 'черновик'}</Badge>}
      </div>
      <Picture src={src} onOpen={open} />
      {meta && (
        <div title={meta} style={{ fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {meta}
        </div>
      )}
      <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap', alignItems: 'center' }}>
        <Button size="xs" variant="secondary" leftIcon={ic(Expand)} onClick={open}>Открыть</Button>
        {primary === 'save' && (
          <Button size="xs" variant="secondary" leftIcon={ic(Save)} disabled={busy}
            onClick={() => { void run(() => saveToProject(projectId, sessionId, thread, versionStep(thread, version))); }}>
            Сохранить в проект
          </Button>
        )}
        {primary === 'download' && src && (
          <Button size="xs" variant="secondary" leftIcon={ic(Download)} title={personal ? PERSONAL_DOWNLOAD_HINT : undefined} onClick={save}>
            Скачать
          </Button>
        )}
        {primary === 'continue' && (
          <Button size="xs" variant="secondary" leftIcon={ic(Undo2)} disabled={busy}
            title="Следующая правка пойдёт от этой версии, остальные останутся в ленте"
            onClick={() => { void run(() => continueFrom(projectId, sessionId, thread, version.id)); }}>
            Продолжить от неё
          </Button>
        )}
        {primary === 'work' && (
          <Button size="xs" variant="primary" leftIcon={ic(Target)} disabled={busy}
            title="Полоса «Картинки» и режим «Картинка» будут работать с этой версией"
            onClick={() => { void run(() => continueFrom(projectId, sessionId, thread, version.id)); }}>
            Работать с этой
          </Button>
        )}
        {src && primary !== 'download' && (
          <span style={{ marginLeft: 'auto', display: 'inline-flex' }}>
            <IconButton size="xs" title="Скачать" ariaLabel="Скачать" onClick={save}>
              {ic(Download)}
            </IconButton>
          </span>
        )}
      </div>
    </Shell>
  );
}

// Черновик «Новая картинка» до первого запуска: пунктирная рамка вместо картинки
function DraftBox({ thread, focused, personal }: { thread: ImageThread; focused: boolean; personal: boolean }) {
  const mobile = useIsMobile();
  const folder = saveFolder(thread);
  return (
    <div data-image-draft="" style={{
      width: cardWidth(mobile, true), maxWidth: '100%', boxSizing: 'border-box', padding: SP.lg,
      display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.xs, textAlign: 'center',
      border: `1.5px dashed ${focused ? C.accent : C.border}`, borderRadius: R.xl, background: C.bgInset,
      fontSize: FS.sm, color: C.textMuted,
    }}>
      <b style={{ color: C.textHeading, fontSize: FS.base }}>Новая картинка</b>
      {focused ? 'Опишите её в поле ввода — версии лягут в ленту ниже' : 'Ещё не нарисована'}
      {!personal && <span style={{ fontSize: FS.xs }}>сохранять в {folder ? `${folder}/` : 'корень проекта'}</span>}
    </div>
  );
}

// Якорь нити { threadId, versionId: "origin" }: исходник картинки или черновик
export function OriginAnchor({ projectId, sessionId, thread, focused }: {
  projectId: string; sessionId: string; thread: ImageThread; focused: boolean;
}) {
  const origin = findVersion(thread, ORIGIN);
  if (!origin || !versionHasImage(thread, origin)) {
    // После первого запуска черновик ничего не добавляет: результат — в якоре запуска ниже
    if (thread.launches?.length || !isEmptyThread(thread)) return null;
    return <DraftBox thread={thread} focused={focused} personal={isPersonalScope(projectId)} />;
  }
  return <VersionCard projectId={projectId} sessionId={sessionId} thread={thread} version={origin} focused={focused} />;
}

// ── Запуск: строка и карточки его вариантов ──

interface LaunchData {
  threadId?: unknown; jobId?: unknown; prompt?: unknown; model?: unknown; count?: unknown;
  estimate?: unknown; initiator?: unknown; baseVersionId?: unknown;
}

const str = (v: unknown) => (typeof v === 'string' && v.trim() ? v : null);

function estimateText(e: unknown): string | null {
  const x = e as { amount?: unknown; unit?: unknown; approx?: unknown } | null;
  if (!x || typeof x.amount !== 'number' || typeof x.unit !== 'string') return null;
  if (isFreeUnit(x.unit)) return 'бесплатно';
  return `${x.approx ? '≈ ' : ''}${money(x.amount, x.unit)}`;
}

function Skeleton({ mobile, solo }: { mobile: boolean; solo: boolean }) {
  return (
    <div data-image-version="skeleton" style={{
      width: cardWidth(mobile, solo), maxWidth: '100%', boxSizing: 'border-box', padding: SP.sm,
      display: 'flex', flexDirection: 'column', gap: SP.xs,
      background: C.bgCard, border: `1px solid ${C.border}`, borderRadius: R.xl,
    }}>
      <div className="cc-skel" style={{ height: 14, width: '55%', borderRadius: R.sm }} />
      <div className="cc-skel" style={{ aspectRatio: PREVIEW_RATIO, maxHeight: PREVIEW_MAX_HEIGHT, borderRadius: R.md }} />
    </div>
  );
}

function RunningLine({ projectId, jobId, model }: { projectId: string; jobId: string; model: string | null }) {
  const { status, cancel } = useJobStatus(projectId, jobId);
  const running = (status?.phase ?? 'run') === 'run';
  const progress = useProgress(status, running);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textSecondary }}>
        <Dot color={C.accent} />Рисуем…{model ? ` · ${model}` : ''}{queueText(progress.queuePosition)}
        <Button size="xs" variant="ghost" leftIcon={ic(X)} onClick={() => { void cancel(); }}>Отменить</Button>
      </div>
      <ProgressBar value={progress.percent} transition="width .5s linear" />
    </div>
  );
}

export function LaunchAnchor({ ctx }: { ctx: ChatItemToolCtx }) {
  const rec = recordOf(ctx.item);
  // Личный чат вне проекта: ctx.projectId = null, область — personal
  const projectId = enterScope(ctx.projectId, ctx.sessionId);
  const state = useThreads(projectId, ctx.sessionId);
  const mobile = useIsMobile();
  const [busy, setBusy] = useState(false);
  const data = (rec?.data ?? {}) as LaunchData;
  const jobId = str(data.jobId);
  const thread = typeof data.threadId === 'string' ? state.threads.find(t => t.id === data.threadId) : undefined;
  const { sessionId } = ctx;
  if (!sessionId || !thread || !jobId) return rec?.fallback ? <Note>{rec.fallback}</Note> : null;

  const launch = launchOf(thread, jobId);
  const versions = launchVersions(thread, jobId);
  const focused = state.focus === thread.id;
  const model = str(data.model);
  const count = typeof data.count === 'number' && data.count > 0 ? data.count : 1;
  const prompt = str(data.prompt) ?? launch?.prompt ?? null;
  const base = findVersion(thread, str(data.baseVersionId) ?? launch?.baseVersionId);
  const agent = (str(data.initiator) ?? launch?.initiator) === 'agent';
  const head = base && versionHasImage(thread, base) ? `Правка ${fromVersion(base)}` : 'Генерация';
  const info = [model, variantsWord(count), estimateText(data.estimate)].filter(Boolean).join(' · ');
  const status = launch?.status ?? (versions.length ? 'done' : 'running');
  const endNote = launchEndNote(status, versions.length, count);
  const skeletons = status === 'running' ? Math.max(0, Math.min(count, 4) - versions.length) : 0;
  const solo = versions.length + skeletons === 1;

  return (
    <div data-image-launch={status} style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.sm, color: C.textSecondary }}>
        <span style={{ display: 'inline-flex', alignSelf: 'center', color: C.textMuted }}>{ic(Sparkles)}</span>
        <b style={{ color: C.textHeading }}>{agent ? `Claude: ${head.charAt(0).toLowerCase()}${head.slice(1)}` : head}</b>
        <span>{threadName(thread)} · {info}</span>
        {prompt && <span style={{ color: C.textMuted, fontStyle: 'italic', overflowWrap: 'anywhere' }}>«{prompt}»</span>}
      </div>

      {status === 'running' && <RunningLine projectId={projectId} jobId={jobId} model={model} />}

      {(versions.length > 0 || status === 'running') && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: CARD_GAP }}>
          {versions.map(v => (
            <VersionCard key={v.id} projectId={projectId} sessionId={sessionId} thread={thread} version={v} focused={focused} model={model} solo={solo} />
          ))}
          {Array.from({ length: skeletons }, (_, i) => (
            <Skeleton key={i} mobile={mobile} solo={solo} />
          ))}
        </div>
      )}

      {endNote && (
        <div data-image-launch-empty={versions.length ? undefined : ''} style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
          <Note>{endNote}</Note>
          {status === 'interrupted' && !versions.length && prompt && focused && (
            <Button size="xs" variant="secondary" leftIcon={ic(RotateCcw)} disabled={busy} title={`«${prompt}»`}
              onClick={() => {
                setBusy(true);
                void launchThread(projectId, sessionId, thread, { kind: 'prompt', prompt }).finally(() => setBusy(false));
              }}>
              Запустить заново
            </Button>
          )}
        </div>
      )}
    </div>
  );
}

// Карточка-стопка нити в ленте чата (ADR-019 §3, макет v3 «Карточка-стопка»): одна
// карточка на стопку, а не по карточке на шаг. В history.json лежит только якорь
// module_record { recordType: "image_thread", data: { threadId, stackId } }, всё
// содержимое — живьём из стора нитей. Взятый вариант становится следующим шагом этой
// же карточки; после отката и новой правки старая стопка остаётся на месте с меткой.

import { useState, type ReactNode } from 'react';
import { AlertTriangle, ChevronLeft, ChevronRight, Download, Image as ImageIcon, Pencil, RotateCcw, X } from 'lucide-react';
import {
  Badge, Button, Dot, IconButton, ProgressBar, C, FS, R, SHADOW, SP, ICON_SIZE, ICON_STROKE, isCardPick, useIsMobile,
} from 'aihome_shell/kit';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import { imageEditorApi } from '../api';
import { enterScope, isPersonalScope } from '../scope';
import { isFreeUnit, money, variantsWord } from '../format';
import { dismissJob, pickByHuman, saveToProject, savedStepOf, takeVariant, workWith } from './actions';
import { download } from './download';
import {
  chainOf, currentIndex, currentStack, findStack, findVersion, interruptedOf, isEmptyThread, isHiddenDraft, isLegacyThread, ORIGIN, saveFolder,
  stackSaveState, stepOf, threadName, versionLabel,
} from './model';
import { recordOf } from './records';
import { openEditor, useThreads } from './threadStore';
import type { ImageThread, ImageThreadEvent, ImageThreadStack } from './threadsApi';
import { imageSrc, launchThread } from './useThreadLaunch';
import { queueText, useJobStatus, useProgress } from './useJobStatus';
import { OriginAnchor, VersionCard } from './VersionCards';
import { CardContextActions, useCardFill, useFocusedThreadId } from '../context/CardFill';

const ic = (I: typeof X, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

function Frame({ focused, stacked, dashed, dim, onPick, children }: {
  focused: boolean; stacked: boolean; dashed?: boolean; dim?: boolean; onPick?: () => void; children: ReactNode;
}) {
  // Края стопки позади карточки — два сдвинутых контура той же карточки
  const edges = stacked ? `, 5px 5px 0 -1px ${C.bgCard}, 5px 5px 0 0 ${C.border}, 10px 10px 0 -1px ${C.bgCard}, 10px 10px 0 0 ${C.borderLight}` : '';
  return (
    <div data-image-thread-card={focused ? 'focused' : 'idle'}
      // Клик по карточке, а не по её кнопке, — выбор человеком: панель следует за ним
      onClick={onPick ? e => { if (isCardPick(e.target, e.currentTarget)) onPick(); } : undefined}
      style={{
      display: 'flex', flexDirection: 'column', gap: SP.sm, padding: SP.md, maxWidth: 520,
      marginRight: stacked ? 10 : 0, marginBottom: stacked ? 10 : 0,
      border: `1px ${dashed ? 'dashed' : 'solid'} ${focused ? C.accent : C.border}`, borderRadius: R.xl,
      background: C.bgCard, boxShadow: `${SHADOW.card}${edges}`, opacity: dim ? 0.85 : 1,
    }}>
      {children}
    </div>
  );
}

function Note({ children }: { children: ReactNode }) {
  return <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{children}</div>;
}

function Acts({ children }: { children: ReactNode }) {
  return <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap', alignItems: 'center' }}>{children}</div>;
}

function StepImage({ src }: { src: string | null }) {
  const [failed, setFailed] = useState<string | null>(null);
  if (!src || failed === src) {
    return (
      <div style={{
        height: 120, borderRadius: R.lg, background: C.bgInset, color: C.textMuted, fontSize: FS.sm,
        display: 'flex', alignItems: 'center', justifyContent: 'center', gap: SP.xs,
      }}>
        {ic(AlertTriangle)} Шаг устарел: картинки уже нет в рабочей папке
      </div>
    );
  }
  return (
    <div style={{ borderRadius: R.lg, background: C.bgInset, overflow: 'hidden', display: 'flex', justifyContent: 'center' }}>
      <img src={src} alt="" onError={() => setFailed(src)}
        style={{ display: 'block', maxWidth: '100%', maxHeight: 280, objectFit: 'contain' }} />
    </div>
  );
}

// ── Идущая генерация и варианты ──

// inEditor — блок в попапе «Редактор»: выбор варианта показывает его на холсте (onPreview)
export function JobBlock({ projectId, sessionId, thread, inEditor, onPreview }: {
  projectId: string; sessionId: string; thread: ImageThread; inEditor?: boolean;
  onPreview?: (v: { jobId: string; variant: number } | null) => void;
}) {
  const jobId = thread.pendingJobId!;
  const { status, cancel } = useJobStatus(projectId, jobId);
  const [picked, setPicked] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const phase = status?.phase ?? 'run';
  const progress = useProgress(status, phase === 'run');
  const count = thread.settings?.count ?? status?.count ?? 1;
  const api = imageEditorApi();
  const cost = status?.cost;
  const costText = cost ? (isFreeUnit(cost.unit) ? 'бесплатно' : money(cost.amount, cost.unit)) : null;

  if (phase === 'run') {
    return (
      <div data-image-thread-job="run" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <Acts>
          <Dot color={C.accent} />
          <span style={{ fontSize: FS.sm, color: C.textPrimary }}>
            Рисуем {variantsWord(count)}…{status?.model ? ` · ${status.model}` : ''}{queueText(progress.queuePosition)}
          </span>
          <Button size="xs" variant="ghost" leftIcon={ic(X)} onClick={() => { void cancel(); }}>Отменить</Button>
        </Acts>
        <ProgressBar value={progress.percent} transition="width .5s linear" />
        <div style={{ display: 'grid', gridTemplateColumns: `repeat(${Math.min(count, 4)}, minmax(0, 1fr))`, gap: SP.xs }}>
          {Array.from({ length: Math.min(count, 4) }, (_, i) => (
            <div key={i} style={{ aspectRatio: '1 / 1', borderRadius: R.md, background: C.bgInset }} />
          ))}
        </div>
      </div>
    );
  }

  const dismiss = async () => {
    setBusy(true);
    if (await dismissJob(projectId, sessionId, thread, jobId)) onPreview?.(null);
    setBusy(false);
  };

  if (phase !== 'done' || !status?.variants.length) {
    const why = phase === 'cancel' ? 'Генерация отменена.'
      : phase === 'lost' ? 'Сервер перезапускался, задача не сохранилась.'
      : status?.error ?? 'Сервис рисования отказал.';
    return (
      <div data-image-thread-job={phase} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
        <Note>{why}{status?.charged === false ? ' Деньги не списаны.' : ''}</Note>
        <Acts><Button size="xs" variant="ghost" disabled={busy} onClick={() => { void dismiss(); }}>Скрыть</Button></Acts>
      </div>
    );
  }

  const variants = status.variants;
  const sel = picked !== null && variants.includes(picked) ? picked : variants[0];
  const take = async () => {
    setBusy(true);
    if (await takeVariant(projectId, sessionId, thread, { jobId, variant: sel })) onPreview?.(null);
    setBusy(false);
  };
  return (
    <div data-image-thread-job="done" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <Note>
        <span style={{ color: C.successText, fontWeight: 600 }}>✓ Готово</span>
        {' · '}{variantsWord(variants.length)}{costText ? ` · ${costText}` : ''}. Выберите — станет следующим шагом.
      </Note>
      <div style={{ display: 'grid', gridTemplateColumns: `repeat(${Math.min(variants.length, 4)}, minmax(0, 1fr))`, gap: SP.xs }}>
        {variants.map((v, i) => (
          <IconButton key={v} variant="media" active={v === sel} title={`Вариант ${i + 1}`} ariaLabel={`Вариант ${i + 1}`}
            onClick={() => { setPicked(v); onPreview?.({ jobId, variant: v }); }} style={{ width: '100%', height: 'auto', aspectRatio: '1 / 1', padding: 0 }}>
            <img src={api.variantUrl(projectId, jobId, v)} alt=""
              style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block', borderRadius: R.md }} />
          </IconButton>
        ))}
      </div>
      <Acts>
        <Button size="sm" variant="primary" disabled={busy} onClick={() => { void take(); }}>
          Взять вариант {variants.indexOf(sel) + 1}
        </Button>
        {!inEditor && (
          <Button size="sm" variant="secondary" disabled={busy} onClick={() => openEditor(sessionId, thread.id)}>
            До / после в редакторе
          </Button>
        )}
        <Button size="sm" variant="ghost" disabled={busy} onClick={() => { void dismiss(); }}>Не брать</Button>
      </Acts>
    </div>
  );
}

// Задачу оборвал перезапуск сервера: вариантов не будет, «Рисуем…» не закончится никогда
function InterruptedBlock({ projectId, sessionId, thread, jobId, prompt }: {
  projectId: string; sessionId: string; thread: ImageThread; jobId: string; prompt: string | null;
}) {
  const [busy, setBusy] = useState(false);
  const run = async (fn: () => Promise<boolean>) => { setBusy(true); await fn(); setBusy(false); };
  return (
    <div data-image-thread-job="interrupted" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <Note>
        Генерация прервана перезапуском сервера.
        {prompt ? '' : ' Запустите её заново из композера в режиме «Картинка».'}
      </Note>
      <Acts>
        {prompt && (
          <Button size="sm" variant="secondary" leftIcon={ic(RotateCcw)} disabled={busy} title={`«${prompt}»`}
            onClick={() => { void run(() => launchThread(projectId, sessionId, thread, { kind: 'prompt', prompt })); }}>
            Запустить заново
          </Button>
        )}
        <Button size="sm" variant="ghost" disabled={busy}
          onClick={() => { void run(() => dismissJob(projectId, sessionId, thread, jobId)); }}>
          Не брать
        </Button>
      </Acts>
    </div>
  );
}

// ── Карточка ──

function StackCard({ projectId, sessionId, thread, stack, focused, events }: {
  projectId: string; sessionId: string; thread: ImageThread; stack: ImageThreadStack | null; focused: boolean;
  events?: readonly ImageThreadEvent[];
}) {
  const mobile = useIsMobile();
  const fill = useCardFill(sessionId, thread, null);
  const chain = chainOf(thread, stack);
  const isCurrent = !stack || stack.stackId === currentStack(thread)?.stackId;
  const at = currentIndex(thread, chain, stack);
  // Новый шаг или откат — показываем, где нить сейчас: листание помнится только до сдвига нити
  const [nav, setNav] = useState({ at, view: at });
  const view = nav.at === at ? nav.view : at;
  const setView = (v: number) => setNav({ at, view: v });
  const idx = Math.min(Math.max(view, 0), chain.length - 1);
  const pos = chain[idx];
  const draft = !thread.file && isEmptyThread(thread);
  const saved = savedStepOf(thread.id);
  const unsaved = isCurrent && !!thread.currentStepId && thread.currentStepId !== saved;
  const [saving, setSaving] = useState(false);
  const src = pos ? imageSrc(projectId, thread, pos.stepId) : null;
  const folder = saveFolder(thread);
  const interrupted = isCurrent ? interruptedOf(thread, events) : null;
  const personal = isPersonalScope(projectId);
  const saveState = stackSaveState(thread, isCurrent, unsaved, personal);

  const edit = async () => {
    if (fill.on) await fill.pick();
    else if (!focused && !(await workWith(projectId, sessionId, thread.id))) return;
    openEditor(sessionId, thread.id);
  };
  const save = async () => { setSaving(true); await saveToProject(projectId, sessionId, thread); setSaving(false); };

  return (
    <Frame focused={focused && isCurrent} stacked={chain.length > 1} dashed={draft} dim={!!stack?.old}
      onPick={() => { void (fill.on ? fill.pick() : pickByHuman(projectId, sessionId, thread.id, focused)); }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.sm }}>
        <span style={{ display: 'inline-flex', color: C.textMuted }}>{ic(ImageIcon, ICON_SIZE.sm)}</span>
        <span style={{ fontWeight: 600, color: C.textHeading, overflowWrap: 'anywhere' }}>{threadName(thread)}</span>
        {!(draft && personal) && (
          <span style={{ color: C.textSecondary }}>· {draft ? `сохранять в ${folder ? `${folder}/` : 'корень проекта'}` : versionLabel(thread, pos, saved)}</span>
        )}
        {stack?.old && <Badge size="xs" tone="neutral">старая стопка</Badge>}
        {focused && isCurrent && <Badge size="xs" tone="accent">{fill.byAgent ? 'в работе ✦' : 'в работе'}</Badge>}
        {chain.length > 1 && (
          <span style={{ marginLeft: 'auto', display: 'inline-flex', alignItems: 'center', gap: SP.xxs, color: C.textMuted }}>
            <IconButton size="xs" title="Предыдущий шаг" ariaLabel="Предыдущий шаг" disabled={idx <= 0} onClick={() => setView(idx - 1)}>
              {ic(ChevronLeft)}
            </IconButton>
            <span style={{ fontSize: FS.xs, whiteSpace: 'nowrap' }}>{stepOf(idx, chain.length)}</span>
            <IconButton size="xs" title="Следующий шаг" ariaLabel="Следующий шаг" disabled={idx >= chain.length - 1} onClick={() => setView(idx + 1)}>
              {ic(ChevronRight)}
            </IconButton>
          </span>
        )}
      </div>

      {draft ? (
        <div style={{
          padding: SP.lg, borderRadius: R.lg, border: `1px dashed ${C.border}`, background: C.bgInset,
          color: C.textMuted, fontSize: FS.sm, lineHeight: 1.5, textAlign: 'center',
        }}>
          Опишите её в композере в режиме «Картинка» — варианты появятся здесь
        </div>
      ) : <StepImage src={src} />}

      {isCurrent && thread.pendingJobId && <JobBlock projectId={projectId} sessionId={sessionId} thread={thread} />}
      {interrupted && <InterruptedBlock projectId={projectId} sessionId={sessionId} thread={thread} {...interrupted} />}

      {!draft && (
        <Acts>
          {fill.on
            ? isCurrent && <CardContextActions sessionId={sessionId} projectId={projectId} thread={thread} versionId={null} fill={fill} size="sm" />
            : !focused && <Button size="sm" variant="secondary" onClick={() => { void workWith(projectId, sessionId, thread.id); }}>Работать с этой</Button>}
          <Button size="sm" variant={focused ? 'secondary' : 'ghost'} leftIcon={ic(Pencil)} onClick={() => { void edit(); }}>Редактировать</Button>
          {src && (
            <Button size="sm" variant="ghost" leftIcon={ic(Download)} title="Скачать" onClick={() => { void download(src, () => threadName(thread)); }}>
              {mobile ? '' : 'Скачать'}
            </Button>
          )}
          {saveState === 'save' && <Button size="sm" variant="primary" loading={saving} onClick={() => { void save(); }}>Сохранить в проект</Button>}
          {saveState === 'in-project' && <span style={{ fontSize: FS.xs, color: C.textMuted }}>В проекте</span>}
        </Acts>
      )}
    </Frame>
  );
}

// Вклад в chat-item-tool: якорь нити в ленте. С 27.09 якорь — { threadId, versionId } и
// рисует исходник (или черновик), версии запусков — свои якоря ниже. Якорь со stackId —
// карточка-стопка старой нити: старые чаты открываются как были
export function ThreadAnchor({ ctx }: { ctx: ChatItemToolCtx }) {
  const rec = recordOf(ctx.item);
  // Личный чат вне проекта: ctx.projectId = null, область — personal
  const projectId = enterScope(ctx.projectId, ctx.sessionId);
  const state = useThreads(projectId, ctx.sessionId);
  const focusId = useFocusedThreadId(ctx.sessionId ?? '', state.focus);
  const data = (rec?.data ?? {}) as { threadId?: unknown; stackId?: unknown; versionId?: unknown };
  const thread = typeof data.threadId === 'string' ? state.threads.find(t => t.id === data.threadId) : undefined;
  if (!ctx.sessionId || !thread) {
    // Нить удалили (пустой черновик сняли ✕) — якорь в истории остался; рисуем след
    return rec?.fallback ? <Note>{rec.fallback}</Note> : null;
  }
  const focused = focusId === thread.id;
  const byVersion = typeof data.versionId === 'string' || (typeof data.stackId !== 'string' && !isLegacyThread(thread) && !!thread.versions?.length);
  if (byVersion) {
    const v = typeof data.versionId === 'string' && data.versionId !== ORIGIN ? findVersion(thread, data.versionId) : null;
    return v
      ? <VersionCard projectId={projectId} sessionId={ctx.sessionId} thread={thread} version={v} focused={focused} />
      : <OriginAnchor projectId={projectId} sessionId={ctx.sessionId} thread={thread} focused={focused} />;
  }
  const stack = typeof data.stackId === 'string' ? findStack(thread, data.stackId) : currentStack(thread);
  // Пустой черновик в ленте не рисуем; пока идёт первый запуск — карточка с «Отменить», как раньше
  if (isHiddenDraft(thread)) return null;
  return (
    <StackCard projectId={projectId} sessionId={ctx.sessionId} thread={thread} stack={stack}
      focused={focused} events={state.events} />
  );
}

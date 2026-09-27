// Карточка-стопка нити в ленте чата (ADR-019 §3, макет v3 «Карточка-стопка»): одна
// карточка на стопку, а не по карточке на шаг. В history.json лежит только якорь
// module_record { recordType: "image_thread", data: { threadId, stackId } }, всё
// содержимое — живьём из стора нитей. Взятый вариант становится следующим шагом этой
// же карточки; после отката и новой правки старая стопка остаётся на месте с меткой.

import { useState, type ReactNode } from 'react';
import { AlertTriangle, ChevronLeft, ChevronRight, Download, Image as ImageIcon, Pencil, X } from 'lucide-react';
import {
  Badge, Button, Dot, IconButton, ProgressBar, C, FS, R, SHADOW, SP, ICON_SIZE, ICON_STROKE, useIsMobile,
} from 'aihome_shell/kit';
import type { ChatItemToolCtx } from '../../../lib/subsystems/registryCore';
import { imageEditorApi } from '../api';
import { isFreeUnit, money, variantsWord } from '../format';
import { dismissJob, saveToProject, savedStepOf, takeVariant, workWith } from './actions';
import {
  chainOf, currentIndex, currentStack, findStack, isEmptyThread, saveFolder, stepOf, threadName, versionLabel,
} from './model';
import { recordOf } from './records';
import { openEditor, useThreads } from './threadStore';
import type { ImageThread, ImageThreadStack } from './threadsApi';
import { imageSrc } from './useThreadLaunch';
import { useJobStatus, useProgress } from './useJobStatus';

const ic = (I: typeof X, size: number = ICON_SIZE.xs) => <I size={size} strokeWidth={ICON_STROKE} />;

function Frame({ focused, stacked, dashed, dim, children }: {
  focused: boolean; stacked: boolean; dashed?: boolean; dim?: boolean; children: ReactNode;
}) {
  // Края стопки позади карточки — два сдвинутых контура той же карточки
  const edges = stacked ? `, 5px 5px 0 -1px ${C.bgCard}, 5px 5px 0 0 ${C.border}, 10px 10px 0 -1px ${C.bgCard}, 10px 10px 0 0 ${C.borderLight}` : '';
  return (
    <div data-image-thread-card={focused ? 'focused' : 'idle'} style={{
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

function download(src: string, name: string) {
  const a = document.createElement('a');
  a.href = src;
  a.download = name;
  a.click();
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
  const progress = useProgress(phase === 'run', status?.createdAt ?? null);
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
            Рисуем {variantsWord(count)}…{status?.model ? ` · ${status.model}` : ''}
          </span>
          <Button size="xs" variant="ghost" leftIcon={ic(X)} onClick={() => { void cancel(); }}>Отменить</Button>
        </Acts>
        <ProgressBar value={progress} transition="width .5s linear" />
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

// ── Карточка ──

function StackCard({ projectId, sessionId, thread, stack, focused }: {
  projectId: string; sessionId: string; thread: ImageThread; stack: ImageThreadStack | null; focused: boolean;
}) {
  const mobile = useIsMobile();
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

  const edit = async () => {
    if (!focused && !(await workWith(projectId, sessionId, thread.id))) return;
    openEditor(sessionId, thread.id);
  };
  const save = async () => { setSaving(true); await saveToProject(projectId, sessionId, thread); setSaving(false); };

  return (
    <Frame focused={focused && isCurrent} stacked={chain.length > 1} dashed={draft} dim={!!stack?.old}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap', fontSize: FS.sm }}>
        <span style={{ display: 'inline-flex', color: C.textMuted }}>{ic(ImageIcon, ICON_SIZE.sm)}</span>
        <span style={{ fontWeight: 600, color: C.textHeading, overflowWrap: 'anywhere' }}>{threadName(thread)}</span>
        <span style={{ color: C.textSecondary }}>· {draft ? `сохранять в ${folder ? `${folder}/` : 'корень проекта'}` : versionLabel(thread, pos, saved)}</span>
        {stack?.old && <Badge size="xs" tone="neutral">старая стопка</Badge>}
        {focused && isCurrent && <Badge size="xs" tone="accent">в работе</Badge>}
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

      {!draft && (
        <Acts>
          {!focused && <Button size="sm" variant="secondary" onClick={() => { void workWith(projectId, sessionId, thread.id); }}>Работать с этой</Button>}
          <Button size="sm" variant={focused ? 'secondary' : 'ghost'} leftIcon={ic(Pencil)} onClick={() => { void edit(); }}>Редактировать</Button>
          {src && (
            <Button size="sm" variant="ghost" leftIcon={ic(Download)} title="Скачать" onClick={() => download(src, threadName(thread))}>
              {mobile ? '' : 'Скачать'}
            </Button>
          )}
          {isCurrent && (unsaved
            ? <Button size="sm" variant="primary" loading={saving} onClick={() => { void save(); }}>Сохранить в проект</Button>
            : thread.file && <span style={{ fontSize: FS.xs, color: C.textMuted }}>В проекте</span>)}
        </Acts>
      )}
    </Frame>
  );
}

// Вклад в chat-item-tool: якорь стопки в ленте
export function ThreadAnchor({ ctx }: { ctx: ChatItemToolCtx }) {
  const rec = recordOf(ctx.item);
  const state = useThreads(ctx.projectId, ctx.sessionId);
  const data = (rec?.data ?? {}) as { threadId?: unknown; stackId?: unknown };
  const thread = typeof data.threadId === 'string' ? state.threads.find(t => t.id === data.threadId) : undefined;
  if (!ctx.projectId || !ctx.sessionId || !thread) {
    // Нить удалили (пустой черновик сняли ✕) — якорь в истории остался; рисуем след
    return rec?.fallback ? <Note>{rec.fallback}</Note> : null;
  }
  const stack = typeof data.stackId === 'string' ? findStack(thread, data.stackId) : currentStack(thread);
  return (
    <StackCard projectId={ctx.projectId} sessionId={ctx.sessionId} thread={thread} stack={stack}
      focused={state.focus === thread.id} />
  );
}

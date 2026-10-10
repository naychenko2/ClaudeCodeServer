// Секция «Обучить персонажа» в карточке сохранённого персонажа (ADR-026 §7.2, макет character-lora-train).
// Вид выбирает чистая trainSectionView; здесь только разметка и действия.

import { useEffect, useState } from 'react';
import { AlertCircle, Check, Clock, Cpu, Info, RefreshCw, Sparkles, Square } from 'lucide-react';
import {
  Badge, Button, ConfirmDialog, Field, Notice, ProgressBar, TextField, ICON_SIZE, ICON_STROKE, C, FS, R, SP,
} from 'aihome_shell/kit';
import { loraTrainApi } from '../api';
import { reloadCharacters } from './useCharacters';
import { useLoraTraining } from './useLoraTraining';
import type { LoraVersionsHandle } from './useLoraVersions';
import { TRAIN_STOP_FOREIGN_CONFIRM, TRAIN_TEXT } from './loraTrainTexts';
import { badgeFor, confirmParts, failureText, isValidTrigger, trainSectionView } from './loraTrainView';
import { TRAIN_MIN } from './trainingPhotos';

const ico = { size: ICON_SIZE.sm, strokeWidth: ICON_STROKE } as const;

export function CharacterLoraTrainSection({ projectId, slug, hasConnected, photoCount, disabled, versions }: {
  projectId: string;
  // Общий с «Версиями персонажа» контроллер: подключение отсюда сразу обновляет тот список
  versions: LoraVersionsHandle;
  slug: string;
  // У персонажа уже есть подключённая LoRA
  hasConnected: boolean;
  // null — список фото ещё не загружен
  photoCount: number | null;
  disabled?: boolean;
}) {
  const training = useLoraTraining(projectId, slug);
  const [edited, setEdited] = useState<string | null>(null);
  const [dismissed, setDismissed] = useState<string | null>(null);
  const [modal, setModal] = useState<'start' | 'cancel' | 'foreign' | null>(null);
  const [isAdmin, setIsAdmin] = useState(false);

  const trigger = edited ?? training.state?.defaultTrigger ?? '';
  const foreignBusy = !!training.state?.gpu.busy && !training.state.gpu.mine;
  useEffect(() => {
    setIsAdmin(false);
    if (!foreignBusy) return;
    let alive = true;
    loraTrainApi.active().then(a => { if (alive) setIsAdmin(!!a.jobId); }).catch(() => {});
    return () => { alive = false; };
  }, [foreignBusy, projectId, slug]);

  const view = trainSectionView({
    view: training.view, state: training.state, error: training.error, photoCount,
    minPhotos: TRAIN_MIN, trigger, isAdmin, dismissedRunId: dismissed,
  });

  // Первое обучение сервер подключает само — подтягиваем персонажа, чтобы раздел LoRA показал новую
  const doneRun = view.kind === 'done' && view.first ? view.version.runId : null;
  useEffect(() => { if (doneRun) void reloadCharacters(projectId); }, [doneRun, projectId]);

  const badge = badgeFor(view);
  const err = training.error ? failureText(training.error) : null;
  const connectErr = versions.error ? failureText(versions.error) : null;
  const locked = disabled || training.busy;

  const startBtn = (label: string, isDisabled: boolean, icon = <Sparkles {...ico} />) => (
    <Button variant="primary" size="md" fullWidth disabled={isDisabled || locked || !training.start}
      onClick={() => setModal('start')}>{icon}{label}</Button>
  );
  const cancelBtn = (
    <div><Button variant="secondary" size="sm" disabled={locked} onClick={() => setModal('cancel')}>
      <Square {...ico} />{TRAIN_TEXT.cancel}
    </Button></div>
  );
  const note = (text: string, tone: 'info' | 'warning' | 'danger', Icon: typeof Info, title?: string) => (
    <Notice tone={tone} icon={Icon} title={title}>{text}</Notice>
  );

  let body: React.ReactNode = null;
  switch (view.kind) {
    case 'loading':
      body = <div style={{ fontSize: FS.sm, color: C.textMuted }}>{TRAIN_TEXT.loading}</div>;
      break;
    case 'off': body = note(TRAIN_TEXT.disabled, 'info', Info); break;
    case 'noLocal': body = note(TRAIN_TEXT.noLocal, 'warning', AlertCircle); break;
    case 'queued':
      body = (
        <>
          <RunBlock icon={<Clock {...ico} />} text={TRAIN_TEXT.queued}><ProgressBar value={35} estimate /></RunBlock>
          {cancelBtn}
        </>
      );
      break;
    case 'running':
      body = (
        <>
          <RunBlock icon={<Cpu {...ico} />} text={view.text}>
            <ProgressBar value={view.percent ?? 35} estimate={view.percent == null} />
            {view.etaText && (
              <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: FS.xs, color: C.textMuted }}>
                <span>{view.etaText}</span><span>{view.percent} %</span>
              </div>
            )}
          </RunBlock>
          {cancelBtn}
        </>
      );
      break;
    case 'done':
      body = (
        <>
          <div style={{ background: C.successBg, borderRadius: R.xl, padding: SP.md, display: 'flex', flexDirection: 'column', gap: SP.xs }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.base, fontWeight: 600, color: C.successText }}>
              <Check {...ico} />{view.first ? TRAIN_TEXT.doneFirstTitle : TRAIN_TEXT.doneNewTitle}
            </div>
            <p style={{ margin: 0, fontSize: FS.sm, lineHeight: 1.45, color: C.textPrimary }}>
              {view.first ? TRAIN_TEXT.doneFirstBody : TRAIN_TEXT.doneNewBody}
            </p>
            <div style={{ fontSize: FS.xs, color: C.textSecondary }}>{view.meta}</div>
          </div>
          {view.photosChanged && note(TRAIN_TEXT.photosChanged, 'warning', AlertCircle)}
          {connectErr && <ErrorLine text={connectErr} />}
          <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
            {!view.first && (
              <>
                <Button variant="primary" size="sm" loading={versions.pending.includes(view.version.runId)} disabled={disabled}
                  onClick={() => { void versions.ctl?.connect(view.version.runId).then(ok => {
                    if (ok) setDismissed(view.version.runId);
                  }); }}>
                  {TRAIN_TEXT.connect}
                </Button>
                <Button variant="secondary" size="sm" disabled={disabled} onClick={() => setDismissed(view.version.runId)}>
                  {TRAIN_TEXT.keepOld}
                </Button>
              </>
            )}
            {view.first && (
              <Button variant="secondary" size="sm" disabled={locked} onClick={() => setDismissed(view.version.runId)}>
                <RefreshCw {...ico} />{TRAIN_TEXT.restart}
              </Button>
            )}
          </div>
        </>
      );
      break;
    case 'failed':
      body = (
        <>
          {note(view.allRejected ? TRAIN_TEXT.failedAllRejected : view.lost ? TRAIN_TEXT.failedLost : TRAIN_TEXT.failedBody, 'danger', AlertCircle, TRAIN_TEXT.failedTitle)}
          {!view.allRejected && startBtn(TRAIN_TEXT.retry, false, <RefreshCw {...ico} />)}
        </>
      );
      break;
    case 'busyOther':
      body = (
        <>
          {note(view.text, 'warning', Clock)}
          {startBtn(TRAIN_TEXT.start, true)}
          {view.canStopForeign && (
            <div><Button variant="ghost" size="sm" onClick={() => setModal('foreign')}
              style={{ color: C.dangerText }}><Square {...ico} />{TRAIN_TEXT.stopForeign}</Button></div>
          )}
        </>
      );
      break;
    case 'busyMine':
      body = (
        <>
          {note(TRAIN_TEXT.busyMine, 'info', Info)}
          {startBtn(TRAIN_TEXT.start, true)}
        </>
      );
      break;
    case 'form':
      body = (
        <>
          {view.stopped && note(TRAIN_TEXT.stopped, 'info', Info)}
          {view.photosChanged && note(TRAIN_TEXT.photosChanged, 'warning', AlertCircle)}
          <Field label={TRAIN_TEXT.triggerLabel}
            hint={isValidTrigger(trigger) ? TRAIN_TEXT.triggerHint : undefined}
            error={isValidTrigger(trigger) ? undefined : TRAIN_TEXT.triggerError}>
            <TextField value={trigger} onChange={setEdited} mono invalid={!isValidTrigger(trigger)} disabled={locked} />
          </Field>
          {err && <ErrorLine text={err} />}
          {startBtn(view.label, view.disabled, view.photosChanged ? <RefreshCw {...ico} /> : <Sparkles {...ico} />)}
        </>
      );
      break;
  }

  const confirm = confirmParts(hasConnected);

  return (
    <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, minWidth: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px` }}>
        <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{TRAIN_TEXT.title}</span>
        {badge && <Badge tone={badge.tone} size="sm">{badge.text}</Badge>}
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, padding: `0 ${SP.md}px ${SP.md}px`, minWidth: 0 }}>
        <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{TRAIN_TEXT.intro}</div>
        {body}
      </div>
      {modal === 'start' && (
        <ConfirmDialog title={TRAIN_TEXT.confirmTitle}
          subtitle={(
            <>
              {confirm.before}<strong>{confirm.bold}</strong>{confirm.after}
              {confirm.note && <><br /><br />{confirm.note}</>}
            </>
          )}
          confirmLabel={TRAIN_TEXT.confirmOk} cancelLabel={TRAIN_TEXT.confirmCancel}
          onCancel={() => setModal(null)}
          onConfirm={async () => { await training.start?.(edited ?? undefined); setModal(null); }} />
      )}
      {modal === 'cancel' && (
        <ConfirmDialog title={TRAIN_TEXT.cancelTitle} subtitle={TRAIN_TEXT.cancelBody}
          confirmLabel={TRAIN_TEXT.cancelOk} cancelLabel={TRAIN_TEXT.cancelKeep} confirmVariant="danger"
          onCancel={() => setModal(null)}
          onConfirm={async () => { await training.cancel?.(); setModal(null); }} />
      )}
      {modal === 'foreign' && (
        <ConfirmDialog title={TRAIN_STOP_FOREIGN_CONFIRM()} subtitle={TRAIN_TEXT.cancelBody}
          confirmLabel={TRAIN_TEXT.cancelOk} cancelLabel={TRAIN_TEXT.cancelKeep} confirmVariant="danger"
          onCancel={() => setModal(null)}
          onConfirm={async () => {
            try { await loraTrainApi.cancelActive(); } catch { /* состояние покажет следующий опрос */ }
            setModal(null);
            void training.refresh?.();
          }} />
      )}
    </div>
  );
}

function RunBlock({ icon, text, children }: { icon: React.ReactNode; text: string; children: React.ReactNode }) {
  return (
    <div style={{
      background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.xl, padding: SP.md,
      display: 'flex', flexDirection: 'column', gap: SP.sm,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.sm, color: C.textPrimary }}>
        <span style={{ display: 'inline-flex', color: C.textMuted }}>{icon}</span>{text}
      </div>
      {children}
    </div>
  );
}

function ErrorLine({ text }: { text: string }) {
  return (
    <div style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start', fontSize: FS.sm, color: C.dangerText, overflowWrap: 'anywhere' }}>
      <AlertCircle size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flex: '0 0 auto', marginTop: 1 }} />{text}
    </div>
  );
}

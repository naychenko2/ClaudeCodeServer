// Главная кнопка карточки персонажа (спека character-panel-final-spec §4, §7): в зоне ровно одна
// главная кнопка ИЛИ блок хода обучения; особые виды — Notice. Состояние обучения и вид действия
// приходят пропсами из карточки, здесь только разметка и модалки.

import { useState } from 'react';
import { AlertCircle, Clock, Info, RefreshCw, Sparkles, Square, Upload, X } from 'lucide-react';
import {
  Button, ConfirmDialog, ContextAddButton, IconButton, Notice, ICON_SIZE, ICON_STROKE, C, FS, R, SP,
} from 'aihome_shell/kit';
import { loraTrainApi } from '../api';
import type { CardAction } from './characterCardView';
import { CHIPS, HERO } from './characterTexts';
import type { useCharacterTrain } from './useCharacterTrain';
import type { LoraVersionsHandle } from './useLoraVersions';
import { TRAIN_ETA_UNKNOWN, TRAIN_STOP_FOREIGN_CONFIRM, TRAIN_TEXT } from './loraTrainTexts';
import { confirmParts, formatDuration } from './loraTrainView';

const ico = { size: ICON_SIZE.sm, strokeWidth: ICON_STROKE } as const;
// Оценка длительности обучения для строки «ход неизвестен» — как в confirmBold
const TRAIN_ETA_FALLBACK_SEC = 2 * 3600;

export function CharacterHero({
  action, train, versions, slug, projectId, hasConnected, contextSessionId, disabled, onOpenPhotos, onOpenTune,
}: {
  action: CardAction;
  // Состояние обучения из useCharacterTrain — владеет им карточка
  train: ReturnType<typeof useCharacterTrain>;
  versions: LoraVersionsHandle;
  slug: string;
  projectId: string;
  // У персонажа уже есть подключённая LoRA — влияет на текст подтверждения старта
  hasConnected: boolean;
  // null — чат не выбран, «В контекст чата» недоступна
  contextSessionId: string | null;
  disabled?: boolean;
  onOpenPhotos: (section?: 'face' | 'train') => void;
  onOpenTune: () => void;
}) {
  const { view, training, edited, dismiss, isAdmin, actionError, connectError } = train;
  const [modal, setModal] = useState<'start' | 'cancel' | 'foreign' | null>(null);
  const locked = !!disabled || training.busy;

  const main = (node: React.ReactNode, meta?: string) => (
    <div data-hero="main" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {node}
      {meta && <div style={{ fontSize: FS.xs, color: C.textMuted, textAlign: 'center' }}>{meta}</div>}
    </div>
  );
  const startBtn = (label: string, icon: React.ReactNode, isDisabled = false) => (
    <Button variant="primary" size="lg" fullWidth leftIcon={icon} disabled={isDisabled || locked || !training.start}
      onClick={() => setModal('start')}>{label}</Button>
  );

  // Notice-виды вместо кнопки и пояснения над ней
  const above: React.ReactNode[] = [];
  if (view.kind === 'form' && view.stopped) {
    above.push(<Notice key="stopped" tone="info" icon={Info}>{TRAIN_TEXT.stopped}</Notice>);
  }
  if ((view.kind === 'form' || view.kind === 'done') && view.photosChanged) {
    above.push(<Notice key="changed" tone="warning" icon={AlertCircle}>{TRAIN_TEXT.photosChanged}</Notice>);
  }
  if (view.kind === 'done') {
    const runId = view.version.runId;
    above.push(view.first ? (
      <Notice key="done" tone="info" icon={Info} title={TRAIN_TEXT.doneFirstTitle}
        action={(
          <IconButton size="xs" title="Закрыть" ariaLabel="Закрыть" onClick={() => dismiss(runId)}>
            <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </IconButton>
        )}>
        {TRAIN_TEXT.doneFirstBody}
      </Notice>
    ) : (
      <Notice key="done" tone="info" icon={Info} title={TRAIN_TEXT.doneNewTitle}
        action={(
          <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
            <Button variant="secondary" size="sm" disabled={disabled} loading={versions.pending.includes(runId)}
              onClick={() => { void versions.ctl?.connect(runId).then(ok => { if (ok) dismiss(runId); }); }}>
              {TRAIN_TEXT.connect}
            </Button>
            <Button variant="ghost" size="sm" disabled={disabled} onClick={() => dismiss(runId)}>{TRAIN_TEXT.keepOld}</Button>
          </div>
        )}>
        {TRAIN_TEXT.doneNewBody}
      </Notice>
    ));
  }

  let body: React.ReactNode = null;
  switch (action.kind) {
    case 'notice': {
      const v = action.view;
      if (v.kind === 'loading') {
        body = main(<Button variant="primary" size="lg" fullWidth loading disabled>{HERO.train}</Button>, TRAIN_TEXT.loading);
      } else if (v.kind === 'off') {
        body = <Notice tone="info" icon={Info}>{TRAIN_TEXT.disabled}</Notice>;
      } else if (v.kind === 'noLocal') {
        body = <Notice tone="warning" icon={AlertCircle}>{TRAIN_TEXT.noLocal}</Notice>;
      } else if (v.kind === 'busyOther') {
        body = (
          <Notice tone="info" icon={Clock}>
            {v.text}
            {v.canStopForeign && isAdmin && (
              <div style={{ marginTop: SP.xs }}>
                <Button variant="ghost" size="sm" onClick={() => setModal('foreign')} style={{ color: C.dangerText }}>
                  <Square {...ico} />{TRAIN_TEXT.stopForeign}
                </Button>
              </div>
            )}
          </Notice>
        );
      } else if (v.kind === 'busyMine') {
        body = <Notice tone="info" icon={Info}>{TRAIN_TEXT.busyMine}</Notice>;
      }
      break;
    }
    case 'progress': {
      const text = view.kind === 'running' ? view.text : TRAIN_TEXT.queued;
      const unknownEta = TRAIN_ETA_UNKNOWN(formatDuration(TRAIN_ETA_FALLBACK_SEC));
      const eta = view.kind === 'running' ? (view.etaText ?? unknownEta) : unknownEta;
      body = (
        <div data-hero="main" style={{
          background: C.accentLight, border: `1px solid ${C.accentMuted}`, borderRadius: R.lg, padding: SP.md,
          display: 'flex', flexDirection: 'column', gap: SP.sm,
        }}>
          <div style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{text}</div>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm }}>
            <span style={{ fontSize: FS.sm, color: C.textMuted, minWidth: 0 }}>{eta}</span>
            <Button variant="ghost" size="sm" disabled={locked} onClick={() => setModal('cancel')}>
              <Square {...ico} />{HERO.cancel}
            </Button>
          </div>
        </div>
      );
      break;
    }
    case 'train':
      body = (
        <>
          {main(startBtn(HERO.train, <Sparkles {...ico} />, action.disabled), action.fixTrigger ? undefined : HERO.trainMeta)}
          {action.fixTrigger && (
            <div style={{ fontSize: FS.sm, color: C.textMuted }}>
              {HERO.fixTrigger}{' '}
              <Button variant="ghostAccent" size="xs" onClick={onOpenTune}>{CHIPS.expert}</Button>
            </div>
          )}
        </>
      );
      break;
    case 'addTrainPhotos':
      body = main(
        <Button variant="primary" size="lg" fullWidth leftIcon={<Upload {...ico} />} onClick={() => onOpenPhotos('train')}>
          {HERO.addTrainPhotos}
        </Button>,
        HERO.trainNeedMeta(action.count),
      );
      break;
    case 'replaceTrainPhotos':
      body = main(
        <Button variant="primary" size="lg" fullWidth leftIcon={<Upload {...ico} />} onClick={() => onOpenPhotos('train')}>
          {HERO.replaceTrainPhotos}
        </Button>,
      );
      break;
    case 'retry':
      body = main(startBtn(HERO.retry, <RefreshCw {...ico} />));
      break;
    case 'toContext':
      body = contextSessionId
        ? <div data-hero="main"><ContextAddButton sessionId={contextSessionId} projectId={projectId} size="lg" toggle
          candidate={{ kind: 'image-character', ref: { slug } }} /></div>
        : main(<Button variant="primary" size="lg" fullWidth disabled>{HERO.toChatContext}</Button>, HERO.noChat);
      break;
  }

  const confirm = confirmParts(hasConnected);
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      {above}
      {body}
      {connectError && <ErrorLine text={connectError} />}
      {actionError && <ErrorLine text={actionError} />}
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

function ErrorLine({ text }: { text: string }) {
  return (
    <div style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start', fontSize: FS.sm, color: C.dangerText, overflowWrap: 'anywhere' }}>
      <AlertCircle size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flex: '0 0 auto', marginTop: 1 }} />{text}
    </div>
  );
}

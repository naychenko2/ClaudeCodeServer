// Список «Версии персонажа» в карточке сохранённого персонажа (ADR-026 §7.3, макет character-lora-train).
// Вид строки и доступные действия — в loraVersionsView; здесь разметка и состояние диалогов.

import { useState } from 'react';
import { AlertCircle, Check, Cpu, Info, Layers, Pencil, Plug, Trash2 } from 'lucide-react';
import {
  Badge, Button, ConfirmDialog, Field, IconButton, Notice, TextField, ICON_SIZE, ICON_STROKE, C, FS, R, SP,
} from 'aihome_shell/kit';
import type { LoraVersionDto } from '../api';
import { useIsMobile } from '../../../lib/breakpoints';
import type { LoraVersionsHandle } from './useLoraVersions';
import {
  VERSIONS_TEXT, VERSION_CONNECTED_FLASH, VERSION_DELETE_TITLE, VERSION_REVERT_TITLE, TRAIN_VERSION_LIMIT,
} from './loraTrainTexts';
import { failureText, versionFailureText } from './loraTrainView';
import {
  atVersionLimit, needsRevertConfirm, validateVersionTitle, versionActions, versionMark, versionTitle,
} from './loraVersionsView';

const ico = { size: ICON_SIZE.sm, strokeWidth: ICON_STROKE } as const;

export function CharacterLoraVersions({ versions, disabled }: {
  versions: LoraVersionsHandle;
  disabled?: boolean;
}) {
  const isMobile = useIsMobile();
  const { data, ctl, pending } = versions;
  const [renaming, setRenaming] = useState<{ runId: string; draft: string; error: string | null } | null>(null);
  const [modal, setModal] = useState<{ kind: 'revert' | 'delete'; runId: string } | null>(null);
  const [flash, setFlash] = useState<string | null>(null);

  const items = data?.items ?? [];
  const nameOf = (v: LoraVersionDto) => versionTitle(v, items);
  const target = modal ? items.find(v => v.runId === modal.runId) ?? null : null;

  const connect = async (v: LoraVersionDto) => {
    setFlash(null);
    const name = nameOf(v);
    if (await ctl?.connect(v.runId)) setFlash(name);
  };

  const saveRename = async () => {
    if (!renaming) return;
    const r = validateVersionTitle(renaming.draft);
    if (!r.ok) { setRenaming({ ...renaming, error: r.error }); return; }
    if (await ctl?.rename(renaming.runId, r.title)) setRenaming(null);
  };

  const err = versions.error
    ? (versions.error.reason === 'bad_title' ? VERSIONS_TEXT.titleTooLong : failureText(versions.error))
    : null;

  let body: React.ReactNode;
  if (!data && versions.loading) {
    body = null;
  } else if (!data) {
    body = err ? <ErrorLine text={err} /> : null;
  } else if (!items.length) {
    body = <div style={{ fontSize: FS.sm, color: C.textMuted, lineHeight: 1.45 }}>{VERSIONS_TEXT.empty}</div>;
  } else {
    body = (
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
        {items.map(v => {
          const mark = versionMark(v);
          const acts = versionActions(v);
          const busy = pending.includes(v.runId) || !!disabled;
          const rowStyle = {
            display: 'flex', flexDirection: 'column', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px`, minWidth: 0,
            borderRadius: R.lg, background: C.bgCard,
            border: `1px solid ${v.connected ? C.success : C.borderLight}`,
          } as const;
          if (renaming?.runId === v.runId) {
            return (
              <div key={v.runId} style={rowStyle}>
                <Field error={renaming.error ?? undefined}>
                  <TextField value={renaming.draft} placeholder={VERSIONS_TEXT.renamePlaceholder} autoFocus
                    invalid={!!renaming.error} disabled={busy}
                    onChange={d => setRenaming({ ...renaming, draft: d, error: null })}
                    onEnter={() => { void saveRename(); }} onEscape={() => setRenaming(null)} />
                </Field>
                <div style={{ display: 'flex', gap: SP.xs }}>
                  <Button variant="primary" size="sm" loading={pending.includes(v.runId)} disabled={busy}
                    onClick={() => { void saveRename(); }}>{VERSIONS_TEXT.renameSave}</Button>
                  <Button variant="ghost" size="sm" disabled={busy} onClick={() => setRenaming(null)}>
                    {VERSIONS_TEXT.renameCancel}
                  </Button>
                </div>
              </div>
            );
          }
          return (
            <div key={v.runId} style={rowStyle}>
              <div style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: `${SP.xs}px ${SP.sm}px`, minWidth: 0 }}>
                <span style={{
                  flex: '1 1 140px', minWidth: 0, fontSize: FS.base, fontWeight: 600, color: C.textHeading,
                  lineHeight: 1.4, overflowWrap: 'anywhere',
                }}>{nameOf(v)}</span>
                <Badge tone={mark.tone} size="sm">
                  {v.connected && <Check size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}{mark.text}
                </Badge>
              </div>
              {v.status === 'training' && v.title?.trim() && (
                <div style={{ display: 'flex', gap: SP.xs, alignItems: 'center', fontSize: FS.sm, color: C.textMuted }}>
                  <Cpu {...ico} />{VERSIONS_TEXT.trainingHint}
                </div>
              )}
              {v.status === 'failed' && (
                <div style={{ fontSize: FS.sm, color: C.textMuted, overflowWrap: 'anywhere' }}>{versionFailureText(v)}</div>
              )}
              {acts.length > 0 && (
                <div style={{ display: 'flex', flexWrap: 'nowrap', alignItems: 'center', gap: SP.xs, minWidth: 0 }}>
                  {acts.includes('connect') && (
                    <Button variant="primary" size="sm" loading={pending.includes(v.runId)} disabled={busy}
                      style={isMobile ? { flex: '1 1 auto', minWidth: 0 } : undefined}
                      onClick={() => {
                        if (needsRevertConfirm(v, items)) setModal({ kind: 'revert', runId: v.runId });
                        else void connect(v);
                      }}>
                      <Plug {...ico} />{VERSIONS_TEXT.connect}
                    </Button>
                  )}
                  <span style={{ flex: 1 }} />
                  {acts.includes('rename') && (
                    <IconButton title={VERSIONS_TEXT.rename} size={isMobile ? 'lg' : 'md'} disabled={busy}
                      onClick={() => { setFlash(null); setRenaming({ runId: v.runId, draft: v.title ?? '', error: null }); }}>
                      <Pencil {...ico} />
                    </IconButton>
                  )}
                  {acts.includes('remove') && (
                    <IconButton title={VERSIONS_TEXT.remove} size={isMobile ? 'lg' : 'md'} tone="danger" disabled={busy}
                      onClick={() => { setFlash(null); setModal({ kind: 'delete', runId: v.runId }); }}>
                      <Trash2 {...ico} />
                    </IconButton>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
    );
  }

  return (
    <div style={{ border: `1px solid ${C.border}`, borderRadius: R.md, minWidth: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px` }}>
        <span style={{ fontSize: FS.base, fontWeight: 600, color: C.textHeading }}>{VERSIONS_TEXT.title}</span>
        {items.length > 0 && <Badge tone="neutral" size="sm">{items.length}</Badge>}
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, padding: `0 ${SP.md}px ${SP.md}px`, minWidth: 0 }}>
        {flash && (
          <div role="status" style={{
            display: 'flex', gap: SP.xs, alignItems: 'flex-start', background: C.successBg, color: C.successText,
            borderRadius: R.xl, padding: SP.md, fontSize: FS.sm, lineHeight: 1.45,
          }}>
            <Check {...ico} style={{ flex: '0 0 auto', marginTop: 1 }} />{VERSION_CONNECTED_FLASH(flash)}
          </div>
        )}
        {data?.own && (
          <Notice tone="info" icon={Info}>
            {VERSIONS_TEXT.own}<span style={{ fontFamily: 'inherit', fontWeight: 600 }}>{data.own.lora}</span>
          </Notice>
        )}
        {data && atVersionLimit(data.used, data.limit) && (
          <Notice tone="warning" icon={Layers}>{TRAIN_VERSION_LIMIT}</Notice>
        )}
        {body}
        {data && err && <ErrorLine text={err} />}
      </div>
      {modal?.kind === 'revert' && target && (
        <ConfirmDialog title={VERSION_REVERT_TITLE(nameOf(target))}
          confirmLabel={VERSIONS_TEXT.revertOk} cancelLabel={VERSIONS_TEXT.revertKeep}
          onCancel={() => setModal(null)}
          onConfirm={async () => { setModal(null); await connect(target); }} />
      )}
      {modal?.kind === 'delete' && target && (
        <ConfirmDialog title={VERSION_DELETE_TITLE(nameOf(target))}
          subtitle={target.connected
            ? <>{VERSIONS_TEXT.deleteConnected}<br /><br />{VERSIONS_TEXT.deleteBody}</>
            : VERSIONS_TEXT.deleteBody}
          confirmLabel={VERSIONS_TEXT.deleteOk} cancelLabel={VERSIONS_TEXT.deleteKeep} confirmVariant="danger"
          onCancel={() => setModal(null)}
          onConfirm={async () => { await ctl?.remove(target.runId); setModal(null); }} />
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

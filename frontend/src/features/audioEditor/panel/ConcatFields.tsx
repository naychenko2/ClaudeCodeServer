// Склейка: новый файл из кусков (макет audio-editor-v2-proposal.md, «Склейка: новый файл из кусков»).
// Куски — версии нитей этого чата и файлы проекта, сверху вниз. Общий стык на все места плюс свой у
// отдельного места; свои стыки привязаны к месту, поэтому перестановка и удаление их сбрасывают.

import { useState } from 'react';
import { ArrowDown, ArrowUp, Plus, X } from 'lucide-react';
import { Button, Checkbox, IconButton, SegmentedControl, Select, TextField, C, FS, R, SP } from 'aihome_shell/kit';
import type { AudioJoint, AudioJointKind, AudioThread } from '../api';
import { focusLabel } from '../strip/summary';
import { FORMATS } from './OpFields';
import type { ConcatInputs, ConcatPiece } from './inputs';
import { Hint, ic, Label } from './primitives';

const KIND_LABEL: Record<AudioJointKind, string> = { butt: 'Встык', pause: 'Пауза', crossfade: 'Плавный переход' };

export function jointLabel(j: AudioJoint): string {
  if (j.kind === 'butt') return 'встык';
  const s = String(j.seconds).replace('.', ',');
  return j.kind === 'pause' ? `пауза ${s} с` : `плавно ${s} с`;
}

// Куски нитей чата: каждая версия каждой нити
export function chatPieces(threads: AudioThread[]): ConcatPiece[] {
  return threads.flatMap(t => t.versions.map(v => ({
    threadId: t.id, versionId: v.id,
    label: focusLabel({ ...t, currentVersionId: v.id }),
  })));
}

// Перестановка или удаление сбрасывают свои стыки на общий
const resetJoints = (n: number): (AudioJoint | null)[] => Array.from({ length: Math.max(n - 1, 0) }, () => null);

export function ConcatFields({ c, set, threads, personal }: {
  c: ConcatInputs; set: (patch: Partial<ConcatInputs>) => void; threads: AudioThread[]; personal: boolean;
}) {
  const [file, setFile] = useState('');
  const options = chatPieces(threads);
  const add = (p: ConcatPiece) => set({ pieces: [...c.pieces, p], joints: [...c.joints, null].slice(0, c.pieces.length) });
  const move = (i: number, d: number) => {
    const next = [...c.pieces];
    [next[i], next[i + d]] = [next[i + d], next[i]];
    set({ pieces: next, joints: resetJoints(next.length) });
  };
  const remove = (i: number) => {
    const next = c.pieces.filter((_, j) => j !== i);
    set({ pieces: next, joints: resetJoints(next.length) });
  };
  const setJoint = (i: number, j: AudioJoint | null) => {
    const joints = [...resetJoints(c.pieces.length).map((x, k) => c.joints[k] ?? x)];
    joints[i] = j;
    set({ joints });
  };
  const own = c.joints.filter(Boolean).length;

  return (
    <div data-op-fields="concat">
      <Label aside="без ИИ · результат — новый файл">Куски · {c.pieces.length}</Label>
      {c.pieces.map((p, i) => (
        <div key={`${i}:${p.threadId ?? p.projectFile}:${p.versionId ?? ''}`}>
          <div data-piece={i} style={{
            display: 'flex', alignItems: 'center', gap: SP.xs, padding: `${SP.xxs}px ${SP.xs}px`,
            border: `1px solid ${C.borderLight}`, borderRadius: R.lg, fontSize: FS.sm, color: C.textPrimary,
          }}>
            <span style={{ color: C.textMuted, fontSize: FS.xs }}>{i + 1}</span>
            <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={p.label}>{p.label}</span>
            <IconButton size="xs" title="Выше" ariaLabel="Выше" disabled={i === 0} onClick={() => move(i, -1)}>{ic(ArrowUp)}</IconButton>
            <IconButton size="xs" title="Ниже" ariaLabel="Ниже" disabled={i === c.pieces.length - 1} onClick={() => move(i, 1)}>{ic(ArrowDown)}</IconButton>
            <IconButton size="xs" title="Убрать кусок" ariaLabel="Убрать кусок" onClick={() => remove(i)}>{ic(X)}</IconButton>
          </div>
          {i < c.pieces.length - 1 && (
            <div data-joint={i} style={{ display: 'flex', alignItems: 'center', gap: SP.xs, margin: `${SP.xxs}px 0 ${SP.xxs}px ${SP.lg}px`, borderLeft: `1px dashed ${C.border}`, paddingLeft: SP.sm }}>
              <span style={{ fontSize: FS.xs, color: c.joints[i] ? C.accent : C.textMuted }}>
                Стык {i + 1} → {i + 2}: {jointLabel(c.joints[i] ?? c.joint)}{c.joints[i] ? ' · свой' : ''}
              </span>
              <div style={{ width: 130 }}>
                <Select<string> value={c.joints[i] ? c.joints[i]!.kind : ''} placeholder="Как у всех" title={`Стык ${i + 1} → ${i + 2}`}
                  options={(['butt', 'pause', 'crossfade'] as AudioJointKind[]).map(k => ({ value: k, label: KIND_LABEL[k] }))}
                  onChange={v => setJoint(i, v ? { kind: v as AudioJointKind, seconds: c.joints[i]?.seconds ?? c.joint.seconds } : null)} />
              </div>
            </div>
          )}
        </div>
      ))}
      <div style={{ marginTop: SP.xs }}>
        <Select<string> value="" placeholder="＋ Кусок из звука чата" title="Звук этого чата"
          options={options.map((o, k) => ({ value: String(k), label: o.label }))}
          onChange={v => { if (v !== '') add(options[Number(v)]); }} />
      </div>
      {!personal && (
        <div style={{ display: 'flex', gap: SP.xs, marginTop: SP.xs }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <TextField value={file} onChange={setFile} placeholder="или файл проекта: audio/jingle.mp3" />
          </div>
          <Button size="xs" variant="secondary" leftIcon={ic(Plus)} disabled={!file.trim()}
            onClick={() => { add({ projectFile: file.trim(), label: file.trim() }); setFile(''); }}>Кусок</Button>
        </div>
      )}

      <Label>Между кусками</Label>
      <SegmentedControl<AudioJointKind> value={c.joint.kind} onChange={kind => set({ joint: { ...c.joint, kind } })}
        options={(['butt', 'pause', 'crossfade'] as AudioJointKind[]).map(k => ({ value: k, label: KIND_LABEL[k] }))} />
      {c.joint.kind !== 'butt' && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.xs }}>
          <input type="range" min={0.1} max={5} step={0.1} value={c.joint.seconds} aria-label="Длина стыка, с"
            onChange={e => set({ joint: { ...c.joint, seconds: Number(e.target.value) } })}
            style={{ flex: 1, minWidth: 0, accentColor: C.accent }} />
          <span style={{ fontSize: FS.sm, color: C.textSecondary, minWidth: 40, textAlign: 'right' }}>{String(c.joint.seconds).replace('.', ',')} с</span>
        </div>
      )}
      <Hint>
        {c.joint.kind === 'crossfade' ? 'Куски звучат внахлёст — итог короче на длину перехода. ' : ''}
        Действует на все стыки, кроме своих{own ? ` (${own})` : ''}.
      </Hint>

      <label style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textPrimary, marginTop: SP.sm }}>
        <Checkbox checked={c.normalize} onChange={v => set({ normalize: v })} ariaLabel="Выровнять громкость кусков" />
        Выровнять громкость кусков · −16 LUFS
      </label>

      <Label>Имя нового файла</Label>
      <div style={{ display: 'flex', gap: SP.xs }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <TextField value={c.name} onChange={name => set({ name })} placeholder="podcast-full" />
        </div>
        <div style={{ width: 96 }}>
          <Select value={c.format} onChange={v => { if (v) set({ format: v }); }} options={FORMATS} />
        </div>
      </div>
      <Hint>Результат — новый файл отдельной карточкой в ленте. Исходные куски не меняются. Разные частоты сведём сами</Hint>
    </div>
  );
}

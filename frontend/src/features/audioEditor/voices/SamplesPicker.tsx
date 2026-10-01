// Набор записей для голоса: загрузка с устройства и пути файлов проекта (1–5 записей до 50 МБ).
// Общий для формы нового голоса и для «＋ Образец» у готового.

import { useRef, useState } from 'react';
import { FileAudio, Upload, X } from 'lucide-react';
import { Button, IconButton, TextField, C, FS, SP, ICON_SIZE, ICON_STROKE } from 'aihome_shell/kit';
import { MAX_VOICE_SAMPLES } from './api';
import { draftCount, type SampleDraft } from './model';

export interface SamplesValue { files: File[]; projectFiles: string[] }

export const EMPTY_SAMPLES: SamplesValue = { files: [], projectFiles: [] };

export const toDraft = (v: SamplesValue): SampleDraft => ({
  files: v.files.map(f => ({ name: f.name, size: f.size })), projectFiles: v.projectFiles,
});

const icon = (I: typeof X) => <I size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />;

export function SamplesPicker({ value, onChange, room = MAX_VOICE_SAMPLES, disabled }: {
  value: SamplesValue;
  onChange: (v: SamplesValue) => void;
  // Сколько записей ещё можно добавить
  room?: number;
  disabled?: boolean;
}) {
  const input = useRef<HTMLInputElement>(null);
  const [path, setPath] = useState('');
  const full = draftCount(toDraft(value)) >= room;

  const addPath = () => {
    const p = path.trim().replace(/^\/+/, '');
    if (!p || full || value.projectFiles.includes(p)) return;
    onChange({ ...value, projectFiles: [...value.projectFiles, p] });
    setPath('');
  };

  const row = (key: string, label: string, onRemove: () => void) => (
    <div key={key} style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textPrimary, minWidth: 0 }}>
      <span style={{ display: 'inline-flex', color: C.textMuted }}>{icon(FileAudio)}</span>
      <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={label}>{label}</span>
      <IconButton size="xs" title="Убрать запись" ariaLabel={`Убрать ${label}`} disabled={disabled} onClick={onRemove}>{icon(X)}</IconButton>
    </div>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      {value.files.map((f, i) => row(`f${i}:${f.name}`, f.name,
        () => onChange({ ...value, files: value.files.filter((_, j) => j !== i) })))}
      {value.projectFiles.map(p => row(`p:${p}`, p,
        () => onChange({ ...value, projectFiles: value.projectFiles.filter(x => x !== p) })))}
      <Button size="sm" variant="dashed" fullWidth leftIcon={icon(Upload)} disabled={disabled || full}
        onClick={() => input.current?.click()}>
        Загрузить запись
      </Button>
      <input ref={input} type="file" accept="audio/*,.wav,.mp3,.flac,.ogg,.m4a" multiple hidden
        onChange={e => {
          const picked = Array.from(e.target.files ?? []);
          e.target.value = '';
          if (picked.length) onChange({ ...value, files: [...value.files, ...picked] });
        }} />
      <div style={{ display: 'flex', gap: SP.xs, alignItems: 'center' }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <TextField value={path} onChange={setPath} placeholder="Или путь в проекте: audio/anya.wav" mono
            disabled={disabled || full} onEnter={addPath} />
        </div>
        <Button size="sm" variant="secondary" disabled={disabled || full || !path.trim()} onClick={addPath}>Добавить</Button>
      </div>
    </div>
  );
}

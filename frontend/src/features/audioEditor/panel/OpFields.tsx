// Поля правки без ИИ (громкость, затухание, формат, кусок) для редактора звука и список путей записей для обучения
// голоса. Поля операций панели «Звук» ушли вместе с панелью.

import { Plus, X } from 'lucide-react';
import { Button, Checkbox, IconButton, Select, TextField, C, FS, SP } from 'aihome_shell/kit';
import type { AudioFileFormat } from '../api';
import type { AudioSelection } from '../player/selection';
import type { TrimInputs } from './inputs';
import { PieceField, type PieceBinding } from './PieceField';
import { ic, Label } from './primitives';

// Список путей проекта (записи для обучения RVC)
export function PathList({ paths, onChange }: { paths: string[]; onChange: (p: string[]) => void }) {
  return (
    <div data-field="clips" style={{ marginBottom: SP.sm }}>
      {paths.map((p, i) => (
        <div key={i} style={{ display: 'flex', gap: SP.xs, alignItems: 'center', marginBottom: SP.xxs }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <TextField value={p} placeholder="путь в проекте: records/andrey-1.wav" onChange={v => onChange(paths.map((x, j) => (j === i ? v : x)))} />
          </div>
          <IconButton size="xs" title="Убрать запись" ariaLabel="Убрать запись" onClick={() => onChange(paths.filter((_, j) => j !== i))}>{ic(X)}</IconButton>
        </div>
      ))}
      <Button size="xs" variant="dashed" leftIcon={ic(Plus)} onClick={() => onChange([...paths, ''])} disabled={paths.length >= 20}>Запись</Button>
    </div>
  );
}

export const FORMATS: { value: AudioFileFormat; label: string }[] = [
  { value: 'wav', label: 'WAV' }, { value: 'mp3', label: 'MP3' },
  { value: 'flac', label: 'FLAC' }, { value: 'ogg', label: 'OGG' },
];

// Кусок обрезки — выделение нити, а не поле входов (panel/piece.ts)
export const trimReady = (t: TrimInputs, piece: AudioSelection | null) =>
  piece !== null || t.gainDb !== 0 || t.fadeIn > 0 || t.fadeOut > 0 || t.normalize;

function NumRow({ label, value, onChange, unit }: { label: string; value: string; onChange: (s: string) => void; unit?: string }) {
  return (
    <div style={{ flex: '1 1 120px', minWidth: 0 }}>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, marginBottom: SP.xxs }}>{label}{unit ? `, ${unit}` : ''}</div>
      <TextField value={value} onChange={onChange} placeholder="—" />
    </div>
  );
}

export function TrimFields({ t, set, piece, inEditor }: { t: TrimInputs; set: (patch: Partial<TrimInputs>) => void; piece: PieceBinding | null; inEditor?: boolean }) {
  const n = (s: string) => Number(s.replace(',', '.')) || 0;
  return (
    <div data-op-fields="trim">
      {piece && <PieceField binding={piece} aside="без ИИ · каждая правка — новая версия" inEditor={inEditor} />}
      <Label>Громкость</Label>
      <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
        <NumRow label="Громкость" unit="дБ" value={t.gainDb ? String(t.gainDb) : ''} onChange={s => set({ gainDb: n(s) })} />
        <NumRow label="Нарастание" unit="с" value={t.fadeIn ? String(t.fadeIn) : ''} onChange={s => set({ fadeIn: Math.max(0, n(s)) })} />
        <NumRow label="Затухание" unit="с" value={t.fadeOut ? String(t.fadeOut) : ''} onChange={s => set({ fadeOut: Math.max(0, n(s)) })} />
      </div>
      <label style={{ display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, color: C.textPrimary, marginTop: SP.xs }}>
        <Checkbox checked={t.normalize} onChange={v => set({ normalize: v })} ariaLabel="Нормализовать до −14 LUFS" />
        Нормализовать до −14 LUFS
      </label>
      <Label>Формат</Label>
      <Select<AudioFileFormat> value={t.format} placeholder="Как у исходника" onChange={v => set({ format: v })} options={FORMATS} />
    </div>
  );
}

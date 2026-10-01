// Секция «Размер» и предупреждение о модели, которая не возьмёт текущую задачу

import { AlertTriangle } from 'lucide-react';
import { Checkbox, C, R, SP } from 'aihome_shell/kit';
import type { ImageThread } from '../../thread/threadsApi';
import { threadHasImage } from '../../thread/useThreadLaunch';
import { ic, Label, type Launch } from './primitives';

// Только у картинки с файлом: у черновика оригинала нет
export function SizeSection({ L, thread }: { L: Launch; thread: ImageThread | null }) {
  if (!thread || !threadHasImage(thread)) return null;
  return (
    <>
      <Label>Размер</Label>
      <label style={{ display: 'inline-flex', alignItems: 'center', gap: SP.sm, cursor: 'pointer', color: C.textPrimary }}>
        <Checkbox checked={L.settings.matchSourceSize} onChange={v => L.setSettings({ matchSourceSize: v })} ariaLabel="Размер оригинала" />
        Вернуть в размере оригинала
      </label>
    </>
  );
}

export function BlockedNotice({ L }: { L: Launch }) {
  if (!L.blocked || !L.model) return null;
  return (
    <div style={{ display: 'flex', gap: SP.xs, alignItems: 'flex-start', marginTop: SP.md, padding: `${SP.xs}px ${SP.sm}px`, background: C.warningBg, color: C.warningText, borderRadius: R.md }}>
      {ic(AlertTriangle)}<span>{L.model.label}: {L.blocked.charAt(0).toLowerCase() + L.blocked.slice(1)}</span>
    </div>
  );
}

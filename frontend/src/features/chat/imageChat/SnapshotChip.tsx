// Снимок холста в композере чата картинки (ADR-018 §3, макет image-editor-v2): холст
// изменился — чип с пунктиром «hero.png · 3 пометки», нет — бледный «без изменений».
// Крестик снимает снимок с сообщений, пункт «+» возвращает.

import { Camera, X } from 'lucide-react';
import { C, FS, R, SP } from '../../../lib/design';
import { Button, IconButton } from '../../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../../components/ui/icons';
import type { ImageChatSnapshotChip } from '../../../lib/subsystems/registryCore';

export function SnapshotChip({ chip }: { chip: ImageChatSnapshotChip }) {
  return (
    <span data-image-snapshot-chip={chip.changed ? 'changed' : 'same'}
      title={chip.changed
        ? 'Холст изменился с прошлого сообщения — снимок с пометками приложится'
        : 'Холст не менялся с прошлого сообщения — снимок не приложится, агент его уже видел'}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xs, height: 30, padding: `0 ${SP.xxs}px 0 ${SP.sm}px`,
        borderRadius: R.md, border: `1px ${chip.changed ? 'dashed' : 'solid'} ${chip.changed ? C.accent : C.border}`,
        background: chip.changed ? C.accentLight : 'transparent',
        color: chip.changed ? C.textSecondary : C.textMuted, fontSize: FS.xs, maxWidth: '100%', minWidth: 0,
      }}>
      <Camera size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{chip.label}</span>
      <IconButton size="xs" title="Не прикладывать картинку" ariaLabel="Не прикладывать картинку" onClick={() => chip.onToggle(false)}>
        <X size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </IconButton>
    </span>
  );
}

// Строка пикера «+»: вернуть снятый снимок
export function SnapshotPickerRow({ chip, onDone }: { chip: ImageChatSnapshotChip; onDone: () => void }) {
  return (
    <div style={{ marginBottom: SP.sm, display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
      <Button variant="secondary" size="sm" leftIcon={<Camera size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        onClick={() => { chip.onToggle(true); onDone(); }}>
        Снимок холста с пометками
      </Button>
      <span style={{ fontSize: FS.sm, color: C.textMuted }}>Прикладывать, когда холст изменился</span>
    </div>
  );
}

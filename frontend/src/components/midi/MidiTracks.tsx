import { C, FONT, FS, GROUP_COLORS, R, SP } from '../../lib/design';
import { useIsMobile } from '../../lib/breakpoints';
import { IconButton } from '../ui/IconButton';
import type { MidiTrackView } from './midiModel';
import { isSolo } from './midiViewerLogic';

// Строка чипов дорожек с заглушением (M) и соло (S); при одной дорожке не нужна.

export type MidiTracksProps = {
  tracks: MidiTrackView[];
  muted: Set<number>;
  onToggleMute(index: number): void;
  onToggleSolo(index: number): void;
};

export function MidiTracks({ tracks, muted, onToggleMute, onToggleSolo }: MidiTracksProps) {
  const isMobile = useIsMobile();
  if (tracks.length < 2) return null;
  const indices = tracks.map(t => t.index);

  return (
    <div
      role="group"
      aria-label="Дорожки"
      style={{
        display: 'flex', gap: SP.sm, padding: `${SP.xs}px 0`,
        flexWrap: isMobile ? 'nowrap' : 'wrap', overflowX: isMobile ? 'auto' : 'visible',
      }}
    >
      {tracks.map((t, pos) => {
        const isMuted = muted.has(t.index);
        const solo = isSolo(indices, muted, t.index);
        const name = t.name.trim() || `Дорожка ${pos + 1}`;
        return (
          <div
            key={t.index}
            style={{
              display: 'flex', alignItems: 'center', gap: SP.xs, flexShrink: 0,
              padding: `${SP.xxs}px ${SP.xxs}px ${SP.xxs}px ${SP.sm}px`,
              border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgWhite,
              opacity: isMuted ? 0.6 : 1, fontFamily: FONT.sans, fontSize: FS.sm,
            }}
          >
            <span aria-hidden style={{
              width: SP.sm, height: SP.sm, borderRadius: R.max, flexShrink: 0,
              background: GROUP_COLORS[t.index % GROUP_COLORS.length],
            }} />
            <span style={{ color: C.textPrimary, maxWidth: 140, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {name}
            </span>
            <span style={{ color: C.textMuted, fontSize: FS.xs }}>{t.notes.length}</span>
            <IconButton size={isMobile ? 'lg' : 'xs'} active={isMuted} title={isMuted ? 'Включить дорожку' : 'Заглушить'} onClick={() => onToggleMute(t.index)}>
              <span style={{ fontSize: FS.xs, fontWeight: 700 }}>M</span>
            </IconButton>
            <IconButton size={isMobile ? 'lg' : 'xs'} active={solo} title={solo ? 'Снять соло' : 'Только эта'} onClick={() => onToggleSolo(t.index)}>
              <span style={{ fontSize: FS.xs, fontWeight: 700 }}>S</span>
            </IconButton>
          </div>
        );
      })}
    </div>
  );
}

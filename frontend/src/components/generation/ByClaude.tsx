import type { CSSProperties } from 'react';
import { Sparkles } from 'lucide-react';
import { C, FS, R, SP } from '../../lib/design';
import { ICON_STROKE } from '../ui/icons';

// Метка «✦ Claude» (.by2 в макете «Видео» v7): у подписи секции и на элементе списка
// отмечает то, что поправил агент. Правка человеком метку снимает — решает вертикаль,
// метка сама только рисуется.
const BY = { h: 16, icon: 9, padX: 5 } as const;

export function ByClaude({ title = 'Правка Claude — ваша правка снимет метку', style }: { title?: string; style?: CSSProperties }) {
  return (
    <span data-by-claude="" title={title} style={{
      display: 'inline-flex', alignItems: 'center', gap: SP.xxs, flexShrink: 0, height: BY.h,
      padding: `0 ${BY.padX}px`, borderRadius: R.sm, whiteSpace: 'nowrap',
      fontSize: FS.xs, fontWeight: 600, color: C.accent, background: C.accentLight, ...style,
    }}>
      <Sparkles size={BY.icon} strokeWidth={ICON_STROKE} />Claude
    </span>
  );
}

// Значок происхождения строки: заметка, ИИ, вместе, вы, «изменена снаружи» (записка макета
// notes-wiki-unified-v1, §0). Значение несёт форма иконки, цвет только подсказывает — оттенки
// различимы и в тёмной теме. C.accent в значках не используем: он за главным действием.

import type { CSSProperties } from 'react';
import { FileText, PenLine, Sparkles, TriangleAlert, Users } from 'lucide-react';
import { C, R } from '../../../lib/design';
import { ICON_STROKE } from '../../../components/ui/icons';

export type OriginKind = 'note' | 'ai' | 'me' | 'mix' | 'ext';
export type OriginGlyphSize = 'xs' | 'sm';

// Подсказка и подпись для скринридера — один текст
export const ORIGIN_LABEL: Record<OriginKind, string> = {
  note: 'Заметка',
  ai: 'Ведёт ИИ',
  me: 'Написали вы',
  mix: 'Ведёте вместе с ИИ',
  ext: 'Изменена снаружи',
};

const ICON = { note: FileText, ai: Sparkles, me: PenLine, mix: Users, ext: TriangleAlert } as const;

const LOOK: Record<OriginKind, Pick<CSSProperties, 'color' | 'background'>> = {
  note: { color: C.textMuted },
  ai: { color: C.info, background: C.infoBg },
  me: { color: C.textSecondary, background: C.bgSelected },
  mix: { color: C.info, background: `linear-gradient(135deg, ${C.infoBg} 50%, ${C.bgSelected} 50%)` },
  ext: { color: C.warningText, background: C.warningBg },
};

// Размер — размер иконки (xs 10 / sm 12); подложка одна — 18×18
const GLYPH_PX: Record<OriginGlyphSize, number> = { xs: 10, sm: 12 };
const BOX = 18;

export function OriginGlyph({ kind, size = 'xs' }: { kind: OriginKind; size?: OriginGlyphSize }) {
  const Icon = ICON[kind];
  return (
    <span
      role="img"
      aria-label={ORIGIN_LABEL[kind]}
      title={ORIGIN_LABEL[kind]}
      data-origin={kind}
      style={{
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        width: BOX, height: BOX, borderRadius: R.sm, flexShrink: 0, ...LOOK[kind],
      }}
    >
      <Icon size={GLYPH_PX[size]} strokeWidth={ICON_STROKE} aria-hidden />
    </span>
  );
}

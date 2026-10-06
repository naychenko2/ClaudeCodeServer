import { C, FONT, R } from '../../lib/design';
import { GlyphIcon, isLucideIconName } from '../../lib/projectGlyphs';

interface SphereLike { name: string; color: string; icon?: string | null }

const GLYPH_RATIO = 0.6;

// Пока чанк глифа едет (или не доедет), плитка остаётся цветной без значка
const Blank = () => null;

// Плитка сферы: цвет сферы + белый глиф lucide, а без значка — первая буква имени
// (как инициалы проекта, когда глифа нет). Имя вне набора lucide тихо уходит в букву.
export function SphereTile({ sphere, size = 22 }: { sphere: SphereLike; size?: number }) {
  const letter = (sphere.name.trim()[0] ?? '?').toUpperCase();
  const showGlyph = !!sphere.icon && isLucideIconName(sphere.icon);
  const radius = size >= 40 ? R.xl : size >= 28 ? R.md : R.sm;
  return (
    <span
      aria-hidden
      style={{
        width: size, height: size, borderRadius: radius, flexShrink: 0, userSelect: 'none',
        background: sphere.color || C.textMuted, color: C.onDark,
        display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
      }}
    >
      {showGlyph
        ? <GlyphIcon name={sphere.icon!} fallback={Blank} size={Math.round(size * GLYPH_RATIO)} strokeWidth={size < 16 ? 2.4 : 2} />
        : <span style={{ fontFamily: FONT.sans, fontWeight: 700, fontSize: Math.round(size * 0.45), lineHeight: 1 }}>{letter}</span>}
    </span>
  );
}

// Плитка секции «Без сферы»: пунктир без цвета
export function NoSphereTile({ size = 22 }: { size?: number }) {
  return (
    <span
      aria-hidden
      style={{
        width: size, height: size, borderRadius: size >= 28 ? R.md : R.sm, flexShrink: 0, boxSizing: 'border-box',
        border: `1.5px dashed ${C.dashed}`, background: 'transparent',
      }}
    />
  );
}

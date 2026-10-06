import { ChevronRight } from 'lucide-react';
import type { ProjectGroup } from '../../types';
import { C, FONT, FS } from '../../lib/design';
import { ICON_SIZE } from '../../components/ui/icons';
import { NoSphereTile, SphereTile } from './SphereTile';

interface Props {
  // null — секция «Без сферы»
  sphere: ProjectGroup | null;
  count: number;
  onOpenPage?: (id: string) => void;
  // Мобильный вид: вместо ссылки «Страница сферы ›» — только шеврон
  compact?: boolean;
}

// Заголовок сферы в списке проектов: плитка с глифом, имя, счётчик проектов и ссылка на страницу.
export function SphereHeader({ sphere, count, onOpenPage, compact }: Props) {
  const name = sphere ? sphere.name : 'Без сферы';
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, margin: compact ? '6px 2px 3px' : '0 0 9px' }}>
      {sphere ? <SphereTile sphere={sphere} size={22} /> : <NoSphereTile size={22} />}
      <span style={{
        fontSize: compact ? FS.sm : FS.md, fontWeight: 700, color: sphere ? C.textPrimary : C.textMuted,
        fontFamily: FONT.sans, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0,
      }}>
        {name}
      </span>
      <span style={{ fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>{count}</span>
      <div style={{ flex: 1, height: 1, background: C.divider }} />
      {sphere && onOpenPage && (
        <button
          type="button" onClick={() => onOpenPage(sphere.id)} title="Страница сферы"
          style={{
            display: 'flex', alignItems: 'center', gap: 2, flexShrink: 0, background: 'none', border: 'none', padding: 0,
            cursor: 'pointer', color: C.accent, fontFamily: FONT.sans, fontSize: FS.sm,
          }}
        >
          {!compact && 'Страница сферы'}
          <ChevronRight size={ICON_SIZE.sm} strokeWidth={2} />
        </button>
      )}
    </div>
  );
}

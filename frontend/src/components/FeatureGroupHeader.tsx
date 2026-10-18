import { ChevronRight } from 'lucide-react';
import { C, R, SP, FS, FONT } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { FeatureGlyph } from './chat/feature/FeatureGlyphs';
import { FeatureRail } from './chat/feature/FeatureRail';
import { CountBadge } from './ui';
import { featureDisplayState, formatDuration } from '../lib/chatGroups';
import { dateOnly } from './chat/feature/featureTime';
import type { FeatureIndexFeature } from '../types/featureIndex';

// Заголовок группы фичи в списке (макет features-overview.html, .fgh):
//   строка 1: [шеvron] [глиф] [название] [счётчик]
//   строка 2: [мини-рельса] [статус]
// Кромка тревоги 3px слева — у «ждёт вас» (info) и «упала» (danger).
// Клик по заголовку сворачивает/разворачивает группу (группа свёрнута по умолчанию).
// Компактный: на 360 px живёт в двух строках, только токены C.* и шкалы, обе темы.
// compact — закрытая фича «на втором плане» (макет .closed .fgh): без рамки/заливки,
//   название вторичным, статус «смержена {дата} · {длительность}» (или «закрыта»).
export function FeatureGroupHeader({ feature, count, expanded, onToggle, isMobile = false, compact = false, personaName, now }: {
  feature: FeatureIndexFeature;
  // Число чатов группы (счётчик в заголовке)
  count: number;
  // Группа раскрыта (шеvron повёрнут, под заголовком — все чаты)
  expanded: boolean;
  onToggle: () => void;
  isMobile?: boolean;
  compact?: boolean;
  // «Кто» в статусе: имя персоны (attention.personaId / now0.personaId, резолвит вызывающий)
  personaName?: string | null;
  // «Сейчас» в мс для «сколько» (стабильные тесты); не задано — Date.now()
  now?: number;
}) {
  const st = featureDisplayState(feature, { now, personaName });
  // Кромка тревоги 3px: 'wait' → info (синий), 'stop' → danger (красный), иначе нет
  const alarmEdge = st.alarm === 'wait' ? C.info : st.alarm === 'stop' ? C.danger : null;
  // Закрытая фича: статус «смержена {дата} · {длительность}» (макет .closed) или «закрыта».
  const closedMerged = feature.bucket === 'closed' && feature.summary?.mergedAt != null;
  const status = compact && feature.bucket === 'closed'
    ? (closedMerged
      ? `смержена ${dateOnly(feature.summary!.mergedAt)} · ${formatDuration(feature.summary!.durationMinutes)}`
      : 'закрыта')
    : st.status;
  return (
    <button
      type="button"
      onClick={onToggle}
      aria-expanded={expanded}
      data-testid="feature-group-header"
      data-feature-state={st.kind}
      style={{
        display: 'grid',
        gridTemplateColumns: '14px 14px 1fr auto',
        gridTemplateRows: 'auto auto',
        columnGap: 7, rowGap: 3,
        alignItems: 'center',
        width: '100%', boxSizing: 'border-box',
        // Компактная (закрытая): без рамки и заливки (макет .closed .fgh)
        border: compact ? '1px solid transparent' : `1px solid ${C.borderLight}`,
        // Кромка тревоги: 3px слева (вместо 1px) у «ждёт вас»/«упала»
        borderLeft: alarmEdge ? `3px solid ${alarmEdge}` : undefined,
        background: compact ? 'transparent' : C.bgCard,
        borderRadius: R.lg,
        padding: compact ? `${SP.xs}px ${SP.sm}px` : `${SP.sm}px ${SP.sm}px`,
        cursor: 'pointer', textAlign: 'left',
        minHeight: isMobile ? (compact ? 40 : 44) : undefined,
      }}
    >
      {/* Шеврон: поворот 90° при раскрытии (макет .fg.open .fgh .chev) */}
      <span style={{
        gridColumn: 1, display: 'flex', color: C.textMuted,
        transform: expanded ? 'rotate(90deg)' : undefined, transition: 'transform 0.15s',
      }}>
        <ChevronRight size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </span>
      {/* Глиф состояния фичи (wait/stopped/run/loop/closed/done) */}
      <span style={{ gridColumn: 2, display: 'flex', flexShrink: 0 }}>
        <FeatureGlyph kind={st.kind} size={13} />
      </span>
      {/* Название фичи: одна строка, обрезка многоточием. Компактное — вторичным (макет .closed .fgh .nm) */}
      <span
        title={feature.title}
        style={{
          gridColumn: 3, gridRow: 1, fontSize: FS.base,
          fontWeight: compact ? 600 : 700,
          color: compact ? C.textSecondary : C.textHeading,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          minWidth: 0,
        }}
      >
        {feature.title}
      </span>
      {/* Счётчик чатов группы */}
      <span style={{ gridColumn: 4, gridRow: 1, display: 'flex', flexShrink: 0 }}>
        <CountBadge inline tone="muted" value={count} />
      </span>
      {/* Строка 2: мини-рельса + статус (span columns 3-5). Компактная — без рельсы */}
      <span style={{
        gridColumn: '3 / 5', gridRow: 2, display: 'flex', alignItems: 'center',
        gap: 7, minWidth: 0,
      }}>
        {!compact && <FeatureRail rail={feature.rail} closed={feature.bucket === 'closed'} />}
        <span
          data-testid="feature-group-status"
          style={{
            fontSize: FS.xs, color: compact ? C.textMuted : C.textSecondary, whiteSpace: 'nowrap',
            overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0,
            fontFamily: FONT.sans,
          }}
        >
          {status}
        </span>
      </span>
    </button>
  );
}

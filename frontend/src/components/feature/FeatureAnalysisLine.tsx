// Строка анализа на вкладке «Статистика» (волна 5, W5-F1; спецификация features-overview-5.md §«Точка 6»).
// Первый ребёнок вкладки, до значков точности и Ганта: короб с маркером, фразой и действием.
// Состояние выбирает чистая функция analysisLineState — компонент только рисует.
// Без `run` (старые ответы) строки нет; пустых состояний у строки не бывает — худшее — other.

import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { Badge } from '../ui/Badge';
import { Dot } from '../ui/Dot';
import { Button } from '../ui/Button';
import { ICON_SIZE, ICON_PROPS } from '../ui/icons';
import { Bell, Check, Clock, Moon, Sparkles } from 'lucide-react';
import { openChatById } from '../../lib/openChat';
import type { NavMarks } from '../../lib/featureNavMarks';
import type { FeatureRun } from '../../types/featureRun';
import { analysisLineState, type AnalysisBadge, type AnalysisLineState } from './FeatureAnalysisModel';

const BADGE_ICON: Record<AnalysisBadge['icon'], typeof Clock> = {
  Clock, Bell, Moon, Check,
};

// Тон короба: фон и рамка по таблице спецификации (quiet — пунктир warning).
function boxStyle(st: AnalysisLineState, isMobile: boolean) {
  const bg = st.tone === 'danger' ? C.dangerBg : st.tone === 'warning' ? C.warningBg : C.bgCard;
  const border = st.dashed
    ? `1px dashed ${C.warning}`
    : st.tone === 'danger' ? `1px solid ${C.dangerBorder}`
    : st.tone === 'warning' ? `1px solid ${C.warning}`
    : `1px solid ${C.borderLight}`;
  return {
    display: 'flex',
    alignItems: isMobile ? 'flex-start' : 'center',
    gap: SP.sm,
    minHeight: 36,
    padding: isMobile ? `${SP.sm}px ${SP.sm + 2}px` : `${SP.xs + 2}px ${SP.sm + 2}px`,
    borderRadius: R.lg,
    fontSize: FS.sm,
    marginBottom: SP.md,
    background: bg,
    border,
    fontFamily: FONT.sans,
    ...(isMobile ? { flexWrap: 'wrap' as const } : {}),
  };
}

export interface FeatureAnalysisLineProps {
  run: FeatureRun;
  // Пометки страницы: чат в архиве — кнопки «Открыть чат» нет (фраза остаётся).
  marks?: NavMarks;
  isMobile?: boolean;
}

export function FeatureAnalysisLine({ run, marks, isMobile = false }: FeatureAnalysisLineProps) {
  const st = analysisLineState(run, Date.now());

  // Чат шага в архиве или пуст — действие «Открыть чат» снимается, «Анализ моделью» остаётся.
  const actions = st.actions.filter(
    a => a.kind === 'analysis' || (a.sessionId && !marks?.chatArchived(a.sessionId)),
  );

  const open = (sessionId: string) => { void openChatById(sessionId); };
  // Компонент иконки бейджа — до JSX: тег не принимает computed-индекс.
  const BadgeIcon = st.badge ? BADGE_ICON[st.badge.icon] : null;

  return (
    <div data-testid="feature-analysis-line" style={boxStyle(st, isMobile)}>
      {st.badge && BadgeIcon ? (
        <Badge tone={st.badge.tone} size="xs" icon={<BadgeIcon size={ICON_SIZE.xs} {...ICON_PROPS} />}>
          {st.badge.text}
        </Badge>
      ) : (
        <span style={{ display: 'inline-block', boxShadow: st.dot?.glow ? SHADOW.focus : undefined }}>
          <Dot color={st.dot!.color} size={7} />
        </span>
      )}

      <span style={{ flex: 1, minWidth: 0, color: C.textPrimary }}>
        {st.strong && <b style={{ color: C.textHeading }}>{st.strong}</b>}
        // На 360 хвост «Последнее событие …» у «движется» не пишем (в макете он только на 1280).
        {st.tail && !(isMobile && st.key === 'moving') && (
          <span style={{ color: C.textMuted }}> {st.tail}</span>
        )}
      </span>

      <div style={{ display: 'flex', gap: SP.xs + 2, ...(isMobile ? { flexBasis: '100%', flexDirection: 'column' as const } : {}) }}>
        {actions.map((a, i) => (
          a.kind === 'analysis' ? (
            <Button
              variant="ghost" size={isMobile ? 'md' : 'sm'} fullWidth={isMobile}
              leftIcon={<Sparkles size={ICON_SIZE.xs} {...ICON_PROPS} />}
              dataTestId="analysis-line-summary"
            >
              {a.label}
            </Button>
          ) : (
            <Button
              variant={a.variant} size={isMobile ? 'md' : 'sm'} fullWidth={isMobile}
              onClick={() => open(a.sessionId!)}
              dataTestId={`analysis-line-open-chat-${i}`}
            >
              {a.label}
            </Button>
          )
        ))}
      </div>
    </div>
  );
}

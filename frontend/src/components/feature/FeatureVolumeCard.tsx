// Карточка «Объём» страницы фичи (FS-FV): «N из M шагов приняты», полоса по корзинам
// трекера и честность плана. Презентационный компонент: все числа — из `stepProgress`
// бэка как есть, фронт ничего не досчитывает (процентов нет). Встраивание — шаг FS-FS.

import type { CSSProperties } from 'react';
import { Button } from '../ui/Button';
import { C, FONT, FS, R, SP, TB } from '../../lib/design';
import { plural } from '../../lib/plural';
import type { StepProgress } from '../../lib/featureProgress';

export interface FeatureVolumeCardProps {
  progress: StepProgress;
  isMobile: boolean;
  // Задан — рядом с полосой ссылка «из чего N ›» (вкладка «Шаги»).
  onOpenSteps?: () => void;
}

// Корзины полосы в порядке слева направо: принято → ревью → работа → не заведено.
const SEGMENTS: { key: 'accepted' | 'review' | 'work' | 'notCreated'; color: string }[] = [
  { key: 'accepted', color: C.success },
  { key: 'review', color: C.info },
  { key: 'work', color: C.accent },
  { key: 'notCreated', color: C.progressTrack },
];

const CARD: CSSProperties = {
  background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
  padding: SP.lg, display: 'flex', flexDirection: 'column', gap: SP.sm, boxSizing: 'border-box', alignSelf: 'stretch',
};

export function FeatureVolumeCard({ progress, isMobile, onOpenSteps }: FeatureVolumeCardProps) {
  const { total, accepted, review, work, notCreated, planState, baselineTotal, followUpTotal, followUpAccepted } = progress;
  // «Разрезан» — cut или done; pending/partial — знаменатель может вырасти.
  const planUncut = planState === 'pending' || planState === 'partial';
  const inFlight = work + review;
  // «план расширен» (FS-FVp): знаменатель вырос после фиксации базовой оценки нарезанного
  // плана (бэк: stepProgress.baselineTotal) — объясняет падение процента. baselineTotal
  // null/undefined (не зафиксирована, старый ответ) или total == baselineTotal (план не рос)
  // — строки нет. Проценты и корзин не пересчитываются: они уже по total.
  const planGrew = baselineTotal != null && total > baselineTotal;
  // Доработки после мержа (FS-PB2, бэк: followUpTotal/followUpAccepted): вне total и
  // корзин, отдельной строкой. 0 или отсутствие поля — строки нет.
  const hasFollowUps = (followUpTotal ?? 0) > 0;

  const tailParts: string[] = [];
  if (inFlight > 0) tailParts.push(`${inFlight} в работе`);
  if (notCreated > 0) tailParts.push(`${notCreated} ${plural(notCreated, 'не заведён', 'не заведено', 'не заведено')}`);

  return (
    <div data-testid="volume-card" style={CARD}>
      {total > 0 ? (
        <>
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 6, flexWrap: 'wrap' }}>
            <span data-testid="volume-count" style={{
              fontFamily: FONT.sans, fontWeight: 600, fontSize: isMobile ? FS.xl : FS.h2, color: C.textHeading,
            }}>{`${accepted} из ${total}`}</span>
            <span style={{ fontSize: FS.md, color: C.textSecondary }}>шагов приняты</span>
          </div>
          <div data-testid="volume-bar" style={{ display: 'flex', gap: 2, height: 8, borderRadius: R.sm, overflow: 'hidden' }}>
            {SEGMENTS.filter(s => progress[s.key] > 0).map(s => (
              <div
                key={s.key}
                data-testid="volume-seg"
                data-seg={s.key}
                style={{ flex: `${progress[s.key]} 1 0`, minWidth: 0, background: s.color }}
              />
            ))}
          </div>
          {planGrew && (
            <div data-testid="volume-plan-grew" style={{ fontSize: FS.sm, color: C.textMuted }}>
              {`план расширен: было ${baselineTotal}, стало ${total}`}
            </div>
          )}
          {hasFollowUps && (
            <div data-testid="volume-followups" style={{ fontSize: FS.sm, color: C.textMuted }}>
              {`Доработки: ${followUpAccepted ?? 0} из ${followUpTotal}`}
            </div>
          )}
          {tailParts.length > 0 && (
            <div data-testid="volume-caption" style={{ fontSize: FS.sm, color: C.textSecondary }}>
              {`ещё ${tailParts.join(', ')}`}
            </div>
          )}
        </>
      ) : (
        <div data-testid="volume-empty" style={{ fontSize: FS.md, color: C.textSecondary }}>шагов пока нет</div>
      )}
      {planUncut && (
        <div data-testid="volume-plan-warning" style={{ fontSize: FS.sm, color: C.warningText }}>
          план разрезан не весь — шагов может стать больше
        </div>
      )}
      {onOpenSteps && total > 0 && (
        <div>
          <Button
            variant="ghost"
            size="sm"
            dataTestId="volume-open-steps"
            onClick={onOpenSteps}
            style={{
              border: 'none', color: C.accent, padding: isMobile ? '11px 8px' : '4px 8px',
              minHeight: isMobile ? TB.iconHitMobile : undefined, display: 'inline-flex',
            }}
          >
            {`из чего ${total} ›`}
          </Button>
        </div>
      )}
    </div>
  );
}

// «Сейчас работают» — блок под плитками «Пульса» (волна 5, ST-F6; макет
// feature-stats-5.html §.a-now): строка на каждый элемент run.now — аватар
// исполнителя, stepRef, «идёт N мин» от startedAt, переход в чат.
// Ничего не пересчитываем: минуты — только из minutesSince (FeatureStatsBits).
// run.now пуст или фича merged/closed — блока нет (компонент возвращает null,
// как в макете: `D.now && D.now.length && D.key !== 'merged'`).
// Токены — только C.*/FS/SP/R; тач-цель переходов на 360 (44) даёт NavTargetButton.

import { type CSSProperties } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { usePersonas } from '../../lib/personas';
import { openChatById } from '../../lib/openChat';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { EMPTY_NAV_MARKS, type FeatureNavCtx } from '../../lib/featureNavMarks';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { minutesSince, formatDuration } from './FeatureStatsBits';
import { NavTargetButton } from './NavTargetButton';
import type { FeatureNow, FeatureRun } from '../../types/featureRun';

export interface FeatureStatsNowProps {
  run: FeatureRun;
  isMobile?: boolean;
  // Контекст возврата и пометки («в архиве»); без него — без записи точки и без пометок.
  nav?: FeatureNavCtx;
  // Фиксированное «сейчас» (тесты); в работе — Date.now().
  nowMs?: number;
}

// Фича закончена: состояние merged/closed или флаг closed. Идущих нет — блока нет.
function isFinished(run: FeatureRun): boolean {
  return run.state === 'merged' || run.state === 'closed' || run.closed === true;
}

// Подпись секции: FS.xxs, UPPERCASE, textMuted (макет .sec, как в StatsHero).
const SEC_LABEL: CSSProperties = {
  fontSize: FS.xxs, textTransform: 'uppercase', letterSpacing: 0.6,
  color: C.textMuted, fontFamily: FONT.sans, marginBottom: SP.xs,
};

// Строка: bgCard, рамка borderLight, R.lg (макет .a-now .it); на 360 — во всю
// ширину (.w360 .a-now .it { width:100% }).
function rowStyle(isMobile: boolean): CSSProperties {
  return {
    display: 'flex', alignItems: 'center', gap: SP.sm,
    padding: `${SP.xs}px ${SP.xs}px ${SP.xs}px ${SP.sm}px`,
    border: `1px solid ${C.borderLight}`,
    borderRadius: R.lg,
    background: C.bgCard,
    ...(isMobile ? { width: '100%' } : {}),
  };
}

// Имя исполнителя: persona по id; нет персоны — честно «персона недоступна»
// (как в LongestNow).
function personName(personaId: string | null | undefined, personas: ReturnType<typeof usePersonas>): string {
  if (!personaId) return '—';
  return personas.find(p => p.id === personaId)?.name ?? 'персона недоступна';
}

// «идёт N мин» от startedAt; задача не запускалась — говорим прямо, без выдуманных минут.
function runningText(startedAt: string | null | undefined, nowMs: number): string {
  const mins = minutesSince(startedAt, nowMs);
  if (mins == null) return 'не запущена';
  return `идёт ${formatDuration(mins)}`;
}

export function FeatureStatsNow({ run, isMobile = false, nav, nowMs }: FeatureStatsNowProps) {
  const marks = nav?.marks ?? EMPTY_NAV_MARKS;
  // Хук выше guard: ранний return не должен обрывать вызов хука
  // (react-hooks/rules-of-hooks), как в прежнем LongestNow.
  // Правило «идёт» теперь на бэке (run.now сужён по IsHeld, коммит feea567e3),
  // клиентский фильтр здесь не нужен.
  const personas = usePersonas();
  const now = run.now ?? [];
  if (now.length === 0 || isFinished(run)) return null;

  const at = nowMs ?? Date.now();

  return (
    <div data-testid="stats-now">
      <div style={SEC_LABEL}>Сейчас работают</div>
      <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
        {now.map((n: FeatureNow) => {
          const persona = n.personaId ? personas.find(p => p.id === n.personaId) : undefined;
          return (
            <div key={n.taskId} data-testid={`now-row-${n.taskId}`} style={rowStyle(isMobile)}>
              {persona
                ? <PersonaAvatar persona={persona} size={18} />
                : (
                  <span
                    aria-hidden
                    style={{
                      width: 18, height: 18, borderRadius: R.full, background: C.bgSelected,
                      flexShrink: 0, display: 'inline-flex', alignItems: 'center',
                      justifyContent: 'center', fontSize: FS.xxs, color: C.textMuted, fontFamily: FONT.sans,
                    }}
                  >?</span>
                )}
              <span style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: SP.xs, fontSize: FS.sm, fontFamily: FONT.sans }}>
                <b style={{ color: C.textHeading, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {personName(n.personaId, personas)}
                </b>
                {n.stepRef && (
                  <span style={{ color: C.textSecondary }}>шаг {n.stepRef}</span>
                )}
                <span data-testid={`now-time-${n.taskId}`} style={{ color: C.textMuted, fontFamily: FONT.mono, fontSize: FS.xs }}>
                  {runningText(n.startedAt, at)}
                </span>
              </span>
              <NavTargetButton
                kind="chat"
                chatId={n.linkedSessionId ?? null}
                chatArchived={marks.chatArchived(n.linkedSessionId)}
                title={`Открыть чат исполнителя`}
                isMobile={isMobile}
                onOpen={() => {
                  if (!n.linkedSessionId) return;
                  if (nav?.featureId) recordFeatureReturn(nav.featureId, { kind: 'chat', id: n.linkedSessionId }, nav.featureTitle);
                  void openChatById(n.linkedSessionId);
                }}
              />
            </div>
          );
        })}
      </div>
    </div>
  );
}

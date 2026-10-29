import { C, FS, R, SP, FONT } from '../../../lib/design';
import { openChatById } from '../../../lib/openChat';
import { getPersonaById } from '../../../lib/personas';
import { Badge } from '../../ui/Badge';
import { Button } from '../../ui/Button';
import { FeatureTrackerBar, trackerBarAria } from './FeatureTrackerBar';
import { FeatureTruthFooter } from './FeatureTruthBody';
import {
  alarmView, chatRoleFromRun, featureEstimateView, featureStepChainRows, featureTruthBar,
  stepBadgeTone, stepStatusText, stepStoppedBy, type NameOf,
} from './featureTruthModel';
import { laneWord } from '../../../lib/roleLabel';
import type { FeatureRun, FeatureStepChain } from '../../../types/featureRun';

// Тело режима «Исполнение фичи» для чата ШАГА — П2 «Мой шаг» (утверждено макетом
// feature-chats-panel-plan-popup-v4 §П2). Разводит роли поверхностей: шапка В2 — фича,
// плашка С1 — сравнение фич, пилюля и этот попап — ШАГ ЭТОГО ЧАТА. Ничего не дублируется:
// фича целиком отсюда не видна, только одной строкой с полосой.
//
// Секции (макет .pop .bd): тревога с действием (если есть) → «Этот чат» (карточка
// шага: название + плашка статуса + исполнитель/линия) → «Цепочка шага» (сам шаг,
// его витки, ревью, которое ждёт этот шаг) → «Фича» (одна строка + полоса) → подвал.
//
// Аккордеона этапов нет (он был в FeatureRunView): этапы — на странице фичи
// («Хронология»), туда ведёт подвал.
//
// Данные — только run и его цепочки (бэк знает Fact'ы цепочек); слова статуса —
// бэковые (chain.status), тон плашки — из stepBadgeTone. Фронт ничего не пересчитывает.

const nameOfPersona: NameOf = (id) => getPersonaById(id)?.name ?? null;

const secStyle: React.CSSProperties = {
  fontSize: FS.xs, fontWeight: 700, color: C.textMuted,
  textTransform: 'uppercase', letterSpacing: 0.4, margin: '10px 0 4px',
};
const rowStyle: React.CSSProperties = {
  display: 'flex', justifyContent: 'space-between', gap: SP.md, padding: '2px 0',
  alignItems: 'center', fontSize: FS.sm, color: C.textSecondary,
};

export function FeatureStepView({ run, chain, sessionId, chatTaskId, isMobile, onOpenPage, onClose }: {
  run: FeatureRun;
  // Цепочка шага этого чата (задача-владелец чата в её taskIds). null — шага нет:
  // вызывающий (FeatureModeBody) тогда рисует сводку (FeatureTruthBody), не это тело.
  chain: FeatureStepChain;
  sessionId: string | null;
  // Задача-владелец чата (Session.TaskId): по ней роль «этот чат — …» в подвале.
  chatTaskId?: string | null;
  isMobile: boolean;
  onOpenPage: () => void;
  // Тревога в ЭТОМ чате: закрыть попап (ответ пишут здесь). Вне — переход в чат тревоги.
  onClose: () => void;
}) {
  const nowMs = Date.now();
  const alarm = run.attention && (run.state === 'waiting' || run.state === 'merge_pending' || run.state === 'stopped')
    ? (() => {
      const who = run.attention.personaId ? nameOfPersona(run.attention.personaId) : null;
      return { view: alarmView(run.state, run.attention.reason ?? null, who, isMobile), chatId: run.attention.linkedSessionId ?? null };
    })()
    : null;

  // Тревога фичи относится к этому шагу? (бэковое поле, нового состояния не заводим)
  const stoppedBy = stepStoppedBy(run, chain);
  const rows = featureStepChainRows(run, chain, nameOfPersona);
  const sp = run.stepProgress ?? null;
  const bar = featureTruthBar(sp, sp?.inMaster);
  const est = featureEstimateView(run, nowMs);
  const executorName = chain.personaId ? nameOfPersona(chain.personaId) : null;
  const lane = laneWord(chain.lane ?? null);
  // Роль в подвале — по задаче чата (roleLabel); без задачи — по цепочке (она найдена по ней).
  const roleText = chatRoleFromRun(run, chatTaskId) || `${chain.stepRef}${lane ? ` · ${lane}` : ''}`;

  const onAlarmAction = () => {
    if (!alarm) return;
    if (alarm.chatId && alarm.chatId !== sessionId) { void openChatById(alarm.chatId); return; }
    onClose();
  };

  return (
    <div data-testid="feature-step-view" data-step-ref={chain.stepRef}>
      {alarm && (
        <div
          data-testid="feature-step-alarm"
          style={{
            display: 'flex', alignItems: 'center', gap: SP.sm, padding: `${SP.sm}px 10px`, borderRadius: R.md, fontSize: FS.sm, margin: '2px 0 6px',
            ...(alarm.view.tone === 'warn' ? { background: C.warningBg, color: C.warningText } : { background: C.dangerBg, color: C.dangerText }),
          }}
        >
          <span style={{ flex: 1, minWidth: 0 }}>{alarm.view.text}</span>
          <Button variant="primary" size="xs" onClick={onAlarmAction} style={isMobile ? { height: 32, minHeight: 32 } : undefined}>
            {alarm.view.actionLabel}
          </Button>
        </div>
      )}

      {/* «Этот чат»: карточка шага этого чата. */}
      <div style={{ ...secStyle, margin: '2px 0 4px' }}>Этот чат</div>
      <div data-testid="feature-step-card" style={{ padding: '8px 10px', borderRadius: R.lg, background: C.bgInset }}>
        <div style={{ display: 'flex', gap: SP.sm, alignItems: 'center', minWidth: 0 }}>
          <span data-testid="feature-step-title" style={{ flex: 1, minWidth: 0, fontSize: FS.base, fontWeight: 700, color: C.textHeading, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {chain.stepRef} · {chain.title}
          </span>
          <span data-testid="feature-step-status" style={{ flexShrink: 0 }}>
            {/* Тревога на ЭТОМ шаге (attention.taskId ∈ цепочки) — danger «стоит · N»
                вместо «идёт»: карточка не спорит с тревогой выше (дизайн-ревью F2, major 3). */}
            <Badge tone={stepBadgeTone(chain, stoppedBy)} size="xs">
              {stepStatusText(chain, nowMs, stoppedBy)}
            </Badge>
          </span>
        </div>
        <div style={{ fontSize: FS.xs, color: C.textMuted, marginTop: 2 }}>
          {[executorName, lane ? `линия ${lane}` : null].filter(Boolean).join(' · ')}
        </div>
      </div>

      {/* «Цепочка шага»: сам шаг, его витки, ревью, ждущее этот шаг. Точки — цвета корзин. */}
      <div style={secStyle}>Цепочка шага</div>
      <div data-testid="feature-step-chain">
        {rows.map(r => (
          <div key={r.id} data-testid="feature-step-chain-row" style={rowStyle}>
            <span style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0, overflow: 'hidden' }}>
              <i style={{ width: 7, height: 7, borderRadius: '50%', flexShrink: 0, display: 'inline-block', background: r.color }} />
              <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{r.text}</span>
            </span>
            {r.time && <span style={{ fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 600, color: C.textMuted, whiteSpace: 'nowrap', flexShrink: 0 }}>{r.time}</span>}
          </div>
        ))}
      </div>

      {/* «Фича»: одна строка (название + % · срок) и полоса. Фича целиком — в шапке и на странице. */}
      <div style={secStyle}>Фича</div>
      <div data-testid="feature-step-feature">
        <div style={{ display: 'flex', justifyContent: 'space-between', gap: SP.md, alignItems: 'baseline', fontSize: FS.base, color: C.textSecondary, minWidth: 0 }}>
          <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            <b style={{ color: C.textHeading, fontWeight: 600 }}>{run.epic?.title ?? 'фича'}</b>
          </span>
          {bar && (
            <span data-testid="feature-step-feature-num" style={{ fontFamily: FONT.mono, fontSize: FS.sm, fontWeight: 700, color: C.textHeading, whiteSpace: 'nowrap', flexShrink: 0 }}>
              {bar.percent} %{est?.rowEta ? ` · ${est.rowEta}` : ''}
            </span>
          )}
        </div>
        {bar && <FeatureTrackerBar bar={bar} ariaLabel={trackerBarAria(bar)} style={{ width: '100%', marginTop: 4 }} testId="feature-step-bar" />}
      </div>

      <FeatureTruthFooter roleText={roleText} isMobile={isMobile} onOpenPage={onOpenPage} />
    </div>
  );
}

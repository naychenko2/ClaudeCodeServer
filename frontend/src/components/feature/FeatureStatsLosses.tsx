// «Главные потери» — блок вкладки «Пульс» (волна 5, ST-F7; вариант A макета
// feature-stats-5.html, перенос §C в §A по записке Майи): три карточки — ожидание,
// витки, дольше всех идущее. Числа — из lossesModel (ST-F3), здесь только рендер:
// ничего не пересчитываем. Перехода в чат из карточки 1 пока нет: бэк не отдаёт
// longestWait.taskId (ST-B2), поэтому только текст. Клик по карточке 2 ведёт в
// «Хронологию» на этап шага — тот же путь, что коды шагов в строке анализа
// (FeatureAnalysisSummary). Нечего показать — пунктирная заметка.
// Токены — только C.*/FS/SP/R; диск номера и значки витков — по шкале ICON_SIZE.

import { type CSSProperties } from 'react';
import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { ICON_SIZE } from '../ui/icons';
import { plural } from '../../lib/plural';
import { formatDuration } from './FeatureStatsBits';
import { lossesModel, type LossCard, type RoundMark } from './FeatureStatsPulseModel';
import { setFeaturePageTab, focusFeatureStage } from '../../lib/featurePage';
import { usePersonas } from '../../lib/personas';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { EMPTY_NAV_MARKS, type FeatureNavCtx } from '../../lib/featureNavMarks';
import { NavTargetPair } from './NavTargetButton';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';

export interface FeatureStatsLossesProps {
  run: FeatureRun;
  stats: FeatureStats;
  isMobile?: boolean;
  // Контекст возврата и пометки для переходов «чат» / «задача» карточки «Дольше всех сейчас».
  nav?: FeatureNavCtx;
  // Фиксированное «сейчас» (тесты); в работе — Date.now().
  nowMs?: number;
}

// Подпись секции: FS.xxs, UPPERCASE, textMuted (макет .sec, как в StatsHero).
const SEC_LABEL: CSSProperties = {
  fontSize: FS.xxs, textTransform: 'uppercase', letterSpacing: 0.6,
  color: C.textMuted, fontFamily: FONT.sans, marginBottom: SP.xs,
};

// Сетка: три в ряд, на 360 — одна колонка (макет .c-loss и .w360 .c-loss).
function gridStyle(isMobile: boolean): CSSProperties {
  return {
    display: 'grid',
    gridTemplateColumns: isMobile ? '1fr' : 'repeat(3, 1fr)',
    gap: isMobile ? SP.sm : SP.md,
  };
}

// Карточка: как плитки — bgCard, borderLight, SHADOW.card, R.lg. Живая карточка 1 —
// infoBg/info (макет .c-card.live, тот же тон, что герой в «ждёт вас»): фон C.infoBg,
// рамка C.info. На 360 поля плотнее.
function cardStyle(live: boolean, isMobile: boolean): CSSProperties {
  return {
    display: 'flex', flexDirection: 'column', gap: SP.xs,
    background: live ? C.infoBg : C.bgCard,
    border: `1px solid ${live ? C.info : C.borderLight}`,
    borderRadius: R.lg,
    boxShadow: SHADOW.card,
    padding: isMobile ? `${SP.md}px` : `${SP.lg}px`,
  };
}

// Сброс UA-стилей для карточки-ссылки (2): `<button>` по умолчанию центрирует текст
// и даёт cursor: default — без этого числа и контекст стоят по центру, у карточек 1
// и 3 — слева. Тот же приём, что в строках этапов (FeatureRunView) и чипах перехода.
const LINK_CARD_EXTRA: CSSProperties = { textAlign: 'left', cursor: 'pointer' };

// Пунктирная заметка «нечего показать» — стиль героевой «нет данных» (макет .dashed).
const DASH_NOTE: CSSProperties = {
  border: `1px dashed ${C.dashed}`, borderRadius: R.lg, padding: SP.md,
  color: C.textMuted, fontSize: FS.sm, fontFamily: FONT.sans, lineHeight: 1.5,
};

// Строка «номер · заголовок»: FS.sm, 600, textSecondary (макет .c-card .rk). Номер —
// диск 20 px на bgSelected: размер берётся из шкалы (ICON_SIZE.lg), а не литералом.
const RK: CSSProperties = {
  display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: `${SP.xs}px ${SP.sm}px`,
  fontSize: FS.sm, fontWeight: 600, color: C.textSecondary, fontFamily: FONT.sans,
};
const NO: CSSProperties = {
  width: ICON_SIZE.lg, height: ICON_SIZE.lg, borderRadius: R.full,
  background: C.bgSelected, color: C.textHeading,
  display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
  fontSize: FS.xs, flexShrink: 0,
};

// Число карточки: FONT.serif, FS.h2 (на 360 — FS.xl, как в плитках), 700 —
// макет .c-card .n (22 px на десктопе).
function bigStyle(isMobile: boolean): CSSProperties {
  return {
    fontFamily: FONT.serif, fontSize: isMobile ? FS.xl : FS.h2, fontWeight: 700,
    color: C.textHeading, lineHeight: 1.2, marginTop: SP.xs,
  };
}

// Контекст: FS.sm, textSecondary (макет .ctx).
const CTX: CSSProperties = { fontSize: FS.sm, color: C.textSecondary, fontFamily: FONT.sans };

// Значок витка ✓/↩: 14 px = ICON_SIZE.xs (макет .a-rounds i), цвета — как в плитке
// «Возвраты»: successBg/successText, dangerBg/dangerText.
const MARK: CSSProperties = {
  width: ICON_SIZE.xs, height: ICON_SIZE.xs, borderRadius: R.sm,
  display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
  fontSize: FS.xxs, fontWeight: 700, fontStyle: 'normal',
};

// Подсказка на карточке 2: кликабельна вся карточка, подсказка лишь указывает цель
// (макет .ft «Цепочка шага →»).
const ACTION: CSSProperties = {
  marginTop: SP.sm, fontSize: FS.xs, fontWeight: 600, color: C.accent, fontFamily: FONT.sans,
};

// Клик по карточке 2 — в «Хронологию» на этап шага: цепочка по stepRef, её первая
// задача, стадия, которая её содержит (тот же путь, что коды шагов в строке анализа,
// FeatureAnalysisSummary).
function focusStep(run: FeatureRun, stepRef: string) {
  setFeaturePageTab('timeline');
  const chain = (run.stepChains ?? []).find(c => c.stepRef === stepRef);
  const taskId = chain?.taskIds?.[0];
  if (!taskId) return;
  const stage = (run.stages ?? []).find(s => (s.taskIds ?? []).includes(taskId));
  if (stage) focusFeatureStage(stage.key);
}

// Имя исполнителя карточки 3: персона по id; нет персоны — честно «персона
// недоступна» (как в «Сейчас работают»).
function personName(personaId: string | null, personas: ReturnType<typeof usePersonas>): string {
  if (!personaId) return '—';
  return personas.find(p => p.id === personaId)?.name ?? 'персона недоступна';
}

// Доля — проценты, не 0.42 (тот же формат, что percentText в compositionRows).
function shareText(share: number | null): string | null {
  if (share == null) return null;
  return `${Math.round(share * 100)} %`;
}

// Что внутри карточки: число и контекст. Формулировки — по макету §C.
function cardText(card: LossCard, personas: ReturnType<typeof usePersonas>): { big: string; ctx: string | null } {
  switch (card.kind) {
    case 'wait': {
      const ctx = card.count == null
        ? 'ожиданий не считалось'
        : card.count > 0 ? `всего ожиданий: ${card.count}` : 'ожиданий не было';
      return { big: card.minutes == null ? '—' : formatDuration(card.minutes), ctx };
    }
    case 'loops': {
      return {
        big: `${card.stepRef} · ${card.loops} ${plural(card.loops, 'возврат', 'возврата', 'возвратов')}`,
        ctx: `${card.stepTitle} · шёл ${formatDuration(card.durationMinutes)}`,
      };
    }
    case 'longest': {
      const share = shareText(card.share);
      return {
        big: `${card.stepRef ? `${card.stepRef} · ` : ''}${formatDuration(card.minutes)}`,
        ctx: `${personName(card.personaId, personas)}${share ? ` · ${share} расхода фичи` : ' · доля расхода не считалась'}`,
      };
    }
    case 'stage': {
      return { big: `${card.stageLabel} · ${formatDuration(card.minutes)}`, ctx: null };
    }
  }
}

export function FeatureStatsLosses({ run, stats, isMobile = false, nav, nowMs }: FeatureStatsLossesProps) {
  const marks = nav?.marks ?? EMPTY_NAV_MARKS;
  const ln = stats.longestNow;
  // Хук выше любого раннего return (замечание Глеба по ST-F6): guards здесь нет —
  // пустое состояние рисуется пунктиром, а не null.
  const personas = usePersonas();
  const model = lossesModel(run, stats, nowMs ?? Date.now());

  return (
    <div data-testid="stats-losses">
      <div style={SEC_LABEL}>Главные потери</div>
      {model.empty ? (
        <div data-testid="losses-empty" style={DASH_NOTE}>
          Потери появятся с первым ожиданием, возвратом или ошибкой.
        </div>
      ) : (
        <div style={gridStyle(isMobile)}>
          {model.cards.map((card) => {
            const { big, ctx } = cardText(card, personas);
            const body = (
              <>
                <div style={RK}>
                  <span aria-hidden style={NO}>{card.no}</span>
                  {card.title}
                </div>
                <div data-testid={`loss-big-${card.no}`} style={bigStyle(isMobile)}>{big}</div>
                {ctx && <div data-testid={`loss-ctx-${card.no}`} style={CTX}>{ctx}</div>}

                {/* Значки витков на карточке 2: на каждый учтённый ход. */}
                {card.kind === 'loops' && card.rounds.length > 0 && (
                  <div
                    data-testid="loss-rounds"
                    style={{ display: 'flex', gap: SP.xs, marginTop: SP.xs, flexWrap: 'wrap' }}
                  >
                    {card.rounds.map((mark: RoundMark, i: number) => (
                      <i
                        key={i}
                        aria-hidden
                        data-testid="loss-round"
                        style={{
                          ...MARK,
                          background: mark === 'ok' ? C.successBg : C.dangerBg,
                          color: mark === 'ok' ? C.successText : C.dangerText,
                        }}
                      >
                        {mark === 'ok' ? '✓' : '↩'}
                      </i>
                    ))}
                  </div>
                )}
              </>
            );

            // Карточка 2 кликабельна: ведёт в «Хронологию» на цепочку шага. Остальные
            // — без перехода: у карточки 1 цели нет, пока бэк не отдаёт longestWait.taskId.
            if (card.kind === 'loops') {
              return (
                <button
                  key={`${card.no}-${card.kind}`}
                  type="button"
                  data-testid={`loss-card-${card.no}`}
                  aria-label={`Открыть цепочку шага ${card.stepRef}`}
                  onClick={() => focusStep(run, card.stepRef)}
                  style={{ ...cardStyle(false, isMobile), ...LINK_CARD_EXTRA }}
                >
                  {body}
                  <span aria-hidden style={ACTION}>Цепочка шага →</span>
                </button>
              );
            }
            return (
              <div
                key={`${card.no}-${card.kind}`}
                data-testid={`loss-card-${card.no}`}
                style={cardStyle(card.kind === 'wait' && card.live, isMobile)}
              >
                {body}
                {/* Переходы «чат» и «задача» — как у прежнего LongestNow (карточка 3 «сейчас»). */}
                {card.kind === 'longest' && ln && (
                  <div data-testid="loss-longest-nav" style={{ marginTop: SP.sm }}>
                    <NavTargetPair
                      chatId={ln.chatId}
                      chatArchived={marks.chatArchived(ln.chatId)}
                      onOpenChat={() => {
                        if (!ln.chatId) return;
                        if (nav?.featureId) recordFeatureReturn(nav.featureId, { kind: 'chat', id: ln.chatId }, nav.featureTitle);
                        void openChatById(ln.chatId);
                      }}
                      taskId={ln.taskId}
                      taskClosed={marks.taskClosed(ln.taskId)}
                      onOpenTask={() => {
                        if (nav?.featureId) recordFeatureReturn(nav.featureId, { kind: 'task', id: ln.taskId }, nav.featureTitle);
                        openTaskInSection({ id: ln.taskId });
                      }}
                      isMobile={isMobile}
                    />
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

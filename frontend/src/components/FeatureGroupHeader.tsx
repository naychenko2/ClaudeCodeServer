import { useState } from 'react';
import type { CSSProperties } from 'react';
import { ChevronRight } from 'lucide-react';
import { C, R, FS, FONT, SHADOW } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { FeatureGlyph } from './chat/feature/FeatureGlyphs';
import { FeatureTrackerBar, trackerBarAria } from './chat/feature/FeatureTrackerBar';
import { featureTruthBar } from './chat/feature/featureTruthModel';
import { PersonaAvatar } from '../features/personas/PersonaAvatar';
import { Button } from './ui/Button';
import { Badge } from './ui/Badge';
import { featureDisplayState, featureQuickAction, featureAvatarPersonaIds, formatDuration } from '../lib/chatGroups';
import { stepProgressFromIndex, formatStepProgress, trackerPercent } from '../lib/featureProgress';
import { dateOnly } from './chat/feature/featureTime';
import type { FeatureIndexFeature } from '../types/featureIndex';
import type { Persona } from '../types';

// Плашка группы фичи в списке чатов — С1 «Трек снизу» (утверждена макетом
// feature-chats-panel-plan-popup-v4): список чатов СРАВНИВАЕТ фичи, и длинная полоса
// трекера под каждой плашкой сразу показывает, какая ближе к концу.
//
// Строка 1: глиф 14, название, «давность» (mono, прячется при наведении).
// Строка 2: стопка аватаров «кто сейчас» · статус («Сейчас {Имя} +N» / тревога тоном) ·
//          справа «{pct} %» (mono; на десктопе — и срок, если он есть в данных).
// Под строкой 2 — полоса трекера во всю ширину (та же, что в шапке В2): пять корзин,
// «в master» первой. Счётчики «принято N из T · …» — в title (полная строка), бейдж
// «расхождений: N» остаётся видимым.
//
// Ушло: мини-рельса из 8 точек (FeatureRail) — этапы больше нигде в чатах не
// повторяются, они на странице фичи («Хронология»).
//
// Высота — ровно 55 px (поля 6/10/2/2 + строки 19/17 + зазор 4 + полоса 4; minHeight
// не нужен). Кромка тревоги 3px — тоном В2: «ждёт вас» — C.warning, «стоит» — C.danger.
// Компактная (закрытая фича, .closed): без рамки/заливки/аватаров/процента/полосы —
// второй план, как прежде.
// Мобила: срок не показывается, только процент (С1 §Для Киры).
//
// Данные — строка индекса (те же числа, что страница фичи): процент — общий
// trackerPercent, полосы — общий FeatureTrackerBar. «Срок» в индексе не несётся
// (оценка живёт в epic.note страницы фичи) — без данных срока нет (пустые части
// скрываются), это отмечено постановщику.

// Единый focus-visible ring (клавиатура) для шеврона и тела плашки (m1): нативное
// кольцо браузера в тёмной теме не видно, поэтому — SHADOW.focus через :focus-visible
// (паттерн FeaturePageHeader/SessionList: один <style>, outline:none + box-shadow).
const FOCUS_CLASS = 'cc-feature-group-header-focus';
if (typeof document !== 'undefined' && !document.getElementById('cc-feature-group-header-focus-style')) {
  const el = document.createElement('style');
  el.id = 'cc-feature-group-header-focus-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  // head ?? body: fake-DOM тестов (vi.hoisted) не всегда несёт head — не роняем импорт.
  (document.head ?? document.body).appendChild(el);
}

// Клик шеврона: stopPropagation (всплытие не доходит до обёртки/родителя) + onToggle.
// Шеврон и тело — СОСЕДНИЕ кнопки (не вложенные), поэтому клик по шеврону сам по себе
// onOpen (обработчик тела) не зовёт; stopPropagation дополнительно гасит всплытие.
// Чистая функция — тестируется без DOM (окружение node, без jsdom).
export function featureGroupChevronClick(onToggle: () => void, e: { stopPropagation: () => void }): void {
  e.stopPropagation();
  onToggle();
}

// «Давность» справа в строке 1 (макет .pr): сколько прошло с attention.since (если есть
// тревога) или с lastActivityAt. Компактный формат: «сейчас» / «N мин» / «N ч» / «dd.mm».
// Чистая функция: now — «сейчас» в мс (стабильные тесты; не задано — Date.now()).
// Местный пояс (getMonth/getDate, а не getUTC*) — как в featureTime.
function ageLabel(iso: string | null | undefined, now?: number): string {
  if (!iso) return '';
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return '';
  const n = now ?? Date.now();
  const diffMin = Math.max(0, Math.round((n - t) / 60000));
  if (diffMin < 1) return 'сейчас';
  if (diffMin < 60) return `${diffMin} мин`;
  const h = Math.floor(diffMin / 60);
  if (h < 24) return `${h} ч`;
  const d = new Date(t);
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const dd = String(d.getDate()).padStart(2, '0');
  return `${dd}.${mm}`;
}

export function FeatureGroupHeader({ feature, expanded, onToggle, onOpen, isMobile = false, compact = false, personaName, now, personaById, selected = false }: {
  feature: FeatureIndexFeature;
  // Группа раскрыта (шеvron повёрнут, под заголовком — все чаты).
  expanded: boolean;
  // Шеврон: сворачивает/разворачивает (отдельная тач-цель; stopPropagation).
  onToggle: () => void;
  // Тело: открывает страницу фичи.
  onOpen: () => void;
  isMobile?: boolean;
  // Закрытая фича «на втором плане» (макет .closed .fgh): без рамки/заливки/аватаров/полосы.
  compact?: boolean;
  // «Кто» в статусе: имя персоны (attention.personaId / now0.personaId, резолвит вызывающий).
  personaName?: string | null;
  // «Сейчас» в мс для «сколько» (стабильные тесты); не задано — Date.now().
  now?: number;
  // Резолвер id персоны → Persona (для стопки аватаров и имени в статусе); не задан — аватаров нет.
  personaById?: (id: string) => Persona | undefined;
  // Открыта страница фичи (рамка accentMuted, фон accentLight).
  selected?: boolean;
}) {
  const [hovered, setHovered] = useState(false);
  const st = featureDisplayState(feature, { now, personaName });
  // Честный прогресс (Ф-pr): из строки индекса; null — старые ответы (полосы и процента нет).
  const progress = stepProgressFromIndex(feature);
  const bar = featureTruthBar(progress, null);
  const percent = progress ? trackerPercent(progress.accepted, progress.total) : 0;
  // Кромка тревоги 3px — тоны В2: 'wait' → warning (жёлтый), 'stop' → danger (красный).
  // (Синий C.info был языком старой плашки; в языке фичи «нужен ответ» — warn.)
  const alarmEdge = st.alarm === 'wait' ? C.warning : st.alarm === 'stop' ? C.danger : null;
  // Закрытая фича: статус «смержена {дата} · {длительность}» (макет .closed) или «закрыта».
  const closedMerged = feature.bucket === 'closed' && feature.summary?.mergedAt != null;
  const closedStatus = compact && feature.bucket === 'closed'
    ? (closedMerged
      ? `смержена ${dateOnly(feature.summary!.mergedAt)} · ${formatDuration(feature.summary!.durationMinutes)}`
      : 'закрыта')
    : null;
  // «Сейчас {Имя} +N» — обычная работа (plain run): имя первого идущего, «+N» остальных.
  // merge_pending/merging/очередь/закрытая — прежние слова featureDisplayState (их
  // «Сейчас» не касается).
  const nowIds = featureAvatarPersonaIds(feature);
  const avatars = nowIds.map(id => personaById?.(id)).filter((p): p is Persona => p != null);
  const firstName = avatars[0]?.name ?? personaName ?? null;
  const plainRun = !compact && st.kind === 'run' && !st.alarm && feature.state !== 'merging' && firstName != null;
  const status = closedStatus ?? (plainRun
    ? 'Сейчас'
    : st.status);
  const statusRest = plainRun ? Math.max(0, nowIds.length - 1) : 0;
  // Очередь: цифры «N из M» набираем FONT.mono — при пересчёте не прыгает ширина.
  const queueNums = st.kind === 'queue' ? /^в очереди · (\d+ из \d+)$/.exec(status)?.[1] ?? null : null;
  // Тон статуса: «ждёт вас» — warningText 600, «стоит» — dangerText 600 (тоны В2),
  // «доработка» — warningText 600, закрытая смержена → successText, «Сейчас» — textSecondary
  // (имя — textHeading 600 внутри), прочее — textSecondary 400.
  const statusColor = compact
    ? (closedMerged ? C.successText : C.textSecondary)
    : feature.bucket === 'merged' ? C.successText
    : st.alarm === 'wait' ? C.warningText
    : st.alarm === 'stop' ? C.dangerText
    : st.kind === 'loop' ? C.warningText
    : st.kind === 'queue' ? C.textPrimary
    : C.textSecondary;
  const statusWeight = (st.alarm != null || st.kind === 'loop' || st.kind === 'queue' || (compact && closedMerged) || feature.bucket === 'merged' || plainRun) ? 600 : 400;
  // «Давность»: с тревоги (attention.since) или с последней активности (lastActivityAt).
  const age = compact ? '' : ageLabel(feature.attention?.since ?? feature.lastActivityAt, now);
  // Быстрое действие: «Ответить» / «К мержу» / «Ключевой чат» (null — нет).
  const quick = featureQuickAction(feature);
  // Быстрое действие рендерим только на десктопе (на 360 оно живёт на странице фичи).
  const showQuick = quick != null && !compact && !isMobile;

  // Рамка обёртки: longhand'ами (M1 волны 1 — шорткод + borderLeft:undefined React обнуляет).
  //   обычная  — 1px borderLight, bg bgCard;
  //   тревога  — 3px слева (warning/danger) + 1px остальные;
  //   выбрана   — рамка accentMuted, bg accentLight;
  //   закрытая  — рамка/фон прозрачные;
  //   наведение (без тревоги) — рамка C.border + SHADOW.card.
  const normalColor = compact ? 'transparent' : selected ? C.accentMuted : (hovered && !alarmEdge ? C.border : C.borderLight);
  const leftColor = alarmEdge ?? normalColor;
  const wrapperStyle: CSSProperties = {
    position: 'relative',
    display: 'flex',
    width: '100%',
    boxSizing: 'border-box',
    borderStyle: 'solid',
    borderTopWidth: 1, borderRightWidth: 1, borderBottomWidth: 1,
    borderLeftWidth: alarmEdge ? 3 : 1,
    borderTopColor: normalColor, borderRightColor: normalColor, borderBottomColor: normalColor,
    borderLeftColor: leftColor,
    background: compact ? 'transparent' : selected ? C.accentLight : C.bgCard,
    borderRadius: R.lg,
    overflow: 'hidden',
    boxShadow: hovered && !compact ? SHADOW.card : undefined,
  };
  // Поля тела (С1): обычная 6 10 2 2 + строки 19/17 + зазор 4 + полоса 4 = ровно 55 px;
  // закрытая (compact) — прежние 5 10 5 2 (без полосы).
  const bodyPadding = compact ? '5px 10px 5px 2px' : '6px 10px 2px 2px';

  return (
    <div
      data-testid="feature-group-header"
      data-feature-state={st.kind}
      style={wrapperStyle}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      onFocus={() => setHovered(true)}
      onBlur={() => setHovered(false)}
    >
      {/* Шеврон: отдельная тач-цель (26/40), stopPropagation, onOpen не зовёт. */}
      <button
        type="button"
        data-testid="feature-group-chevron"
        className={FOCUS_CLASS}
        aria-label={expanded ? 'Свернуть чаты фичи' : 'Развернуть чаты фичи'}
        aria-expanded={expanded}
        onClick={e => featureGroupChevronClick(onToggle, e)}
        onMouseEnter={e => { e.currentTarget.style.background = C.bgSelected; e.currentTarget.style.color = C.textPrimary; }}
        onMouseLeave={e => { e.currentTarget.style.background = 'transparent'; e.currentTarget.style.color = C.textMuted; }}
        style={{
          // Ловушки <button> (раздел 0): рамка/фон/шрифт/поля/выравнивание явно.
          border: 'none',
          background: 'transparent',
          color: C.textMuted,
          fontFamily: FONT.sans,
          padding: 0,
          margin: 0,
          cursor: 'pointer',
          width: isMobile ? 40 : 26,
          flexShrink: 0,
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
        }}
      >
        <ChevronRight
          size={ICON_SIZE.xs}
          strokeWidth={ICON_STROKE}
          style={{ transform: expanded ? 'rotate(90deg)' : undefined, transition: 'transform 0.15s' }}
        />
      </button>

      {/* Тело: открывает страницу фичи (onOpen). Две строки + полоса, gap 3. */}
      <button
        type="button"
        data-testid="feature-group-body"
        className={FOCUS_CLASS}
        aria-label={`Открыть страницу фичи «${feature.title}»`}
        onClick={onOpen}
        style={{
          // Ловушки <button> (раздел 0): рамка/фон/шрифт/поля/выравнивание явно.
          border: 'none',
          background: 'transparent',
          color: 'inherit',
          fontFamily: FONT.sans,
          padding: bodyPadding,
          margin: 0,
          cursor: 'pointer',
          textAlign: 'left',
          flex: 1,
          minWidth: 0,
          display: 'flex',
          flexDirection: 'column',
          gap: 3,
          ...(compact && isMobile ? { minHeight: 52 } : null),
        }}
      >
        {/* Строка 1: глиф, название, давность. lineHeight 19 (Р-1). */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 7, lineHeight: '19px' }}>
          <span style={{ display: 'flex', flexShrink: 0 }}>
            <FeatureGlyph kind={st.kind} size={14} />
          </span>
          <span
            title={feature.title}
            style={{
              flex: 1, minWidth: 0,
              fontSize: FS.base,
              fontWeight: compact ? 600 : 700,
              color: compact ? C.textSecondary : C.textHeading,
              whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            }}
          >
            {feature.title}
          </span>
          {age && (
            <span
              data-testid="feature-group-age"
              style={{
                flexShrink: 0,
                fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
                // Прячется при наведении (под быстрым действием) — overlap справа в строке 1.
                visibility: hovered ? 'hidden' : 'visible',
              }}
            >
              {age}
            </span>
          )}
        </div>

        {/* Строка 2: аватары, статус, процент. lineHeight 17 (Р-1). */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 7, lineHeight: '17px', minWidth: 0 }}>
          {avatars.length > 0 && (
            <span style={{ display: 'inline-flex', alignItems: 'center', flexShrink: 0 }}>
              {avatars.map((p, i) => (
                <span
                  key={p.id}
                  style={{
                    // Кольцо 2px bgCard + нахлёст −5 со второго (макет: стопка аватаров).
                    boxShadow: `0 0 0 2px ${C.bgCard}`,
                    marginLeft: i === 0 ? 0 : -5,
                    borderRadius: R.full,
                    display: 'inline-flex',
                  }}
                >
                  <PersonaAvatar persona={p} size={16} />
                </span>
              ))}
            </span>
          )}
          <span
            data-testid="feature-group-status"
            title={progress ? formatStepProgress(progress) : undefined}
            style={{
              flex: 1, minWidth: 0,
              fontSize: FS.xs,
              color: statusColor,
              fontWeight: statusWeight,
              fontFamily: FONT.sans,
              whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            }}
          >
            {plainRun ? (
              <>Сейчас <b data-testid="feature-group-now-name" style={{ color: C.textHeading, fontWeight: 600 }}>{firstName}</b>{statusRest > 0 ? ` +${statusRest}` : ''}</>
            ) : queueNums
              ? <>в очереди · <span data-testid="feature-group-queue-nums" style={{ fontFamily: FONT.mono }}>{queueNums}</span></>
              : status}
          </span>
          {!compact && bar && (
            <span
              data-testid="feature-group-percent"
              title={progress ? formatStepProgress(progress) : undefined}
              style={{
                flexShrink: 0, whiteSpace: 'nowrap',
                fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
              }}
            >
              <b style={{ color: C.textHeading }}>{percent} %</b>
            </span>
          )}
          {!compact && (feature.trackerIssues ?? 0) > 0 && (
            <span data-testid="feature-group-tracker-issues" style={{ display: 'inline-flex', flexShrink: 0 }}>
              <Badge tone="warning" size="xs" title="Трекер расходится с работой — оркестратор поправит">
                {`расхождений: ${feature.trackerIssues}`}
              </Badge>
            </span>
          )}
        </div>

        {/* Полоса трекера во всю ширину (С1 «Трек снизу»): та же, что в шапке В2. */}
        {!compact && bar && (
          <FeatureTrackerBar
            bar={bar}
            ariaLabel={trackerBarAria(bar)}
            testId="feature-group-bar"
            style={{ width: '100%', marginTop: 4 }}
          />
        )}
      </button>

      {/* Быстрое действие: absolute сверху-справа, видно на :hover/:focus-within.
          Позиционирование и test-id — на обёртке (Button не принимает data-testid). */}
      {showQuick && (
        <span
          data-testid="feature-group-quick"
          style={{
            position: 'absolute',
            top: 6,
            right: 6,
            // Видно при наведении/фокуса; в покое — прозрачно (но в DOM, для стабильных тестов).
            opacity: hovered ? 1 : 0,
            pointerEvents: hovered ? 'auto' : 'none',
            transition: 'opacity 0.15s',
          }}
        >
          <Button
            size="xs"
            variant="ghost"
            // Доступное имя: действие + фича («Ответить: <название>») — видимая подпись
            // «Ответить» сама по себе не говорит, какой фичи (M1, Р2).
            ariaLabel={`${quick}: ${feature.title}`}
            onClick={e => { e.stopPropagation(); onOpen(); }}
            style={{ background: C.bgWhite }}
          >
            {quick}
          </Button>
        </span>
      )}
    </div>
  );
}

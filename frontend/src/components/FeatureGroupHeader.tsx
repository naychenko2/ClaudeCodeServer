import { useState } from 'react';
import type { CSSProperties } from 'react';
import { ChevronRight } from 'lucide-react';
import { C, R, FS, FONT, SHADOW } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { FeatureGlyph } from './chat/feature/FeatureGlyphs';
import { FeatureRail } from './chat/feature/FeatureRail';
import { PersonaAvatar } from '../features/personas/PersonaAvatar';
import { Button } from './ui/Button';
import { featureDisplayState, featureQuickAction, featureAvatarPersonaIds, formatDuration } from '../lib/chatGroups';
import { stepProgressFromIndex, formatStepProgress } from '../lib/featureProgress';
import { dateOnly } from './chat/feature/featureTime';
import type { FeatureIndexFeature } from '../types/featureIndex';
import type { Persona } from '../types';

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

// Заголовок группы фичи в списке (макет features-overview-2.html, .ha — вариант А):
//   обёртка div (рамка, фон, кромка тревоги 3px) → ДВЕ СОСЕДНИЕ кнопки:
//     • шеврон — отдельная тач-цель (26 px десктоп / 40 на 360): сворачивает/разворачивает
//       чаты; клик — stopPropagation, onOpen НЕ зовёт;
//     • тело — открывает страницу фичи (onOpen; до Ф2.3 — заглушка).
//   строка 1: глиф 14, название, «давность» (mono 11, прячется при наведении);
//   строка 2: мини-рельса, стопка аватаров «кто сейчас», статус по тону, прогресс 4/6 ↺3;
//   быстрое действие (ui/Button xs ghost, bgWhite) — absolute сверху-справа, видно на
//   :hover/:focus-within; на 360 не рендерится (действие живёт на странице фичи).
// Итог — ровно 55 px (макет 54,5; Р-1: межстрочие строк 19/17 задаём явно, minHeight нет).
// Компактная (закрытая фича, .closed .fgh): рамка/фон прозрачные, название вторичным,
// статус «смержена {дата} · {длительность}» (successText) или «закрыта» (textSecondary);
// без рельсы/аватаров/прогресса/быстрого действия.
// Ловушки <button> (раздел 0 таблицы токенов) заданы явно: рамка/фон/шрифт/поля/выравнивание.

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
  // Тело: открывает страницу фичи (до Ф2.3 — заглушка).
  onOpen: () => void;
  isMobile?: boolean;
  // Закрытая фича «на втором плане» (макет .closed .fgh): без рамки/заливки/рельсы/аватаров.
  compact?: boolean;
  // «Кто» в статусе: имя персоны (attention.personaId / now0.personaId, резолвит вызывающий).
  personaName?: string | null;
  // «Сейчас» в мс для «сколько» (стабильные тесты); не задано — Date.now().
  now?: number;
  // Резолвер id персоны → Persona (для стопки аватаров); не задан — аватаров нет.
  personaById?: (id: string) => Persona | undefined;
  // Открыта страница фичи (рамка accentMuted, фон accentLight).
  selected?: boolean;
}) {
  const [hovered, setHovered] = useState(false);
  const st = featureDisplayState(feature, { now, personaName });
  // Честный прогресс (Ф-pr): из строки индекса; null — старые ответы (новых полей нет) →
  // строка прогресса не рисуется. Мёртвый фолбэк «N/M ↺K» убран: бэк всегда даёт данные,
  // а голое «N/M» без слов («3/5») запрещено ТЗ — единственный формат «принято N из T».
  const progress = stepProgressFromIndex(feature);
  // Кромка тревоги 3px: 'wait' → info (синий), 'stop' → danger (красный), иначе нет.
  const alarmEdge = st.alarm === 'wait' ? C.info : st.alarm === 'stop' ? C.danger : null;
  // Закрытая фича: статус «смержена {дата} · {длительность}» (макет .closed) или «закрыта».
  const closedMerged = feature.bucket === 'closed' && feature.summary?.mergedAt != null;
  const status = compact && feature.bucket === 'closed'
    ? (closedMerged
      ? `смержена ${dateOnly(feature.summary!.mergedAt)} · ${formatDuration(feature.summary!.durationMinutes)}`
      : 'закрыта')
    : st.status;
  // Тон статуса: тревога «ждёт/к мержу» → info 600, «стоит» → dangerText 600,
  // «доработка» → warningText 600, закрытая смержена → successText, иначе textSecondary 400.
  const statusColor = compact
    ? (closedMerged ? C.successText : C.textSecondary)
    : feature.bucket === 'merged' ? C.successText
    : st.alarm === 'wait' ? C.info
    : st.alarm === 'stop' ? C.dangerText
    : st.kind === 'loop' ? C.warningText
    : C.textSecondary;
  const statusWeight = (st.alarm != null || st.kind === 'loop' || (compact && closedMerged) || feature.bucket === 'merged') ? 600 : 400;
  // «Давность»: с тревоги (attention.since) или с последней активности (lastActivityAt).
  const age = compact ? '' : ageLabel(feature.attention?.since ?? feature.lastActivityAt, now);
  // Стопка аватаров «кто сейчас»: nowPersonaIds (волна 2) с фолбэком на now0 (волна 1).
  const avatars = featureAvatarPersonaIds(feature)
    .map(id => personaById?.(id))
    .filter((p): p is Persona => p != null);
  // Быстрое действие: «Ответить» / «К мержу» / «Ключевой чат» (null — нет).
  const quick = featureQuickAction(feature);
  // Быстрое действие рендерим только на десктопе (на 360 оно живёт на странице фичи).
  const showQuick = quick != null && !compact && !isMobile;

  // Рамка обёртки: longhand'ами (M1 волны 1 — шорткод + borderLeft:undefined React обнуляет).
  //   обычная  — 1px borderLight, bg bgCard;
  //   тревога  — 3px слева (info/danger) + 1px остальные;
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
  // Поля тела: обычная 7 10 7 2 (55 px), закрытая (compact) 5 10 5 2 (≈50,5, таблица §2);
  // на 360 закрытая — minHeight 52 (→ 54).
  const bodyPadding = compact ? '5px 10px 5px 2px' : '7px 10px 7px 2px';

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

      {/* Тело: открывает страницу фичи (onOpen). Две строки, gap 3. */}
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

        {/* Строка 2: мини-рельса, стопка аватаров, статус, прогресс. lineHeight 17 (Р-1). */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 7, lineHeight: '17px', minWidth: 0 }}>
          {!compact && <FeatureRail rail={feature.rail} closed={st.kind === 'done' || st.kind === 'closed'} label={feature.bucket === 'merged' ? 'Этап: смержена' : undefined} />}
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
            style={{
              flex: 1, minWidth: 0,
              fontSize: FS.xs,
              color: statusColor,
              fontWeight: statusWeight,
              fontFamily: FONT.sans,
              whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            }}
          >
            {status}
          </span>
          {!compact && (
            <span
              data-testid="feature-group-progress"
              title={progress ? formatStepProgress(progress) : undefined}
              style={{
                flexShrink: 1, minWidth: 0,
                overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
              }}
            >
              {progress ? formatStepProgress(progress) : null}
            </span>
          )}
        </div>
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
            // Видно при наведении/фокусе; в покое — прозрачно (но в DOM, для стабильных тестов).
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

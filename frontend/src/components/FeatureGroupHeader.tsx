import { useState } from 'react';
import type { CSSProperties } from 'react';
import { ChevronRight } from 'lucide-react';
import { C, R, FS, FONT, SHADOW } from '../lib/design';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { FeatureGlyph } from './chat/feature/FeatureGlyphs';
import { FeatureTrackerBar, trackerBarAria } from './chat/feature/FeatureTrackerBar';
import { featureTruthBar } from './chat/feature/featureTruthModel';
import { Button } from './ui/Button';
import { featureDisplayState, featureQuickAction, formatDuration, STAGE_NAME } from '../lib/chatGroups';
import { stepProgressFromIndex, formatStepProgress } from '../lib/featureProgress';
import { dateOnly } from './chat/feature/featureTime';
import type { FeatureIndexFeature, FeatureRailKey } from '../types/featureIndex';
import type { FeatureAsk, FeatureEta } from '../lib/featurePage';
import type { Persona } from '../types';

// Плашка группы фичи в списке чатов — v2 (записка features-simplify-v1 §3 R2, шаг
// FS-FL1): две строки и тонкая полоса. Полоса трекера — та же, что в шапке В2:
// она и сравнивает фичи глазами, вместо процента и цифр.
//
// Строка 1: глиф 14, название, справа срок (600 FS.sm C.textSecondary, tabular-nums —
// не моно): из feature.eta (FS-V4/FS-FT): endsAt → «≈ чт»; clockStopped → «часы
// стоят»; eta нет → «—». Закрытая/смерженная — дата мержа вместо срока.
// Строка 2: одна фраза. Обычная: «{этап} · {stepsAccepted}/{stepsTotal}» (этап —
// now0.stage, фолбэк на активной стадии рельсы; имена — общий STAGE_NAME). При
// feature.ask — вместо неё: wait/merge → «Ждёт тебя: {ask.text}» (warningText),
// stopped → «Стоит {давность}: {ask.text}» (dangerText). Закрытая: «Смержена ·
// {длительность}» / прежняя CLOSED_NO_MERGE_STATUS. Очередь и «ждёт согласия на
// мерж» без ask — прежние слова featureDisplayState (одна подпись на все места, Ш1d).
// Под строкой 2 — полоса трекера во всю ширину.
//
// Ушло из строки (R2): стопка аватаров, ⚠ расхождений трекера (он на странице
// фичи), процент (полоса говорит то же), давность активности, «Сейчас {Имя}».
//
// Высота — ровно 55 px (поля 6/10/3/2 + строки 19/17 + отступ над полосой 4 + полоса
// 4; minHeight не нужен). Кромка тревоги 3px — тоны В2: «ждёт» — C.warning,
// «стоит» — C.danger.
// Компактная (закрытая фича, .closed): без рамки/заливки/полосы — второй план, как
// прежде.
//
// Данные — строка индекса (те же числа, что страница фичи): срок — только
// feature.eta, тревога — только feature.ask (контракт FS-FL1: фронт их не выводит
// из других полей); полосы — общий FeatureTrackerBar.

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

// Срок справа в строке 1 (v2): «≈ чт» — день недели окончания из feature.eta (короткая
// форма строки списка; полная дата — на странице фичи); «часы стоят» — по бэковому
// clockStopped (даже при живом endsAt: часы не идут); eta нет / даты нет — «—».
// Чистая функция. Местный пояс (toLocaleDateString), как в featureTime/chatGroups.
function etaLabel(eta: FeatureEta | null | undefined): string {
  if (!eta) return '—';
  if (eta.clockStopped != null) return 'часы стоят';
  if (!eta.endsAt) return '—';
  const d = new Date(eta.endsAt);
  if (Number.isNaN(d.getTime())) return '—';
  return `≈ ${d.toLocaleDateString('ru-RU', { weekday: 'short' })}`;
}

// «Стоит 2 ч» — давность тревоги с ask.since: тот же формат длительности, что у
// статусов фичи (formatDuration). Нет since/невалидно — пустая строка (фраза без
// давности). Чистая функция: now — «сейчас» в мс (стабильные тесты; не задано — Date.now()).
function stoppedAge(ask: FeatureAsk, now?: number): string {
  if (!ask.since) return '';
  const t = new Date(ask.since).getTime();
  if (Number.isNaN(t)) return '';
  const min = Math.max(0, Math.round(((now ?? Date.now()) - t) / 60000));
  return formatDuration(min);
}

// Этап строки 2: now0.stage (контракт FS-FL1), фолбэк на активной стадии рельсы
// (old answers: now0 может не прийти). Имена — общий STAGE_NAME (chatGroups), своей
// копии слов нет. Нет ни того, ни другого (или ключ неизвестен) — null: выдуманного
// этапа нет, строка остаётся без слова этапа.
function stageNameOf(feature: FeatureIndexFeature): string | null {
  const key = feature.now0?.stage ?? feature.rail.find(s => s.state === 'active')?.key ?? null;
  if (!key) return null;
  return STAGE_NAME[key as FeatureRailKey] ?? null;
}

export function FeatureGroupHeader({ feature, expanded, onToggle, onOpen, isMobile = false, compact = false, personaName, now, selected = false }: {
  feature: FeatureIndexFeature;
  // Группа раскрыта (шеvron повёрнут, под заголовком — все чаты).
  expanded: boolean;
  // Шеврон: сворачивает/разворачивает (отдельная тач-цель; stopPropagation).
  onToggle: () => void;
  // Тело: открывает страницу фичи.
  onOpen: () => void;
  isMobile?: boolean;
  // Закрытая фича «на втором плане» (макет .closed .fgh): без рамки/заливки/полосы.
  compact?: boolean;
  // «Кто» в запасных словах состояния (featureDisplayState enrich; аватаров в v2 нет).
  personaName?: string | null;
  // «Сейчас» в мс для давности тревоги (стабильные тесты); не задано — Date.now().
  now?: number;
  // Резолвер id персоны. В v2 не потребляется (стопка аватаров ушла, R2); проп держим в
  // типе: вызывающий (SessionList) передаёт его, снятие — за FS-FL2a.
  personaById?: (id: string) => Persona | undefined;
  // Открыта страница фичи (рамка accentMuted, фон accentLight).
  selected?: boolean;
}) {
  const [hovered, setHovered] = useState(false);
  const st = featureDisplayState(feature, { now, personaName });
  // Честный прогресс (Ф-pr): из строки индекса; null — старые ответы (полосы нет).
  const progress = stepProgressFromIndex(feature);
  // Полоса — БЕЗ подсветки «в master», и это не пропуск: строка индекса (FeatureIndex.cs)
  // несёт корзины stepsAccepted/Review/Work/NotCreated, а ортогонального InMaster у неё
  // нет (решение постановщика, как в В2; новых полей в бэк не заводим). Числа,
  // которые индекс несёт, рисуются как есть.
  const bar = featureTruthBar(progress, null);
  // Кромка тревоги 3px — тоны В2: 'wait' → warning (жёлтый), 'stop' → danger (красный).
  const alarmEdge = st.alarm === 'wait' ? C.warning : st.alarm === 'stop' ? C.danger : null;

  // === v2 (FS-FL1): срок и одна фраза. Ключи — только feature.eta и feature.ask:
  // фронт их не выводит из других полей (контракт шага). ===

  // Закрытая/смерженная: справа вместо срока — дата мержа (тот же dateOnly, что у
  // подписей режима фичи); без мержа и без даты — «—».
  const closedMerged = feature.bucket === 'closed' && feature.summary?.mergedAt != null;
  const closedLike = feature.bucket === 'closed' || feature.bucket === 'merged';
  const etaText = closedLike
    ? (closedMerged ? dateOnly(feature.summary!.mergedAt) ?? '—' : '—')
    : etaLabel(feature.eta);

  // Строка 2 — одна фраза. Приоритет: закрытая → тревога (ask) → очередь → «{этап} ·
  // N/M» → прежнее слово состояния (merge_pending/пауза без этапа: их слов в v2 нет,
  // берём featureDisplayState — одна подпись на все места, Ш1d).
  const closedLine = closedMerged
    ? `Смержена · ${formatDuration(feature.summary!.durationMinutes)}`
    : closedLike ? st.status : null;
  const ask = closedLike ? null : feature.ask ?? null;
  const askAge = ask?.source === 'stopped' ? stoppedAge(ask, now) : '';
  const askText = ask
    ? (ask.source === 'stopped' ? `Стоит${askAge ? ` ${askAge}` : ''}: ${ask.text}` : `Ждёт тебя: ${ask.text}`)
    : null;
  // Очередь: цифры «N из M» набираем FONT.mono — при пересчёте не прыгает ширина.
  const queueNums = !closedLike && !ask && st.kind === 'queue' ? /^в очереди · (\d+ из \d+)$/.exec(st.status)?.[1] ?? null : null;
  const stage = closedLike || ask || st.kind === 'queue' ? null : stageNameOf(feature);
  const stageLine = stage ? `${stage} · ${feature.stepsAccepted}/${feature.stepsTotal}` : null;
  const line2Text = closedLine ?? askText ?? stageLine ?? st.status;
  // Тон строки 2: «ждёт тебя» — warningText 600, «стоит» — dangerText 600 (тоны В2);
  // смержена — successText 600; очередь — textPrimary 600; «доработка» — warningText
  // 600; этап и прочее — textSecondary 400.
  const line2Color = (closedMerged || st.kind === 'done') ? C.successText
    : ask ? (ask.source === 'stopped' ? C.dangerText : C.warningText)
    : st.kind === 'queue' ? C.textPrimary
    : st.kind === 'loop' ? C.warningText
    : C.textSecondary;
  const line2Weight = (closedMerged || st.kind === 'done' || ask != null || st.kind === 'queue' || st.kind === 'loop') ? 600 : 400;
  // Полный текст тревоги — в title (фраза на строке — с многоточием); полная строка
  // счётчиков — общий formatStepProgress.
  const line2Title = ask ? ask.text : progress ? formatStepProgress(progress) : undefined;
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
  // Поля тела (С1) — высота как сумма слоёв, ровно 55 px, без фиксированной height:
  //   1 рамка + 6 + строка1 19 + строка2 17 + отступ над полосой 4 + полоса 4 + 3 + 1 рамка = 55.
  // Строки идут вплотную (gap убран): зазор 3 ×2 поднимал плашку до 60,4 (дизайн-ревью
  // F2, major 2). Нижнее поле 3, а не 2: формула макета v4 (6/10/2/2 + 19 + 17 + 4 + 4)
  // даёт 54 при заявленных 55 — недостающий 1 px берём сюда, чтобы сумма слоёв была
  // ровно spec без height:55 и без обрезки содержимого.
  // Закрытая (compact) — прежние 5 10 5 2 (без полосы).
  const bodyPadding = compact ? '5px 10px 5px 2px' : '6px 10px 3px 2px';

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

      {/* Тело: открывает страницу фичи (onOpen). Две строки + полоса — вплотную (gap 0):
          высота плашки складывается из фиксированных слоёв, а не из зазоров между ними. */}
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
          ...(compact && isMobile ? { minHeight: 52 } : null),
        }}
      >
        {/* Строка 1 (v2): глиф, название, срок справа (600 FS.sm textSecondary,
            tabular-nums — не моно; закрытая — дата мержа). Высота 19 фиксирована
            (как .r1 в макете): содержимое не растягивает строку — из неё складывается
            высота плашки (55). */}
        <div data-testid="feature-group-row1" style={{ display: 'flex', alignItems: 'center', gap: 7, height: 19, lineHeight: '19px' }}>
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
          <span
            data-testid="feature-group-eta"
            style={{
              flexShrink: 0, whiteSpace: 'nowrap',
              fontSize: FS.sm, fontWeight: 600, color: C.textSecondary,
              // Цифры и слова срока одним шрифтом UI (не моно): табличные цифры не
              // дают строке прыгать при пересчёте.
              fontVariantNumeric: 'tabular-nums',
            }}
          >
            {etaText}
          </span>
        </div>

        {/* Строка 2 (v2): одна фраза — «{этап} · N/M» / «Ждёт тебя: …» / «Стоит …: …» /
            «Смержена · …». Аватаров, ⚠ расхождений и процента нет (R2: полоса ниже
            говорит о прогрессе глазами). Высота 17 фиксирована (.r2 макета) и
            alignItems: center: содержимое не растягивает строку (дизайн-ревью F2,
            major 2). */}
        <div data-testid="feature-group-row2" style={{ display: 'flex', alignItems: 'center', height: 17, lineHeight: '17px', minWidth: 0 }}>
          <span
            data-testid="feature-group-status"
            title={line2Title}
            style={{
              flex: 1, minWidth: 0,
              fontSize: FS.xs,
              color: line2Color,
              fontWeight: line2Weight,
              fontFamily: FONT.sans,
              whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
            }}
          >
            {queueNums
              ? <>в очереди · <span data-testid="feature-group-queue-nums" style={{ fontFamily: FONT.mono }}>{queueNums}</span></>
              : line2Text}
          </span>
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

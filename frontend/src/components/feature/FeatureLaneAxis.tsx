// Ось времени «Хронологии» (GC-F5b) — подписи шкалы над дорожками этапов.
//
// Шкала та же, что у дорожек: доли считает axisFrac (FeatureLaneModel), поэтому
// подписи встают ровно над своими интервалами. Подписи времени — через 6 ч плюс
// «сейчас ЧЧ:ММ» у открытой фичи. У закрытой/смерженной «сейчас» НЕТ: последняя
// подпись — дата закрытия (момент мержа из окна фичи).
//
// «Сейчас» приходит пропсом (now), а не берётся из края окна: у живой фичи окно
// stats — снимок журнала, он заканчивается ДО текущего момента, и подпись должна
// звать реальное время. axisFrac прижимает «сейчас» к правому краю шкалы, если
// он за окном; справа от шкалы — зона будущего.
//
// На 360 шестичасовые подписи наезжают друг на друга, поэтому там остаются только
// «начало … сейчас».
//
// Время — в локальной зоне, как в ленте (formatClock/formatDateTime из
// FeatureStatsBits).

import { C, FS, FONT, SP, Z } from '../../lib/design';
import { axisFrac, type LaneAxis } from './FeatureLaneModel';
import { formatClock, formatDate, formatDateTime } from './FeatureStatsBits';

// Шаг подписей шкалы, ч.
const TICK_HOURS = 6;
const TICK_MS = TICK_HOURS * 3600_000;

// Отметку ближе TICK_TAIL_GAP_MS к правой подписи («сейчас»/дата закрытия) не
// рисуем: она налезает на неё (на 360 и на узкой дорожке — особенно).
const TICK_TAIL_GAP_MS = TICK_MS;

export interface FeatureLaneAxisProps {
  // Окно фичи: границы (from/to) и timeFrac (доля шкалы, занятая временем).
  // null — данных нет: ось не рисуется.
  axis: LaneAxis | null;
  // Момент «сейчас» (ISO) — только у открытой фичи. null у закрытой/смерженной:
  // тогда подписи «сейчас» нет, последняя подпись — дата закрытия.
  now: string | null;
  // Узкая раскладка (360): только «начало … сейчас».
  isMobile?: boolean;
  // Узкая ШКАЛА (не окно): 601–~1100 px — часовые отметки налезают на подпись
  // «начало …» (первая отметка стоит на 16,6 % ширины шкалы). Тогда подписи как на
  // мобильном: «начало» без времени, часовых отметок нет (GC-F5c, замечание ревью).
  dense?: boolean;
  // Отступы шкалы под колонки сетки строки этапа (px): слева — колонка этапа
  // (250 px), справа — колонка длительности (104 px). Подписи встают ровно над
  // дорожкой (GC-F5c: ось и дорожка — одна шкала).
  insetLeft?: number;
  insetRight?: number;
}

interface AxisLabel {
  key: string;
  text: string;
  // Доля ширины шкалы, где стоит подпись (0..100).
  leftPct: number;
  // Выравнивание: начало — по левому краю, центр — по своей точке, конец — по правому.
  align: 'start' | 'center' | 'end';
  // «Сейчас» — акцентом и жирным (макет feature-gantt-6: .axis span.now).
  accent?: boolean;
}

// Подписи шкалы (чистая функция — считается без DOM).
export function buildAxisLabels(axis: LaneAxis, now: string | null, isMobile: boolean): AxisLabel[] {
  const labels: AxisLabel[] = [];
  const from = new Date(axis.from).getTime();
  const to = new Date(axis.to).getTime();
  if (!Number.isFinite(from) || !Number.isFinite(to) || to <= from) return labels;

  // Начало окна: на 360 — дата (часы не помещаются), на широкой — дата и время.
  labels.push({
    key: 'from',
    text: isMobile ? `начало ${formatDate(axis.from)}` : `начало ${formatDateTime(axis.from)}`,
    leftPct: 0,
    align: 'start',
  });

  // Отметки через 6 ч. Хвост возле «сейчас» пропускаем — подписи наезжают.
  if (!isMobile) {
    for (let k = 1; ; k += 1) {
      const t = from + k * TICK_MS;
      if (t > to) break;
      if (to - t < TICK_TAIL_GAP_MS) break;
      labels.push({
        key: `t${k}`,
        text: formatClock(new Date(t).toISOString()),
        leftPct: axisFrac(new Date(t).toISOString(), axis) * 100,
        align: 'center',
      });
    }
  }

  // Конец: «сейчас» у открытой фичи, дата закрытия — у закрытой/смерженной.
  if (now) {
    labels.push({
      key: 'now',
      text: `сейчас ${formatClock(now)}`,
      leftPct: axisFrac(now, axis) * 100,
      align: 'end',
      accent: true,
    });
  } else {
    labels.push({
      key: 'end',
      text: formatDateTime(axis.to),
      leftPct: axisFrac(axis.to, axis) * 100,
      align: 'end',
    });
  }

  return labels;
}

// Ось времени: липкая шапка с подписями шкалы.
export function FeatureLaneAxis({ axis, now, isMobile, dense, insetLeft = 0, insetRight = 0 }: FeatureLaneAxisProps) {
  if (!axis) return null;
  // Узкая шкала (мобильная раскладка ИЛИ узкая шкала на широком окне) — подписи
  // сокращённые: часовых отметок нет, «начало» без времени.
  const narrow = !!isMobile || !!dense;
  const labels = buildAxisLabels(axis, now, narrow);

  return (
    <div
      data-testid="lane-axis"
      style={{
        // Липкая шапка над лентой: Z.panelEdge — над содержимым панели, но ниже
        // любых оверлеев (поповер, модалка), поэтому ось не перекрывает их.
        position: 'sticky',
        top: 0,
        zIndex: Z.panelEdge,
        background: C.bgMain,
        borderBottom: `1px solid ${C.borderLight}`,
        padding: `${SP.xs}px 0`,
      }}
    >
      <div
        data-testid="lane-axis-scale"
        style={{
          position: 'relative', height: 16, fontFamily: FONT.mono, fontSize: FS.xxs, color: C.textMuted,
          // Шкала живёт в колонке дорожки: отступы под колонки сетки строки этапа.
          marginLeft: insetLeft, marginRight: insetRight,
        }}
      >
        {labels.map(l => (
          <span
            key={l.key}
            data-testid={`lane-axis-label-${l.key}`}
            style={{
              position: 'absolute',
              top: 0,
              left: `${l.leftPct}%`,
              whiteSpace: 'nowrap',
              transform: l.align === 'start' ? undefined : l.align === 'end' ? 'translateX(-100%)' : 'translateX(-50%)',
              color: l.accent ? C.accent : undefined,
              fontWeight: l.accent ? 700 : undefined,
            }}
          >
            {l.text}
          </span>
        ))}
      </div>
    </div>
  );
}

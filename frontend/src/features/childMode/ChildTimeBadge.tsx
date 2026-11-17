// «Спокойное время» в шапке ребёнка (KM-F3): плашка с остатком минут и полосой.
// Образец — timepill макета kids-shell-v2.html (строки 489-493, ветка docs/kids-shell-mockup)
// и записка kids-shell-v2-proposal.md «Новое №2»: собрана из Badge + ProgressBar.
// Презентационный компонент: без запросов и без своего состояния — все данные через пропсы.
//
// Принятые правила:
// - красного и секунд нет никогда: тон только neutral/warning, текст только минуты;
// - минуты больше 10 округляются до 5 («около»), ≤10 называются точно;
// - дневного лимита нет (minutesLeft = null) — только конец окна «до 19:00»;
// - на телефоне (compact) — без полосы, короткий текст, полный смысл в aria-label;
// - состояния кроме ok/warn — плашки нет вовсе: «время вышло», «вне окна» и правила —
//   отдельные экраны (KM-F4, KM-F6), время там говорят те экраны.

import { Clock } from 'lucide-react';
import { Badge } from '../../components/ui/Badge';
import { ProgressBar } from '../../components/ui/ProgressBar';
import { C, SP } from '../../lib/design';
import { ICON_PROPS, ICON_SIZE } from '../../components/ui/icons';
import type { ChildStatus } from './types';

// Ширина полосы в плашке — .timepill .bar из макета (48px). Это размер элемента,
// не отступ, поэтому вне шкалы SP.
const BAR_W = 48;

// Округление до 5-минутных шагов: 34 → «около 35», 37 → «около 35», 38 → «около 40».
// Точные ≤10 не округляются: за 5 минут ребёнку нужна точность, а не «около».
const roundTo5 = (n: number) => Math.round(n / 5) * 5;

// Склонение счётителя: 1 минута, 2–4 минуты, 5 и больше минут (11–14 — минут).
const minutesUnit = (n: number): string => {
  const lastTwo = n % 100;
  const last = lastTwo % 10;
  if (lastTwo >= 11 && lastTwo <= 14) return 'минут';
  if (last === 1) return 'минута';
  if (last >= 2 && last <= 4) return 'минуты';
  return 'минут';
};

// Конец окна из ISO 8601 с явным смещением (ADR-041, «Общие правила»): часы ребёнка —
// литеральные HH:mm из строки. Сервер пишет время в таймзоне ребёнка; перевод на часы
// браузера сдвинул бы обещанный конец окна на разницу таймзон.
const windowClock = (iso: string): string | null => {
  const m = iso.match(/T(\d{2}:\d{2})/);
  return m ? m[1] : null;
};

export interface ChildTimeBadgeProps {
  // Статус из GET /api/child/status (ADR-041, «Контракт /api/child/*»). Компонент
  // берёт из него ровно state/minutesLeft/windowEndsAt — остальное здесь не нужно.
  status: Pick<ChildStatus, 'state' | 'minutesLeft' | 'windowEndsAt'>;
  // Дневный лимит пространства — знаменатель процента полосы. Без него полосы нет:
  // полоса без знаменателя была бы ложью о времени. В контракте status лимита нет —
  // вызывающий передаёт его, только когда реально знает.
  totalMinutes?: number | null;
  // Телефон (спека макета, строка 493): без полосы, короткая форма; полный смысл — в aria-label.
  compact?: boolean;
}

export function ChildTimeBadge({ status, totalMinutes, compact }: ChildTimeBadgeProps) {
  if (status.state !== 'ok' && status.state !== 'warn') return null;
  const warn = status.state === 'warn';
  const minutes = status.minutesLeft;
  const window = status.windowEndsAt ? windowClock(status.windowEndsAt) : null;

  // Что сказать: остаток минут, иначе конец окна, иначе плашке нечего сказать — её нет.
  // head — обычный префикс строки, number — выделенная часть, plain — целая обычный
  // строка (конец окна, выделять нечего), spoken — полная фраза для aria-label.
  let head = '';
  let number: string | null = null;
  let plain: string | null = null;
  // spoken пуст не бывает: любая ветка, поставившая number или plain, заполняет и его
  let spoken = '';
  if (typeof minutes === 'number' && minutes > 0) {
    if (minutes > 10) {
      const rounded = roundTo5(minutes);
      number = `${rounded} мин`;
      head = compact ? '' : 'Ещё около ';
      spoken = `Осталось около ${rounded} минут`;
    } else {
      const unit = minutesUnit(minutes);
      number = compact ? `${minutes} мин` : `${minutes} ${unit}`;
      head = compact ? '' : 'Ещё ';
      spoken = `Осталось ${minutes} ${unit}`;
    }
  } else if (window) {
    plain = `до ${window}`;
    spoken = `Занятие закончится в ${window}`;
  }
  if (number === null && plain === null) return null;

  // Число жирное и своим цветом (макет: .timepill b — text-heading). В warning-тоне
  // цвет остаётся цветом тона — только жирность подчёркивает цифру.
  const num = (t: string) =>
    <span style={{ fontWeight: 600, ...(warn ? {} : { color: C.textHeading }) }}>{t}</span>;

  // Полоса — доля остатка в дневном лимите (макет: 35 из 45 → 78%, 5 из 45 → 11%).
  // Без знаменателя полосы нет намеренно. Перерасход разрешён И3 (начатый ход доживает
  // до конца) — за край полосы не выходим.
  const showBar = !compact && number !== null && typeof totalMinutes === 'number' && totalMinutes > 0;
  const pct = showBar && typeof minutes === 'number'
    ? Math.max(0, Math.min(100, Math.round((minutes / totalMinutes) * 100)))
    : 0;

  return (
    // role="status" + aria-label — по образцу живых индикаторов (ui/LiveDot.tsx):
    // полный смысл доступен голосовому чтению в любой форме плашки.
    <span role="status" aria-label={spoken}>
      <Badge tone={warn ? 'warning' : 'neutral'} size="sm" icon={<Clock {...ICON_PROPS} size={ICON_SIZE.sm} />}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: SP.sm }}>
          <span>
            {head}
            {number !== null ? num(number) : plain}
          </span>
          {showBar && (
            // Полоса внутри плашки, как .timepill .bar в макете. Заливка ProgressBar —
            // блок, в оплётке inline-flex, иначе она встала бы отдельной строкой.
            <span style={{ display: 'inline-flex', alignItems: 'center' }}>
              <ProgressBar value={pct} tone={warn ? 'warning' : 'success'} style={{ width: BAR_W }} />
            </span>
          )}
        </span>
      </Badge>
    </span>
  );
}

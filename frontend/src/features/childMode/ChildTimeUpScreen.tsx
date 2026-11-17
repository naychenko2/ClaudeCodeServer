// KM-F4: экран «Время вышло» — спокойный конец дня: заставка без ошибки и красного,
// итог дня и «Чем заняться без экрана». Состояние и данные приезжают только пропсами,
// запросов у компонента нет.
// Образец: docs/mockups/kids-shell-v2.html (ветка docs/kids-shell-mockup), timeout()
// (стр. 672–690) и записка kids-shell-v2-proposal.md, «Новое №3»: EmptyState-заставка +
// карточка + список с Check. Холст нейтральный: цвета предмета здесь нет — по записке
// (спецификация, п.2) главная и «время вышло» стоят без project.

import { CalendarClock, Check, Sun } from 'lucide-react';
import { C, FS, R, SHADOW, SP } from '../../lib/design';
import { ICON_PROPS, ICON_SIZE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { plural } from '../../lib/plural';
import { EmptyState } from '../../components/ui/EmptyState';
import { Island } from '../../components/ui/Island';
import type { ChildStatus } from './types';

// Тексты заставки по старшей причине (И3). Слов «ошибка», «лимит», «бюджет», «403» здесь
// нет намеренно: это не отказ взрослому, а спокойный конец ребёнку (записка, «Новое №3»).
// Экран принадлежит только трём состояниям конца дня. Для чужих (ok/warn/rules_pending)
// он молчит: фолбэк на «Уроки отдыхают» был бы ложью о дне, который ещё не кончился
// (вердикт KM-F4: экран обещает только то, что принёс сервер).
const HEADLINES = {
  time_up: {
    title: 'На сегодня с уроками всё',
    lead: 'Хорошая работа. Разговор сохранился — завтра продолжишь с того же места.',
  },
  out_of_window: {
    title: 'Уроки отдыхают',
    lead: 'Сейчас время уроков закрыто. Отдохни — допишешь, когда они снова откроются.',
  },
  budget_exhausted: {
    title: 'Уроки отдыхают',
    lead: 'На сегодня помощник уже всё сделал. Отдохни — допишешь, когда откроются уроки.',
  },
} as const;

type HeadlineKind = keyof typeof HEADLINES;

// «Чем заняться без экрана» — мостик в офлайн (записка: «спокойный конец со мостиком
// в офлайн»). Пункты предметно-нейтральные: экран стоит без цвета предмета, поэтому
// и подсказки — не про конкретный урок. Порядок = порядок списка.
const OFFLINE_IDEAS = [
  { title: 'Разомнись', text: 'Десять приседаний и хорошенько потянись.' },
  { title: 'Расскажи близким', text: 'Расскажи папе или маме, что сегодня разбирали. Объяснять — лучший способ запомнить.' },
  { title: 'Нарисуй на бумаге', text: 'Нарисуй то, что сегодня получилось. На бумаге оно остаётся с тобой.' },
  { title: 'Почитай что-нибудь своё', text: 'Книгу, комикс или журнал — то, что давно откладывалось.' },
] as const;

// Дни недели в форме после «в» (Intl даёт именительный падеж — «в пятница» было бы
// неверно). Индекс — getUTCDay: дата берётся из строки статуса, её день не меняется.
const WEEKDAY_IN = ['воскресенье', 'понедельник', 'вторник', 'среду', 'четверг', 'пятницу', 'субботу'] as const;

// «когда откроются» из nextWindowAt. ISO приходит со смещением таймзоны ребёнка
// (ADR-041, «Общие правила»), поэтому день и часы читаются прямо из строки: пересчёт
// в таймзону браузера сдвинул бы их (тот же приём, что windowEndsClock в ChildHome).
// nowMs — внешний, чтобы «сегодня/завтра» проверялись тестом на фиксированном времени.
function opensLabel(iso: string, nowMs: number): string | null {
  const m = iso.match(/^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/);
  if (!m) return null;
  const clock = `${m[4]}:${m[5]}`;
  const targetUtc = Date.UTC(+m[1], +m[2] - 1, +m[3]);
  // Смещение из хвоста строки ('+03:00', 'Z' — 0): сдвинув им текущий момент,
  // получаем «сегодня» в таймзоне ребёнка, а не браузера.
  const off = iso.match(/([+-])(\d{2}):(\d{2})$/);
  const offMin = off ? (off[1] === '-' ? -1 : 1) * (+off[2] * 60 + +off[3]) : 0;
  const now = new Date(nowMs + offMin * 60_000);
  const todayUtc = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  const days = Math.round((targetUtc - todayUtc) / 86_400_000);
  if (days <= 0) return `сегодня в ${clock}`;
  if (days === 1) return `завтра в ${clock}`;
  // Дальше завтра (выходные — окно в начале следующей недели): называем день.
  return `в ${WEEKDAY_IN[new Date(targetUtc).getUTCDay()]} в ${clock}`;
}

interface Props {
  // Старшая причина состояния (И3): здесь живут только time_up / out_of_window / budget_exhausted;
  // для чужых состояний компонент возвращает null (их рисует другой экран)
  status: ChildStatus;
  // Минуты с уроками за сегодня — итог дня для time_up. 0 или меньше — строки итога нет
  minutesToday: number;
}

export function ChildTimeUpScreen({ status, minutesToday }: Props) {
  const isMobile = useIsMobile(); // хук до раннего выхода: порядок хуков не зависит от состояния
  // Чужое состояние (ok/warn/rules_pending) рисует другой экран — этот не говорит
  // о конце дня и ничего не рисует (вердикт KM-F4).
  const kind: HeadlineKind | null =
    status.state === 'time_up' || status.state === 'out_of_window' || status.state === 'budget_exhausted'
      ? status.state
      : null;
  if (!kind) return null;
  const head = HEADLINES[kind];
  // Обещание времени — только когда его принёс сервер (nextWindowAt заполнен при
  // out_of_window, И3); без него заставка остаётся спокойной, но ничего не обещает.
  const opensAt = kind === 'out_of_window' && status.nextWindowAt
    ? opensLabel(status.nextWindowAt, Date.now())
    : null;

  return (
    <div style={{
      maxWidth: 760, width: '100%', margin: '0 auto', boxSizing: 'border-box',
      padding: isMobile ? `${SP.xl}px ${SP.lg}px` : `${SP.xxxl}px ${SP.xl}px`,
      display: 'flex', flexDirection: 'column', gap: SP.lg,
    }}>
      {/* Заставка: тёплый warning-круг (макет .es .big) вместо accent — экран не об отказе.
          Пустой span заполняет круг EmptyState целиком: свой цвет без правки ui-примитива. */}
      <EmptyState
        inline
        icon={
          <span style={{
            width: '100%', height: '100%', borderRadius: R.modal,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            background: C.warningBg, color: C.warningText,
          }}>
            <Sun {...ICON_PROPS} size={ICON_SIZE.xl} />
          </span>
        }
        iconRadius={R.modal}
        title={head.title}
        // Hero-размер детской заставки — по шкале (FS.display = hero-текст, h1 на мобиле),
        // как «Привет, …» на главной: ребёнку крупнее (записка, «Отклонения размеров»).
        titleSize={isMobile ? FS.h1 : FS.display}
        subtitle={<span style={{ fontSize: FS.lg, lineHeight: 1.55 }}>{head.lead}</span>}
      />

      {/* Итог дня — только у time_up: у «вне окна» дня ещё нет, у exhausted итог не в минутах. */}
      {kind === 'time_up' && minutesToday > 0 && (
        <Island bg={C.bgCard} borderColor={C.borderLight} shadow={SHADOW.card} style={{ borderRadius: R.xxl, padding: SP.lg }}>
          <div style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading, marginBottom: SP.sm }}>
            Итог дня
          </div>
          <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm }}>
            <span style={{ fontSize: FS.h2, fontWeight: 600, color: C.textHeading }}>
              {minutesToday} {plural(minutesToday, 'минута', 'минуты', 'минут')}
            </span>
            <span style={{ fontSize: FS.sm, color: C.textMuted }}>с уроками</span>
          </div>
        </Island>
      )}

      {/* Список, а не сетка карточек (макет .ideas): на 360 одна колонка выходит сама,
          горизонтальной прокрутке не взяться — widths здесь нет. */}
      <div>
        <div style={{
          fontSize: FS.xxs, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted,
          marginBottom: SP.sm,
        }}>
          Чем заняться без экрана
        </div>
        <ul style={{ margin: 0, padding: 0, listStyle: 'none', display: 'flex', flexDirection: 'column', gap: SP.md }}>
          {OFFLINE_IDEAS.map((idea) => (
            <li key={idea.title} style={{ display: 'flex', gap: SP.sm, alignItems: 'flex-start' }}>
              <Check {...ICON_PROPS} size={ICON_SIZE.sm} style={{ color: C.success, flexShrink: 0, marginTop: 2 }} />
              <span>
                <span style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading }}>{idea.title}</span>
                <span style={{ display: 'block', fontSize: FS.sm, lineHeight: 1.45, color: C.textSecondary }}>{idea.text}</span>
              </span>
            </li>
          ))}
        </ul>
      </div>

      {/* Мостик обратно: когда уроки откроются (макет .disclose) — последней строкой. */}
      {opensAt && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, fontSize: FS.sm, color: C.textMuted }}>
          <CalendarClock {...ICON_PROPS} size={ICON_SIZE.sm} style={{ flexShrink: 0 }} />
          <span>Уроки снова откроются {opensAt}</span>
        </div>
      )}
    </div>
  );
}

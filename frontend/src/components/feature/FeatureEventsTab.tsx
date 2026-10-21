// Вкладка «События» страницы фичи (волна 3, Ф4; макет features-overview-3-final §4/§9а).
//
// Перенос блока «События» из волны 2 (FeaturePageBody.FeaturePageEvents) в новую
// вёрстку макета:
//   • лента: время · аватар · имя персоны · понятная русская формулировка типа события ·
//     переходы «чат · задача» (NavTargetPair: чат = sessionId, задача = taskId);
//   • группировка по дням (заголовок «Сегодня, dd.mm» / «Вчера, dd.mm» / «dd.mm»);
//   • «Показать все» (порог 8, макет LIM): по умолчанию — 8 последних, раскрытие по
//     eventsTotal; «Свернуть» — обратно;
//   • тон: ожидания (ждёт вас) — info, ошибки (исполнитель остановился) — danger,
//     мерж/закрытие — accent;
//   • пустое: «Событий пока нет».
//
// Данные — ТОЛЬКО из `features/{id}` → `events[]` + `eventsTotal`; бэк не меняем.
// Формулировка строится на фронте из `kind` + `stepRef`/`role` (бэк не подписывает).

import { useMemo, useState } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { getPersonaById } from '../../lib/personas';
import { dateOnly, hhmm } from '../chat/feature/featureTime';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { ExecutorAvatar } from './FeatureTimelineBits';
import { NavTargetPair } from './NavTargetButton';
import type { FeatureEvent } from '../../types/featureIndex';

// Порог «Показать все» (макет: LIM=8). По умолчанию — 8 последних событий.
export const EVENTS_COLLAPSED_LIMIT = 8;

// === Чистые функции (тестируются в node, без DOM) ===

// Тон события (макет: ожидания/ошибки/мерж выделены):
//   wait  — «ждёт вашего ответа» (info, синий);
//   error — «исполнитель остановился» (danger, красный);
//   merge — мерж/закрытие (accent);
//   null  — обычное событие (без подсветки).
export type EventTone = 'wait' | 'error' | 'merge' | null;
export function eventTone(e: FeatureEvent): EventTone {
  switch (e.kind) {
    case 'waiting': return 'wait';
    case 'stopped': return 'error';
    case 'merged':
    case 'closed': return 'merge';
    default: return null;
  }
}

// Понятная русская формулировка типа события (макет: «создала фичу», «начал Ш1»,
// «вернул Ш3: major 1», «смержил в master»). Гендер-нейтральные конструкции:
// имя персоны — отдельно (жирно), действие — самодостаточная фраза.
export function eventPhrase(e: FeatureEvent): string {
  const ref = e.stepRef ?? '';
  switch (e.kind) {
    case 'created':
      // Эпик (role=feature/пусто) — «создание фичи»; иначе — по роли.
      if (!e.role || e.role === 'feature') return 'создание фичи';
      return `создание: ${e.role}`;
    case 'started':
      return ref ? `старт ${ref}` : 'старт';
    case 'completed':
      return ref ? `сдача ${ref}` : 'сдача';
    case 'accepted':
      return ref ? `принято ${ref}` : 'принято';
    case 'accepted_with_remarks':
      return ref ? `принято ${ref} с замечаниями` : 'принято с замечаниями';
    case 'returned':
      return ref ? `возврат ${ref}` : 'возврат';
    case 'withdrawn':
      return ref ? `снято ${ref}` : 'снято';
    case 'stopped':
      return 'исполнитель остановился';
    case 'waiting':
      return 'ждёт вашего ответа';
    case 'merged':
      return 'мерж в master';
    case 'closed':
      return 'фича закрыта';
    default:
      // Неизвестный вид — честно показываем сырой kind (не выдумываем подпись).
      return e.kind;
  }
}

// Заголовок дня (макет: «Сегодня, dd.mm» / «Вчера, dd.mm» / «dd.mm»).
// «Сегодня»/«Вчера» — по локальной дате (featureTime: локальные геттеры).
export function eventDayLabel(iso: string, now: number): string {
  const d = dateOnly(iso);
  if (!d) return '';
  const today = new Date(now);
  const ev = new Date(iso);
  const sameDay = (a: Date, b: Date) =>
    a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
  if (sameDay(today, ev)) return `Сегодня, ${d}`;
  const yest = new Date(now);
  yest.setDate(yest.getDate() - 1);
  if (sameDay(yest, ev)) return `Вчера, ${d}`;
  return d;
}

// Сгруппировать события по дням (порядок — как в бэке: at desc). Возвращает пары
// [заголовок дня, события дня] в порядке первого вхождения дня.
export interface EventDay {
  label: string;
  events: FeatureEvent[];
}
export function groupEventsByDay(events: FeatureEvent[], now: number): EventDay[] {
  const days: EventDay[] = [];
  let cur: EventDay | null = null;
  for (const e of events) {
    const label = eventDayLabel(e.at, now);
    if (!cur || cur.label !== label) {
      cur = { label, events: [] };
      days.push(cur);
    }
    cur.events.push(e);
  }
  return days;
}

// === Компоненты ===

// Строка события (макет .ev): время (mono, muted) · аватар 16 · имя (жирно) ·
// формулировка · «чат · задача». Тон: wait (info) / error (danger) / merge (accent).
function EventRow({ e, featureId, isMobile = false }: {
  e: FeatureEvent; featureId: string; isMobile?: boolean;
}) {
  const persona = e.personaId ? getPersonaById(e.personaId) : undefined;
  const name = persona?.name ?? (e.personaId ? e.personaId : null);
  const time = hhmm(e.at) ?? dateOnly(e.at) ?? '';
  const tone = eventTone(e);

  // Подсветка тона (макет: ожидания/ошибки/мерж выделены).
  const nameColor =
    tone === 'wait' ? C.info :
    tone === 'error' ? C.dangerText :
    tone === 'merge' ? C.accent :
    C.textHeading;

  // Переход в чат события: openChatById(sessionId) + точка возврата.
  const openChat = () => {
    if (!e.sessionId) return;
    recordFeatureReturn(featureId, { kind: 'chat', id: e.sessionId });
    void openChatById(e.sessionId);
  };
  // Переход в задачу события: openTaskInSection(taskId) + точка возврата.
  const openTask = () => {
    if (!e.taskId) return;
    recordFeatureReturn(featureId, { kind: 'task', id: e.taskId });
    openTaskInSection({ id: e.taskId });
  };

  return (
    <div
      data-testid={`event-row-${e.kind}-${e.taskId}`}
      style={{
        display: 'grid', gridTemplateColumns: '48px 20px minmax(0, 1fr)',
        columnGap: SP.sm, alignItems: 'start', padding: '5px 0',
        fontSize: FS.sm, color: C.textSecondary,
      }}
    >
      <span data-testid="event-time" style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, paddingTop: 2, whiteSpace: 'nowrap' }}>
        {time}
      </span>
      <span style={{ display: 'flex', justifyContent: 'center', paddingTop: 2 }}>
        <ExecutorAvatar personaId={e.personaId ?? null} />
      </span>
      <span style={{ minWidth: 0, display: 'flex', alignItems: 'baseline', gap: 6, flexWrap: 'wrap' }}>
        {name && (
          <span data-testid="event-who" style={{ fontWeight: 600, color: nameColor, whiteSpace: 'nowrap' }}>
            {name}
          </span>
        )}
        <span data-testid="event-phrase" style={{ minWidth: 0, overflowWrap: 'anywhere' }}>
          {eventPhrase(e)}
        </span>
        {e.reason && (
          <span data-testid="event-reason" style={{ minWidth: 0, fontSize: FS.xs, color: C.textMuted, overflowWrap: 'anywhere' }}>
            {e.reason}
          </span>
        )}
        <span style={{ display: 'inline-flex', gap: 4, marginLeft: 'auto' }}>
          <NavTargetPair
            chatId={e.sessionId ?? null}
            onOpenChat={openChat}
            chatTitle="Открыть чат. Страница фичи скроется, вернётся по плашке."
            showNoChat={false}
            taskId={e.taskId || null}
            onOpenTask={openTask}
            taskTitle="Открыть задачу. Страница фичи скроется, вернётся по плашке."
            isMobile={isMobile}
          />
        </span>
      </span>
    </div>
  );
}

// Заголовок дня (макет .evday): mono 10, капс, muted, letter-spacing .06em.
function DayHeader({ label }: { label: string }) {
  return (
    <div
      data-testid="event-day"
      style={{
        fontFamily: FONT.mono, fontSize: FS.xxs, fontWeight: 700, color: C.textMuted,
        textTransform: 'uppercase', letterSpacing: '0.06em',
        margin: '12px 0 4px',
      }}
    >
      {label}
    </div>
  );
}

export interface FeatureEventsTabProps {
  // Лента событий (at desc) + общее число (до обрезки) — единственный источник вкладки.
  events: FeatureEvent[];
  eventsTotal: number;
  // Id фичи (навигация: точка возврата).
  featureId: string;
  isMobile?: boolean;
}

export function FeatureEventsTab({ events, eventsTotal, featureId, isMobile = false }: FeatureEventsTabProps) {
  const [expanded, setExpanded] = useState(false);
  // «Сейчас» — фиксированный момент (Date.now на рендер) для подписей «Сегодня/Вчера»;
  // стабильен в пределах кадра (как now в FeatureTimelineTab).
  const now = useMemo(() => Date.now(), []);

  // Пустое: событий нет вовсе.
  if (events.length === 0) {
    return (
      <div data-testid="events-empty" style={{ padding: '24px 0', textAlign: 'center', fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted }}>
        Событий пока нет
      </div>
    );
  }

  const shown = expanded ? events : events.slice(0, EVENTS_COLLAPSED_LIMIT);
  const days = groupEventsByDay(shown, now);
  const hasMore = events.length > EVENTS_COLLAPSED_LIMIT;

  return (
    <div data-testid="events" style={{ display: 'flex', flexDirection: 'column', fontFamily: FONT.sans }}>
      {days.map((day, i) => (
        <div key={day.label + i}>
          <DayHeader label={day.label} />
          {day.events.map((e, j) => (
            <EventRow key={`${e.kind}-${e.taskId}-${j}`} e={e} featureId={featureId} isMobile={isMobile} />
          ))}
        </div>
      ))}
      {hasMore && (
        <button
          type="button"
          data-testid="events-show-all"
          onClick={() => setExpanded(v => !v)}
          style={{
            display: 'block', width: '100%', marginTop: SP.sm,
            border: `1px solid ${C.border}`, background: C.bgSelected,
            borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`,
            fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
            cursor: 'pointer', fontFamily: FONT.sans,
            minHeight: isMobile ? 40 : 32,
          }}
        >
          {expanded ? 'Свернуть' : `Показать все · ${eventsTotal}`}
        </button>
      )}
    </div>
  );
}

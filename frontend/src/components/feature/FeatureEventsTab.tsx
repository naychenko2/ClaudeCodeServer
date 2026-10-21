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

import { useMemo } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { getPersonaById } from '../../lib/personas';
import { roleLabel } from '../../lib/roleLabel';
import { dateOnly, hhmm } from '../chat/feature/featureTime';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import {
  expandFeaturePageEvents, collapseFeaturePageEvents,
  useFeaturePageEventsExpanded, useFeaturePageExpandError,
} from '../../lib/featurePage';
import { ExecutorAvatar } from './FeatureTimelineBits';
import { NavTargetPair } from './NavTargetButton';
import { Button } from '../ui/Button';
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
      // Эпик (role=feature/пусто) — «создание фичи»; иначе — по роли. Роль —
      // русским словом через roleLabel (не сырой step/rework); роль вне словаря
      // (roleLabel → '') — нейтральное «создана задача» без сырого слова.
      if (!e.role || e.role === 'feature') return 'создание фичи';
      const role = roleLabel(e.role, e.stepRef ?? null, null);
      return role ? `создание: ${role}` : 'создана задача';
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
function EventRow({ e, index, featureId, isMobile = false }: {
  e: FeatureEvent; index: number; featureId: string; isMobile?: boolean;
}) {
  const persona = e.personaId ? getPersonaById(e.personaId) : undefined;
  // Персона в данных, но не найдена (недоступна) — нейтральное «персона недоступна»,
  // не сырой UUID; без personaId — имени нет вовсе.
  const name = persona?.name ?? (e.personaId ? 'персона недоступна' : null);
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
      // Уникальный testid: kind + taskId + глобальный индекс в показанной ленте
      // (два события с одним kind/taskId — разные строки, findNodeByAttr не путает).
      data-testid={`event-row-${e.kind}-${e.taskId}-${index}`}
      style={{
        display: 'grid', gridTemplateColumns: '48px 20px minmax(0, 1fr)',
        columnGap: SP.sm, alignItems: 'start', padding: `${SP.xs}px 0`,
        fontSize: FS.sm, color: C.textSecondary,
      }}
    >
      <span data-testid="event-time" style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, paddingTop: SP.xxs, whiteSpace: 'nowrap' }}>
        {time}
      </span>
      <span style={{ display: 'flex', justifyContent: 'center', paddingTop: SP.xxs }}>
        <ExecutorAvatar personaId={e.personaId ?? null} />
      </span>
      <span style={{ minWidth: 0, display: 'flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
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
        <span style={{ display: 'inline-flex', gap: SP.xs, marginLeft: 'auto' }}>
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
        margin: `${SP.md}px 0 ${SP.xs}px`,
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
  // Раскрытие (все события, не только 8) и локальная ошибка догрузки — из стора страницы
  // (не локальный useState): переживают уход на другую вкладку и возврат (м.3), а
  // догруженная лента не перезапрашивается при повторном раскрытии (м.2).
  const expanded = useFeaturePageEventsExpanded();
  const expandError = useFeaturePageExpandError();
  // «Сейчас» — фиксированный момент (Date.now на рендер) для подписей «Сегодня/Вчера»;
  // стабильен в пределах кадра (как now в FeatureTimelineTab).
  const now = useMemo(() => Date.now(), []);

  // Пустое: событий нет вовсе.
  if (events.length === 0) {
    return (
      <div data-testid="events-empty" style={{ padding: `${SP.xl}px 0`, textAlign: 'center', fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted }}>
        Событий пока нет
      </div>
    );
  }

  const shown = expanded ? events : events.slice(0, EVENTS_COLLAPSED_LIMIT);
  const days = groupEventsByDay(shown, now);
  const hasMore = events.length > EVENTS_COLLAPSED_LIMIT;
  // Глобальный индекс события в показанной ленте (для уникального data-testid).
  const shownIndex = new Map<FeatureEvent, number>();
  shown.forEach((e, i) => shownIndex.set(e, i));

  return (
    <div data-testid="events" style={{ display: 'flex', flexDirection: 'column', fontFamily: FONT.sans }}>
      {days.map((day, i) => (
        <div key={day.label + i}>
          <DayHeader label={day.label} />
          {day.events.map(e => (
            <EventRow
              key={`${e.kind}-${e.taskId}-${shownIndex.get(e)}`}
              e={e}
              index={shownIndex.get(e)!}
              featureId={featureId}
              isMobile={isMobile}
            />
          ))}
        </div>
      ))}
      {hasMore && expandError && (
        // Догрузка (eventsLimit=all) упала: страница не роняется (данные загружены),
        // на ленте — локальная ошибка с «Повторить» (м.1).
        <div data-testid="events-expand-error" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.sm, padding: `${SP.xs}px ${SP.sm}px`, background: C.dangerBg, borderRadius: R.md, border: `1px solid ${C.dangerBorder}` }}>
          <span style={{ fontSize: FS.sm, color: C.dangerText, flex: 1 }}>Не удалось догрузить все события</span>
          <Button
            variant="ghost"
            size="xs"
            dataTestId="events-expand-retry"
            onClick={() => expandFeaturePageEvents(featureId)}
            style={{ background: C.bgWhite, minHeight: isMobile ? 40 : 32 }}
          >
            Повторить
          </Button>
        </div>
      )}
      {hasMore && !expandError && (
        <Button
          variant="ghost"
          size="xs"
          fullWidth
          dataTestId="events-show-all"
          onClick={() => {
            // «Показать все» — ДОГРУЗИТЬ всю ленту (eventsLimit=all): бэк режет её
            // до 100, а eventsTotal — общее число до обрезки. Без догрузки раскрытие
            // показало бы только 100 из 1000. «Свернуть» — только UI (без запроса).
            if (expanded) collapseFeaturePageEvents(featureId);
            else expandFeaturePageEvents(featureId);
          }}
          style={{ marginTop: SP.sm, background: C.bgSelected, minHeight: isMobile ? 40 : 32 }}
        >
          {expanded ? 'Свернуть' : `Показать все · ${eventsTotal}`}
        </Button>
      )}
    </div>
  );
}

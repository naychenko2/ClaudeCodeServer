import type { CSSProperties, ReactNode } from 'react';
import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { getPersonaById, usePersonasVersion } from '../../lib/personas';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { Button } from '../ui/Button';
import { PipelineFlow } from '../chat/feature/PipelineFlow';
import {
  featureChats, stepRows, stepStatusLabel, taskCounters, eventRows, showAllNeeded,
  chatRoleLabel, laneLabel, eventTimeLabel,
  type FeatureChatEntry, type StepRow, type TaskCounters, type EventRow,
} from './featurePageBody';
import type { FeatureIndexFeature, FeaturesIndex, FeatureEvent } from '../../types/featureIndex';
import type { FeatureRun } from '../../types/featureRun';

// Токены макета §5.4–5.6 (features-overview-2.html, вариант А):
// .blk — ОТКРЫТЫЙ блок (marginBottom 18, без фона/рамки): подпись .cap
// (11/700, капс, 0,06em, textMuted, marginBottom 6, gap 6; правая часть .r —
// marginLeft auto, без капса) + тело;
// .cols — две колонки: grid minmax(0,1fr) 300px, gap 18, align-items start;
// .rw — строка (чат/шаг): flex, gap 8, padding 7 8, margin 0 -8, R 8,
// minHeight 36 (360: 44), 12,5, cursor pointer, width calc(100% + 16px);
// .ev — строка события: grid 44px 16px minmax(0,1fr), column-gap 8, padding 5 0,
// 12 textSecondary; время mono 10,5 textMuted (paddingTop 1), аватар 16,
// «кто» 600 textHeading;
// .cnts — счётчики: теги §5.1 (11/600, R 6, padding 1 7), row gap 6, marginBottom 6.
// Все значения — из шкал FS/SP/R, где есть; константы — где в шкале нет.

// Единый focus-visible ring (клавиатура) для строк чатов/шагов и ссылок «открыть чат» —
// тот же паттерн, что у крошки (FeaturePageHeader): один инжект <style> на модуль.
const FOCUS_CLASS = 'cc-feature-page-body-focus';
if (typeof document !== 'undefined' && !document.getElementById('cc-feature-page-body-focus-style')) {
  const el = document.createElement('style');
  el.id = 'cc-feature-page-body-focus-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

// Подпись блока (.blk .cap): 11/700, капс, разрядка 0,06em, textMuted,
// marginBottom 6, gap 6. Правая часть (.r) — ОТДЕЛЬНЫЙ элемент: marginLeft auto,
// без капса и разрядки (M4: число не склеивается с подписью — «Шаги 2», не «ШАГИ2»).
function BlockCap({ cap, right, rightTestId }: { cap: ReactNode; right?: ReactNode; rightTestId?: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 6, fontSize: FS.xs, color: C.textMuted }}>
      <span style={{ fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase' }}>{cap}</span>
      {right != null && (
        <span data-testid={rightTestId} style={{ marginLeft: 'auto', textTransform: 'none', letterSpacing: 0 }}>{right}</span>
      )}
    </div>
  );
}

// Блок (.blk): ОТКРЫТЫЙ — без фона/рамки/полей (M6: не карточка), отступ снизу 18.
function Block({ cap, right, rightTestId, children }: { cap: ReactNode; right?: ReactNode; rightTestId?: string; children: ReactNode }) {
  return (
    <div style={{ marginBottom: 18 }}>
      <BlockCap cap={cap} right={right} rightTestId={rightTestId} />
      {children}
    </div>
  );
}

// Строка (.rw): общий каркас для строк чатов и шагов (макет .rw, таблица §5.5).
// Ловушки <button> (§0): рамка/фон/шрифт/цвет/поля/выравнивание — явно;
// focus — SHADOW.focus (FOCUS_CLASS); touch-цель ≥ 44 на 360.
function rowBase(isMobile: boolean, extra?: CSSProperties): CSSProperties {
  return {
    display: 'flex', alignItems: 'center', gap: SP.sm,
    padding: '7px 8px', margin: '0 -8px', width: 'calc(100% + 16px)',
    borderRadius: R.md, minHeight: isMobile ? 44 : 36,
    border: 'none', background: 'none', color: 'inherit',
    fontFamily: FONT.sans, fontSize: FS.sm, textAlign: 'left',
    cursor: 'pointer', boxSizing: 'border-box',
    ...extra,
  };
}

// Аватар 18 в строке (таблица §5.5): PersonaAvatar, если персона известна;
// иначе нейтральный круг (bgSelected) — без персоны строку не роняем.
function RowAvatar({ personaId }: { personaId: string | null }) {
  usePersonasVersion();
  const persona = personaId ? getPersonaById(personaId) : undefined;
  if (persona) return <PersonaAvatar persona={persona} size={18} />;
  return <span style={{ width: 18, height: 18, borderRadius: R.full, background: C.bgSelected, display: 'inline-block', flexShrink: 0 }} />;
}

// Метка роли чата (таблица §5.5): mono 10,5 / 400, фон bgSelected, textSecondary,
// R 6, padding 0 5. «Ключевой» — фон accentLight, текст accent, 700.
// Роль — русским словом (M1: не коды curator/executor); у исполнителя —
// код шага и линия, если есть.
function RoleTag({ kind, stepRef, lane, isKey }: {
  kind: string; stepRef?: string | null; lane?: string | null; isKey?: boolean;
}) {
  if (isKey) {
    return (
      <span style={{
        fontFamily: FONT.mono, fontSize: FS.xxs, fontWeight: 700,
        background: C.accentLight, color: C.accent, borderRadius: R.sm, padding: '0 5px', whiteSpace: 'nowrap',
      }}>ключевой</span>
    );
  }
  const parts = [chatRoleLabel(kind)];
  if (stepRef) parts.push(stepRef);
  if (lane) parts.push(laneLabel(lane));
  return (
    <span style={{
      fontFamily: FONT.mono, fontSize: FS.xxs, fontWeight: 400,
      background: C.bgSelected, color: C.textSecondary, borderRadius: R.sm, padding: '0 5px', whiteSpace: 'nowrap',
    }}>{parts.join(' · ')}</span>
  );
}

// Строка чата (.rw, таблица §5.5): кнопка — клик ведёт в чат (onOpenChat,
// механизм onBack, Ф2.3). Аватар 18 + название (многоточие) + метка роли.
// Ключевой чат: фон accentLight, текст 700 textHeading (M1: не accentSoft+рамка).
function ChatRow({ entry, onOpenChat, isMobile }: {
  entry: FeatureChatEntry;
  onOpenChat: (chatId: string) => void;
  isMobile: boolean;
}) {
  return (
    <button
      type="button"
      data-testid={`feature-chat-${entry.sessionId}`}
      className={FOCUS_CLASS}
      onClick={() => onOpenChat(entry.sessionId)}
      style={rowBase(isMobile, entry.isKey ? { background: C.accentLight } : undefined)}
    >
      <span style={{ display: 'flex', flexShrink: 0 }}>
        <RowAvatar personaId={entry.personaId} />
      </span>
      <span style={{
        flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        color: entry.isKey ? C.textHeading : C.textPrimary,
        fontWeight: entry.isKey ? 700 : 400,
      }}>
        {entry.name ?? 'Чат'}
      </span>
      <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexShrink: 0 }}>
        {entry.isArchived && (
          <span style={{
            fontFamily: FONT.mono, fontSize: FS.xxs, background: C.bgSelected, color: C.textSecondary,
            borderRadius: R.sm, padding: '0 5px', whiteSpace: 'nowrap',
          }}>в архиве</span>
        )}
        <RoleTag kind={entry.link.kind} stepRef={entry.link.stepRef} lane={entry.link.lane} isKey={entry.isKey} />
      </span>
    </button>
  );
}

// Тег исхода шага: слова — готовые подписи бэка (stepStatusLabel, одна точка
// правды), тон — по outcome (таблица §5.1: 11/600, R 6, padding 1 7).
function stepTag(row: StepRow): { text: string; bg: string; fg: string } {
  const text = stepStatusLabel(row.outcome, row.status);
  switch (row.outcome) {
    case 'accepted': return { text, bg: C.successBg, fg: C.successText };
    case 'running': return { text, bg: C.bgSelected, fg: C.textHeading };
    case 'returned':
    case 'accepted_with_remarks': return { text, bg: C.warningBg, fg: C.warningText };
    case 'withdrawn': return { text, bg: C.bgSelected, fg: C.textMuted };
    case 'pending': return { text, bg: C.bgSelected, fg: C.textMuted };
    default: return { text, bg: C.bgSelected, fg: C.textSecondary };
  }
}

// Строка шага (.rw, таблица §5.5): КНОПКА, если шаг ведёт в чат (sessionId ≠ null —
// чат исполнителя из индекса), иначе ТЕКСТ (div) (M4). Слева: аватар 18 (исполнитель)
// + код (mono 11/700 textHeading) + « · » + название (12 textPrimary, многоточие;
// код в названии не повторяется — stripStepCode). Справа: линия + «круг N»
// (11 textMuted) + тег исхода.
function StepRowView({ row, onOpenChat, isMobile }: {
  row: StepRow;
  onOpenChat: (chatId: string) => void;
  isMobile: boolean;
}) {
  const tag = stepTag(row);
  const inner = (
    <>
      <span style={{ display: 'flex', flexShrink: 0 }}>
        <RowAvatar personaId={row.personaId} />
      </span>
      <span style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'baseline', gap: SP.xs }}>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700, color: C.textHeading, flexShrink: 0 }}>{row.stepRef}</span>
        <span style={{ color: C.textPrimary, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{row.title}</span>
      </span>
      <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexShrink: 0, whiteSpace: 'nowrap' }}>
        {row.lane && <span style={{ fontSize: FS.xs, color: C.textMuted }}>{laneLabel(row.lane)}</span>}
        {row.rounds > 1 && <span style={{ fontSize: FS.xs, color: C.textMuted }}>· круг {row.rounds}</span>}
        <span style={{
          fontSize: FS.xs, fontWeight: 600, borderRadius: R.sm, padding: '1px 7px',
          background: tag.bg, color: tag.fg,
        }}>{tag.text}</span>
      </span>
    </>
  );
  if (row.sessionId) {
    return (
      <button
        type="button"
        data-testid={`feature-step-${row.stepRef}`}
        className={FOCUS_CLASS}
        onClick={() => onOpenChat(row.sessionId!)}
        style={rowBase(isMobile)}
      >
        {inner}
      </button>
    );
  }
  return (
    <div data-testid={`feature-step-${row.stepRef}`} style={rowBase(isMobile, { cursor: 'default' })}>
      {inner}
    </div>
  );
}

// Счётчики задач (таблица §5.5): теги §5.1 (11/600, R 6, padding 1 7, gap 4),
// ряд gap 6, marginBottom 6. Метрика «0» не выводится.
function TaskCountersView({ c }: { c: TaskCounters }) {
  const tag = (text: string, bg: string, fg: string) => (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 4,
      fontSize: FS.xs, fontWeight: 600, fontFamily: FONT.sans,
      padding: '1px 7px', borderRadius: R.sm, whiteSpace: 'nowrap',
      background: bg, color: fg,
    }}>{text}</span>
  );
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 6 }}>
      {c.done > 0 && tag(`готово ${c.done}`, C.successBg, C.successText)}
      {c.inProgress > 0 && tag(`в работе ${c.inProgress}`, C.bgSelected, C.textHeading)}
      {c.pending > 0 && tag(`впереди ${c.pending}`, C.bgSelected, C.textMuted)}
    </div>
  );
}

// Словарь видов событий (бэк: FeatureEvent.Kind, 12 значений) → слова по-русски.
// Неизвестный вид (бэк расширил контракт) — как есть, не роняем строку.
const KIND_LABEL: Record<string, string> = {
  created: 'создана',
  started: 'запущена',
  stopped: 'остановлена',
  waiting: 'ждёт',
  accepted: 'принята',
  accepted_with_remarks: 'принята с правками',
  returned: 'возвращена',
  withdrawn: 'снята',
  completed: 'закрыта',
  dropped: 'сброшена',
  merged: 'смержена',
  closed: 'закрыта',
};

// Роль задачи (TaskItem.Role) → слово; null — без роли.
const ROLE_LABEL: Record<string, string> = {
  feature: 'эпик',
  cut: 'разрез',
  plan: 'план',
  step: 'шаг',
  rework: 'доработка',
  review: 'ревью',
  qa: 'проверка',
  final: 'финал',
  merge: 'мерж',
  consult: 'консультация',
};

// Строка события (.ev, таблица §5.6): grid 44px 16px minmax(0,1fr), column-gap 8,
// padding 5 0, 12 textSecondary, align start. Время (mono 10,5 textMuted, paddingTop 1):
// сегодня «ЧЧ:ММ», иначе «dd.mm ЧЧ:ММ» (M3: дата вместе со временем). Аватар 16.
// «Кто» — 600 textHeading; без персоны — без прочерка (M3), только фраза.
function EventRowView({ row, onOpenChat, isMobile, now }: {
  row: EventRow;
  onOpenChat: (chatId: string) => void;
  isMobile: boolean;
  now?: number;
}) {
  usePersonasVersion();
  const persona = row.personaId ? getPersonaById(row.personaId) : undefined;
  const time = eventTimeLabel(row.at, now);
  const kindLabel = KIND_LABEL[row.kind] ?? row.kind;
  // Суффикс: роль + код шага (у step/rework), если есть.
  const roleLabel = row.role ? (ROLE_LABEL[row.role] ?? row.role) : null;
  const suffix = roleLabel != null ? `${roleLabel}${row.stepRef ? ` ${row.stepRef}` : ''}` : (row.stepRef ? row.stepRef : null);
  // Ссылка на чат: только если sessionId ≠ null (fail-closed).
  const hasChatLink = row.sessionId != null;
  return (
    <div
      data-testid={`feature-event-${row.at}-${row.kind}`}
      style={{
        display: 'grid', gridTemplateColumns: '44px 16px minmax(0,1fr)', columnGap: SP.sm,
        alignItems: 'start', fontSize: FS.sm, color: C.textSecondary, padding: '5px 0',
      }}
    >
      <span style={{ fontFamily: FONT.mono, fontSize: FS.xxs, color: C.textMuted, paddingTop: 1, whiteSpace: 'nowrap' }}>{time}</span>
      <span style={{ display: 'flex', justifyContent: 'center' }}>
        {persona
          ? <PersonaAvatar persona={persona} size={16} />
          : <span style={{ width: 16, height: 16, borderRadius: R.full, background: C.bgSelected, display: 'inline-block' }} />}
      </span>
      <span style={{ minWidth: 0 }}>
        <span style={{ overflowWrap: 'anywhere' }}>
          {persona && (
            <>
              <span style={{ fontWeight: 600, color: C.textHeading }}>{persona.name}</span>{' '}
            </>
          )}
          {kindLabel}{suffix ? ` · ${suffix}` : ''}
        </span>
        {row.reason && (
          <span style={{ display: 'block', fontSize: FS.xs, color: C.textMuted, overflowWrap: 'anywhere' }}>{row.reason}</span>
        )}
        {hasChatLink && (
          <button
            type="button"
            data-testid={`feature-event-chat-link-${row.sessionId}`}
            className={FOCUS_CLASS}
            onClick={() => onOpenChat(row.sessionId!)}
            style={{
              display: 'inline-flex', alignItems: 'center',
              border: 'none', background: 'transparent', cursor: 'pointer',
              fontFamily: FONT.sans, fontSize: FS.xs, color: C.accent,
              padding: isMobile ? '12px 0' : '4px 0', marginTop: 2,
            }}
          >
            открыть чат
          </button>
        )}
      </span>
    </div>
  );
}

// «Шагов нет» (макет .empty.sm, таблица §5.5): заголовок PT Serif 14/700 textHeading,
// текст 12,5 textSecondary, поля 14 10 6. Иконки в макете нет — полный EmptyState
// (круг 44) здесь неуместен.
function StepsEmpty() {
  return (
    <div data-testid="feature-steps-empty" style={{ padding: '14px 10px 6px', textAlign: 'center' }}>
      <div style={{ fontFamily: FONT.serif, fontSize: FS.md, fontWeight: 700, color: C.textHeading, marginBottom: 4 }}>
        Шагов пока нет
      </div>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, maxWidth: 300, margin: '0 auto' }}>
        Шаги появятся, когда Егор разложит план.
      </div>
    </div>
  );
}

// Тело страницы фичи (волна 2 «Обзор фич», шаг Ф2.5; макет §5.4, вариант А):
//   1280: две колонки grid minmax(0,1fr) 300px, gap 18, align start (M6):
//     левая:  Конвейер (PipelineFlow) + Шаги;
//     правая:  Чаты фичи (ключевой первым) + Задачи (счётчики) + События (лента + «Показать все»);
//   360: одна колонка, порядок (R-11): конвейер, чаты, шаги, задачи, события.
// Блоки — открытые (marginBottom 18), не карточки.
//
// Данные: run (FeatureRun) — из useFeaturePage; index (FeaturesIndex) — из useFeaturesIndex;
// feature (FeatureIndexFeature) — из индекса; events/eventsTotal — из useFeaturePage.
// «Без новых запросов»: чаты — только из индекса, названия/персоны — из
// chatNames/chatPersonas (список сессий проекта, уже загружен SessionList'ом).
// onOpenChat — открыть чат (механизм onBack, Ф2.3): строки чатов, шаги, ведущие в чат,
// и «открыть чат» в ленте. «Показать все» — перезапрос api.tasks.getFeature(fid, 'all').
export function FeaturePageBody({ run, index, feature, chatNames, chatPersonas, events, eventsTotal, onShowAll, onOpenChat, isMobile = false, now }: {
  run: FeatureRun;
  index: FeaturesIndex;
  feature: FeatureIndexFeature | null;
  chatNames?: Record<string, string>;
  chatPersonas?: Record<string, string>;
  events: FeatureEvent[];
  eventsTotal: number;
  onShowAll?: (() => void) | null;
  onOpenChat: (chatId: string) => void;
  isMobile?: boolean;
  // «Сейчас» в мс (стабильные тесты времени в ленте); не задано — Date.now().
  now?: number;
}) {
  const steps = stepRows(run, index, feature);
  const chats = featureChats(index, feature, chatNames, chatPersonas);
  const counters = taskCounters(run);

  // Конвейер: правая часть подписи — «принято/всего» + возвраты (макет: prog(f)).
  const pipelineRight = feature
    ? `${feature.stepsAccepted}/${feature.stepsTotal}${feature.loops > 0 ? ` ↺${feature.loops}` : ''}`
    : null;

  // Блоки (порядок в разметке: на 360 — конвейер, чаты, шаги, задачи, события (R-11);
  // на 1280 колонки: левая = конвейер+шаги, правая = чаты+задачи+события).
  const conveyorBlock = (
    <Block cap="Конвейер" right={pipelineRight ?? undefined}>
      <PipelineFlow run={run} />
    </Block>
  );
  const stepsBlock = (
    <Block cap="Шаги" right={steps.length > 0 ? String(steps.length) : undefined} rightTestId="feature-steps-cap">
      {steps.length === 0
        ? <StepsEmpty />
        : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
            {steps.map(s => <StepRowView key={s.stepRef} row={s} onOpenChat={onOpenChat} isMobile={isMobile} />)}
          </div>
        )}
    </Block>
  );
  const chatsBlock = (
    <Block cap={`Чаты фичи · ${chats.length}`}>
      {chats.length === 0
        ? <div style={{ fontSize: FS.sm, color: C.textMuted, padding: SP.xs }}>Чатов пока нет.</div>
        : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
            {chats.map(c => <ChatRow key={c.sessionId} entry={c} onOpenChat={onOpenChat} isMobile={isMobile} />)}
          </div>
        )}
    </Block>
  );
  const tasksBlock = (
    <Block cap="Задачи">
      <TaskCountersView c={counters} />
    </Block>
  );
  // Лента событий — отдельный компонент (FeaturePageEvents), одна реализация.
  const eventsBlock = (
    <FeaturePageEvents
      events={events}
      eventsTotal={eventsTotal}
      onShowAll={onShowAll}
      onOpenChat={onOpenChat}
      isMobile={isMobile}
      now={now}
    />
  );

  if (isMobile) {
    // Одна колонка, порядок R-11: конвейер, чаты, шаги, задачи, события.
    return (
      <div style={{ display: 'flex', flexDirection: 'column' }} data-testid="feature-page-body">
        {conveyorBlock}
        {chatsBlock}
        {stepsBlock}
        {tasksBlock}
        {eventsBlock}
      </div>
    );
  }
  // Две колонки (1280): minmax(0,1fr) 300px, gap 18, align start (M6).
  return (
    <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0,1fr) 300px', gap: 18, alignItems: 'start' }} data-testid="feature-page-body">
      <div style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
        {conveyorBlock}
        {stepsBlock}
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
        {chatsBlock}
        {tasksBlock}
        {eventsBlock}
      </div>
    </div>
  );
}

// Лента событий (отдельный экспорт — FeaturePage рендерит её в теле страницы).
// События + «Показать все» (ui/Button xs ghost, 360: minHeight 40), если лента обрезана.
// onOpenChat — «открыть чат» ведёт в чат (механизм onBack, Ф2.3).
export function FeaturePageEvents({ events, eventsTotal, onShowAll, onOpenChat, isMobile = false, now }: {
  events: FeatureEvent[];
  eventsTotal: number;
  onShowAll?: (() => void) | null;
  onOpenChat: (chatId: string) => void;
  isMobile?: boolean;
  now?: number;
}) {
  const rows = eventRows(events);
  const showAll = showAllNeeded(events, eventsTotal);
  return (
    <Block cap="События">
      {rows.length === 0
        ? <div style={{ fontSize: FS.sm, color: C.textSecondary, padding: '14px 10px 6px' }}>Событий пока нет.</div>
        : (
          <div style={{ display: 'flex', flexDirection: 'column' }}>
            {rows.map((r, i) => (
              <EventRowView
                key={`${r.at}|${r.kind}|${r.sessionId ?? ''}|${i}`}
                row={r}
                onOpenChat={onOpenChat}
                isMobile={isMobile}
                now={now}
              />
            ))}
            {showAll && onShowAll && (
              <span data-testid="feature-page-show-all" style={{ display: 'block', marginTop: SP.xs }}>
                <Button
                  size="xs"
                  variant="ghost"
                  fullWidth
                  onClick={onShowAll}
                  style={isMobile ? { minHeight: 40 } : undefined}
                >
                  Показать все ({eventsTotal})
                </Button>
              </span>
            )}
          </div>
        )}
    </Block>
  );
}

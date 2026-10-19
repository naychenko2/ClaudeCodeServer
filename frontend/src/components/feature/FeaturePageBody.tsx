import type { ReactNode } from 'react';
import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { getPersonaById, usePersonasVersion } from '../../lib/personas';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { PipelineFlow } from '../chat/feature/PipelineFlow';
import { dateOnly, hhmm } from '../chat/feature/featureTime';
import {
  featureChats, stepRows, stepStatusLabel, taskCounters, eventRows, showAllNeeded,
  type FeatureChatEntry, type StepRow, type TaskCounters, type EventRow,
} from './featurePageBody';
import type { FeatureIndexFeature, FeaturesIndex, FeatureEvent } from '../../types/featureIndex';
import type { FeatureRun } from '../../types/featureRun';

// Токены макета §5 (features-overview-2.html):
// .blk — блок с заголовком (cap) и телом; .cols — две колонки (grid 1fr 1fr, gap 16);
// .rw — строка (chat/step): grid 24px 1fr auto, gap 8, minHeight 32/40, R.md;
// .ev — строка события: grid 48px 20px 1fr, gap 8;
// .role — метка роли (key — accent, прочее — textMuted);
// .tag — тег (ok/run/loop/stop/mute);
// .cnts — счётчики (row, gap 6);
// .be — бейдж «нужно от бэка» (bgSelected, textMuted, R.max, 11/700).
// Все значения — из шкал FS/SP/R, где есть; константы — где в шкале нет.

// Единый focus-visible ring (клавиатура) для строк чатов и ссылок «открыть чат» —
// тот же паттерн, что у крошки (FeaturePageHeader): один инжект <style> на модуль.
const FOCUS_CLASS = 'cc-feature-page-body-focus';
if (typeof document !== 'undefined' && !document.getElementById('cc-feature-page-body-focus-style')) {
  const el = document.createElement('style');
  el.id = 'cc-feature-page-body-focus-style';
  el.textContent = `.${FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

// Заголовок блока (.blk .cap): FS.sm, 700, uppercase, letterSpacing .06em, C.textMuted.
function BlockCap({ children, right }: { children: ReactNode; right?: ReactNode }) {
  return (
    <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', marginBottom: SP.xs }}>
      <span style={{ fontSize: FS.xs, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: C.textMuted }}>
        {children}
      </span>
      {right}
    </div>
  );
}

// Обёртка блока (.blk): padding 12, R.lg, bgCard, border 1px borderLight.
function Block({ cap, right, children }: { cap: ReactNode; right?: ReactNode; children: ReactNode }) {
  return (
    <div style={{ padding: SP.sm, borderRadius: R.lg, background: C.bgCard, border: `1px solid ${C.borderLight}` }}>
      <BlockCap>{cap}{right}</BlockCap>
      {children}
    </div>
  );
}

// Бейдж «нужно от бэка» (.be): bgSelected, textMuted, R.max, 11/700, padding 0 7px, lineHeight 18px.
function BeBadge({ children }: { children: ReactNode }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', fontSize: FS.xs, fontWeight: 700,
      borderRadius: R.max, padding: '0 7px', lineHeight: '18px', whiteSpace: 'nowrap',
      background: C.bgSelected, color: C.textMuted,
    }}>
      {children}
    </span>
  );
}

// Метка роли чата (.role): key — accent, прочее — textMuted.
function RoleTag({ kind, stepRef }: { kind: string; stepRef?: string | null }) {
  if (kind === 'key') {
    return <span style={{ fontSize: FS.xs, fontWeight: 700, color: C.accent }}>ключевой</span>;
  }
  const label = stepRef ? `${kind} · ${stepRef}` : kind;
  return <span style={{ fontSize: FS.xs, color: C.textMuted }}>{label}</span>;
}

// Строка чата (.rw): grid 24px 1fr auto, gap 8, minHeight 32/40, R.md, border 1px transparent.
// Ключевой чат — border accent (подсветка).
// Строка — настоящая <button> (M3): клик ведёт в чат (механизм onBack, Ф2.3).
// Ловушки <button> (§0): рамка/фон/шрифт/выравнивание явно, focus — SHADOW.focus,
// touch-цель ≥ 40 на 360 (minHeight 40).
// Название (M4): из списка сессий проекта (chatNames, без новых запросов); нет — «Чат».
// GUID наружу не попадает.
function ChatRow({ entry, onOpenChat, isMobile }: {
  entry: FeatureChatEntry;
  onOpenChat: (chatId: string) => void;
  isMobile: boolean;
}) {
  const minH = isMobile ? 40 : 32;
  return (
    <button
      type="button"
      data-testid={`feature-chat-${entry.sessionId}`}
      className={FOCUS_CLASS}
      onClick={() => onOpenChat(entry.sessionId)}
      style={{
        display: 'grid', gridTemplateColumns: '24px 1fr auto', gap: SP.sm, alignItems: 'center',
        width: '100%', minHeight: minH, padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.md,
        border: entry.isKey ? `1px solid ${C.accent}` : '1px solid transparent',
        background: entry.isKey ? C.accentSoft : 'transparent',
        fontFamily: FONT.sans, fontSize: FS.sm, color: 'inherit', textAlign: 'left',
        cursor: 'pointer', boxSizing: 'border-box',
      }}
    >
      <span style={{ display: 'flex', justifyContent: 'center' }}>
        <span style={{ width: 20, height: 20, borderRadius: '50%', background: C.bgSelected, display: 'inline-block' }} />
      </span>
      <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontSize: FS.sm, color: C.textPrimary }}>
        {entry.name ?? 'Чат'}
      </span>
      <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexShrink: 0 }}>
        {entry.isArchived && (
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>архив</span>
        )}
        <RoleTag kind={entry.link.kind} stepRef={entry.link.stepRef} />
      </span>
    </button>
  );
}

// Строка шага (.rw): grid 24px 1fr auto, gap 8, minHeight 32/40, R.md.
// Слева: mono stepRef + название; справа: линия + тег исхода + круги.
function StepRowView({ row, isMobile }: { row: StepRow; isMobile: boolean }) {
  const minH = isMobile ? 40 : 32;
  // Тег исхода (M2): текст — готовая подпись бэка chain.status (одна точка правды,
  // те же слова, что «Конвейер»/PipelineFlow); бэк не отдал status (старый ответ) —
  // собственные слова бэка по outcome (stepStatusLabel), не трактовка фронта.
  // Тон: ok → successBg, run → bgSelected, loop → warningBg, stop → dangerBg, pend → bgSelected.
  const tag = ((): { text: string; bg: string; fg: string } => {
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
  })();
  return (
    <div
      data-testid={`feature-step-${row.stepRef}`}
      style={{
        display: 'grid', gridTemplateColumns: '24px 1fr auto', gap: SP.sm, alignItems: 'center',
        minHeight: minH, padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.md,
      }}
    >
      <span style={{ display: 'flex', justifyContent: 'center' }}>
        <span style={{ width: 14, height: 14, borderRadius: '50%', background: C.bgSelected, display: 'inline-block' }} />
      </span>
      <span style={{ minWidth: 0, display: 'flex', alignItems: 'baseline', gap: SP.xs, flexWrap: 'wrap' }}>
        <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, flexShrink: 0 }}>{row.stepRef}</span>
        <span style={{ fontSize: FS.sm, color: C.textPrimary, overflowWrap: 'anywhere' }}>{row.title}</span>
      </span>
      <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexShrink: 0, whiteSpace: 'nowrap' }}>
        {row.lane && <span style={{ fontSize: FS.xs, color: C.textMuted }}>{row.lane}</span>}
        {row.rounds > 1 && <span style={{ fontSize: FS.xs, color: C.textMuted }}>· круг {row.rounds}</span>}
        <span style={{
          fontSize: FS.xs, fontWeight: 700, borderRadius: R.max, padding: '0 7px', lineHeight: '18px',
          background: tag.bg, color: tag.fg,
        }}>{tag.text}</span>
      </span>
    </div>
  );
}

// Счётчики задач (.cnts): row, gap 6, теги (ok/run/mute).
function TaskCountersView({ c }: { c: TaskCounters }) {
  const tag = (text: string, bg: string, fg: string) => (
    <span style={{
      fontSize: FS.xs, fontWeight: 700, borderRadius: R.max, padding: '0 7px', lineHeight: '18px',
      whiteSpace: 'nowrap', background: bg, color: fg,
    }}>{text}</span>
  );
  return (
    <div style={{ display: 'flex', gap: SP.xxs, flexWrap: 'wrap', marginBottom: SP.xs }}>
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

// Строка события (.ev): grid 48px 20px 1fr, gap 8, fontSize FS.sm.
// Время (mono, muted) + аватар + текст: «{имя} · {вид}» + суффикс «{роль} {шаг}».
// «Открыть чат» — настоящая <button> (M3): ловушки §0 (рамка/фон/шрифт, focus SHADOW.focus,
// touch-цель ≥ 40 на 360); клик ведёт в чат (onOpenChat, механизм onBack, Ф2.3).
// Ссылка — только если sessionId ≠ null (fail-closed).
function EventRowView({ row, onOpenChat, isMobile }: {
  row: EventRow;
  onOpenChat: (chatId: string) => void;
  isMobile: boolean;
}) {
  usePersonasVersion();
  const persona = row.personaId ? getPersonaById(row.personaId) : undefined;
  const time = hhmm(row.at) ?? dateOnly(row.at) ?? '';
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
        display: 'grid', gridTemplateColumns: '48px 20px 1fr', gap: SP.sm, alignItems: 'start',
        fontSize: FS.sm, padding: `${SP.xxs}px 0`,
      }}
    >
      <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap' }}>{time}</span>
      <span style={{ display: 'flex', justifyContent: 'center' }}>
        {persona
          ? <PersonaAvatar persona={persona} size={20} />
          : <span style={{ width: 20, height: 20, borderRadius: '50%', background: C.bgSelected, display: 'inline-block' }} />}
      </span>
      <span style={{ minWidth: 0 }}>
        <span style={{ color: C.textPrimary, overflowWrap: 'anywhere' }}>
          {persona?.name ?? '—'} · {kindLabel}{suffix ? ` · ${suffix}` : ''}
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

// Тело страницы фичи (волна 2 «Обзор фич», шаг Ф2.5):
// две колонки (макет §5, вариант А):
//   левая:  Конвейер (PipelineFlow) + Шаги (stepChains);
//   правая: Чаты фичи (из индекса, ключевой первым) + Задачи (счётчики) + События (лента + «Показать все»).
//
// Данные: run (FeatureRun) — из useFeaturePage; index (FeaturesIndex) — из useFeaturesIndex;
// feature (FeatureIndexFeature) — из индекса. «Без новых запросов»: чаты — только из индекса,
// названия — из chatNames (список сессий проекта, уже загружен SessionList'ом).
// onOpenChat — открыть чат (механизм onBack, Ф2.3): строки чатов и «открыть чат» в ленте.
// «Показать все» — перезапрос api.tasks.getFeature(fid, 'all') (без лимита).
export function FeaturePageBody({ run, index, feature, chatNames, onOpenChat, isMobile = false }: {
  run: FeatureRun;
  index: FeaturesIndex;
  feature: FeatureIndexFeature | null;
  chatNames?: Record<string, string>;
  onOpenChat: (chatId: string) => void;
  isMobile?: boolean;
}) {
  const steps = stepRows(run);
  const chats = featureChats(index, feature, chatNames);
  const counters = taskCounters(run);

  const cols: React.CSSProperties = {
    display: 'grid',
    gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr',
    gap: SP.md,
  };

  return (
    <div style={cols} data-testid="feature-page-body">
      {/* Левая колонка: Конвейер + Шаги */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
        <Block cap="Конвейер">
          <PipelineFlow run={run} />
        </Block>
        <Block
          cap="Шаги"
          right={steps.length > 0 ? <span style={{ fontSize: FS.xs, color: C.textMuted }}>{steps.length}</span> : undefined}
        >
          {steps.length === 0 ? (
            <div style={{ fontSize: FS.sm, color: C.textMuted, padding: SP.xs }}>
              Шагов пока нет. Шаги появятся, когда Егор разложит план.
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
              {steps.map(s => <StepRowView key={s.stepRef} row={s} isMobile={isMobile} />)}
            </div>
          )}
        </Block>
      </div>

      {/* Правая колонка: Чаты + Задачи + События */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.md, minWidth: 0 }}>
        <Block
          cap={`Чаты фичи · ${chats.length}`}
        >
          {chats.length === 0 ? (
            <div style={{ fontSize: FS.sm, color: C.textMuted, padding: SP.xs }}>
              Чатов пока нет.
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
              {chats.map(c => <ChatRow key={c.sessionId} entry={c} onOpenChat={onOpenChat} isMobile={isMobile} />)}
            </div>
          )}
        </Block>

        {/* Кнопка «Все N задач в «Задачах»» убрана: переход в «Задачи» будет в Ф4.1 —
            до него это мёртвое действие (тот же аргумент, что у меню ⋯ шапки). */}
        <Block cap="Задачи">
          <TaskCountersView c={counters} />
        </Block>
      </div>
    </div>
  );
}

// Лента событий (отдельный блок, т.к. зависит от page.page.events, а не от run).
// События + «Показать все» (если events.length < eventsTotal).
// onOpenChat — «открыть чат» ведёт в чат (механизм onBack, Ф2.3).
export function FeaturePageEvents({ events, eventsTotal, onShowAll, onOpenChat, isMobile = false }: {
  events: FeatureEvent[];
  eventsTotal: number;
  onShowAll?: (() => void) | null;
  onOpenChat: (chatId: string) => void;
  isMobile?: boolean;
}) {
  const rows = eventRows(events);
  const showAll = showAllNeeded(events, eventsTotal);
  return (
    <Block
      cap="События"
      right={showAll ? <BeBadge>журнал: {eventsTotal - events.length} ещё</BeBadge> : undefined}
    >
      {rows.length === 0 ? (
        <div style={{ fontSize: FS.sm, color: C.textMuted, padding: SP.xs }}>
          Событий пока нет.
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column' }}>
          {rows.map((r, i) => (
            <EventRowView
              key={`${r.at}|${r.kind}|${r.sessionId ?? ''}|${i}`}
              row={r}
              onOpenChat={onOpenChat}
              isMobile={isMobile}
            />
          ))}
          {showAll && onShowAll && (
            <button
              data-testid="feature-page-show-all"
              onClick={onShowAll}
              style={{
                display: 'block', width: '100%', marginTop: SP.xs,
                border: `1px solid ${C.border}`, background: C.bgSelected,
                borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`,
                fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
                cursor: 'pointer', fontFamily: 'inherit',
                minHeight: isMobile ? 40 : 32,
              }}
            >
              Показать все ({eventsTotal})
            </button>
          )}
        </div>
      )}
    </Block>
  );
}

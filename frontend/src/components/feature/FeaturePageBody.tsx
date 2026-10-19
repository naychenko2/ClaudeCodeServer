import type { ReactNode } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { getPersonaById, usePersonasVersion } from '../../lib/personas';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { PipelineFlow } from '../chat/feature/PipelineFlow';
import { dateOnly, hhmm } from '../chat/feature/featureTime';
import {
  featureChats, stepRows, taskCounters, eventRows, showAllNeeded,
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
function ChatRow({ entry, isMobile }: { entry: FeatureChatEntry; isMobile: boolean }) {
  usePersonasVersion();
  const minH = isMobile ? 40 : 32;
  return (
    <div
      data-testid={`feature-chat-${entry.sessionId}`}
      style={{
        display: 'grid', gridTemplateColumns: '24px 1fr auto', gap: SP.sm, alignItems: 'center',
        minHeight: minH, padding: `${SP.xs}px ${SP.sm}px`, borderRadius: R.md,
        border: entry.isKey ? `1px solid ${C.accent}` : '1px solid transparent',
        background: entry.isKey ? C.accentSoft : 'transparent',
      }}
    >
      <span style={{ display: 'flex', justifyContent: 'center' }}>
        <span style={{ width: 20, height: 20, borderRadius: '50%', background: C.bgSelected, display: 'inline-block' }} />
      </span>
      <span style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontSize: FS.sm, color: C.textPrimary }}>
        {entry.sessionId}
      </span>
      <span style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexShrink: 0 }}>
        {entry.isArchived && (
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>архив</span>
        )}
        <RoleTag kind={entry.link.kind} stepRef={entry.link.stepRef} />
      </span>
    </div>
  );
}

// Строка шага (.rw): grid 24px 1fr auto, gap 8, minHeight 32/40, R.md.
// Слева: mono stepRef + название; справа: линия + тег исхода + круги.
function StepRowView({ row, isMobile }: { row: StepRow; isMobile: boolean }) {
  const minH = isMobile ? 40 : 32;
  // Тег исхода: ok → «принят» (successBg), run → «идёт» (bgSelected), loop → «доработка» (warningBg),
  // stop → «стоит» (dangerBg), pend → «впереди» (bgSelected, textMuted).
  const tag = ((): { text: string; bg: string; fg: string } => {
    switch (row.outcome) {
      case 'accepted': return { text: 'принят', bg: C.successBg, fg: C.successText };
      case 'running': return { text: 'идёт', bg: C.bgSelected, fg: C.textHeading };
      case 'returned':
      case 'accepted_with_remarks': return { text: 'доработка', bg: C.warningBg, fg: C.warningText };
      case 'withdrawn': return { text: 'снят', bg: C.bgSelected, fg: C.textMuted };
      case 'pending': return { text: 'впереди', bg: C.bgSelected, fg: C.textMuted };
      default: return { text: row.outcome, bg: C.bgSelected, fg: C.textSecondary };
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

// Строка события (.ev): grid 48px 20px 1fr, gap 8, fontSize FS.sm.
// Время (mono, muted) + аватар + текст. Ссылка на чат — если sessionId ≠ null.
function EventRowView({ row }: { row: EventRow }) {
  usePersonasVersion();
  const persona = row.personaId ? getPersonaById(row.personaId) : undefined;
  const time = hhmm(row.at) ?? dateOnly(row.at) ?? '';
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
          {persona?.name ?? '—'} · {row.kind}
        </span>
        {row.reason && (
          <span style={{ display: 'block', fontSize: FS.xs, color: C.textMuted, overflowWrap: 'anywhere' }}>{row.reason}</span>
        )}
        {hasChatLink && (
          <span
            data-testid={`feature-event-chat-link-${row.sessionId}`}
            style={{ display: 'inline-block', fontSize: FS.xs, color: C.accent, cursor: 'pointer', marginTop: 2 }}
          >
            открыть чат
          </span>
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
// feature (FeatureIndexFeature) — из индекса. «Без новых запросов»: чаты — только из индекса.
// «Показать все» — перезапрос api.tasks.getFeature(fid, 'all') (без лимита).
export function FeaturePageBody({ run, index, feature, isMobile = false }: {
  run: FeatureRun;
  index: FeaturesIndex;
  feature: FeatureIndexFeature | null;
  isMobile?: boolean;
}) {
  const steps = stepRows(run);
  const chats = featureChats(index, feature);
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
              {chats.map(c => <ChatRow key={c.sessionId} entry={c} isMobile={isMobile} />)}
            </div>
          )}
        </Block>

        <Block cap="Задачи">
          <TaskCountersView c={counters} />
          <button
            data-testid="feature-page-all-tasks"
            style={{
              display: 'flex', alignItems: 'center', gap: 5, border: 'none', background: 'transparent',
              cursor: 'pointer', fontSize: FS.sm, fontWeight: 600, color: C.accent,
              fontFamily: 'inherit', padding: 0, minHeight: isMobile ? 40 : 24,
            }}
          >
            Все {counters.total} задач фичи в «Задачах»
            <span aria-hidden="true">→</span>
          </button>
        </Block>
      </div>
    </div>
  );
}

// Лента событий (отдельный блок, т.к. зависит от page.page.events, а не от run).
// События + «Показать все» (если events.length < eventsTotal).
export function FeaturePageEvents({ events, eventsTotal, onShowAll, isMobile = false }: {
  events: FeatureEvent[];
  eventsTotal: number;
  onShowAll?: (() => void) | null;
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
          {rows.map((r, i) => <EventRowView key={i} row={r} />)}
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

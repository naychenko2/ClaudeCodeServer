import { useState } from 'react';
import type { ChatItem, PipelinePhaseItem, PipelinePhaseKey, Persona } from '../../types';
import { C, FONT, SHADOW } from '../../lib/design';
import { getPersonaById, personaLabel, usePersonasVersion } from '../../lib/personas';
import { PersonaAvatar } from '../../features/personas/PersonaAvatar';
import { MarkdownContent } from './MarkdownContent';

type PipelineItem = Extract<ChatItem, { kind: 'pipeline' }>;

const PHASE_TITLES: Record<PipelinePhaseKey, string> = {
  analysis: 'Анализ',
  plan: 'План',
  review: 'Ревью',
  execute: 'Исполнение',
};

// Порядок фаз для «дорожки» прогресса
const PHASE_ORDER: PipelinePhaseKey[] = ['analysis', 'plan', 'review', 'execute'];

function Spinner() {
  return <div className="tool-spinner" style={{ width: 11, height: 11, flexShrink: 0 }} />;
}

// Строка-аккордеон фазы: роль-исполнитель + заголовок фазы (с кругом доработки), текст по клику
function PhaseRow({ entry, defaultOpen }: { entry: PipelinePhaseItem; defaultOpen?: boolean }) {
  const [open, setOpen] = useState(!!defaultOpen);
  const persona = getPersonaById(entry.personaId);
  const label = persona ? personaLabel(persona) : 'Роль';
  const title = PHASE_TITLES[entry.phase] + (entry.round > 1 ? ` · доработка ${entry.round}` : '');
  return (
    <div style={{ borderTop: `1px solid ${C.divider}` }}>
      <button
        onClick={() => setOpen(o => !o)}
        style={{
          width: '100%', display: 'flex', alignItems: 'center', gap: 8, padding: '7px 12px',
          border: 'none', background: 'transparent', cursor: 'pointer', textAlign: 'left',
        }}
      >
        {persona
          ? <PersonaAvatar persona={persona as Persona} size={22} />
          : <span style={{ width: 22, height: 22, borderRadius: '50%', background: C.bgSelected, flexShrink: 0 }} />}
        <span style={{ flexShrink: 0, fontSize: 10.5, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.05em', fontFamily: FONT.sans }}>
          {title}
        </span>
        <span style={{ flex: 1, minWidth: 0, fontSize: 12.5, fontWeight: 600, color: C.textHeading, fontFamily: FONT.sans, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {label}
        </span>
        <svg width="11" height="11" viewBox="0 0 24 24" fill="none" stroke={C.textMuted} strokeWidth="2.5"
          strokeLinecap="round" strokeLinejoin="round"
          style={{ flexShrink: 0, transform: open ? 'rotate(180deg)' : 'none', transition: 'transform 0.15s' }}>
          <path d="M6 9l6 6 6-6" />
        </svg>
      </button>
      {open && (
        <div style={{ padding: '0 12px 10px 42px', fontSize: 13 }}>
          <MarkdownContent text={entry.text} />
        </div>
      )}
    </div>
  );
}

// Карточка конвейера пантеона в ленте: задача, фазы-аккордеон (анализ → план → ревью →
// исполнение) с live-спиннером текущей фазы. Исполнение продолжается обычным ходом ниже.
export function PipelineView({ item, onCancel }: {
  item: PipelineItem;
  // Отменить идущий конвейер (POST /chats/{id}/pipeline/cancel)
  onCancel?: () => void;
}) {
  usePersonasVersion(); // аватары/подписи обновятся после загрузки стора персон
  const isRunning = item.status !== 'done' && item.status !== 'error';
  // Последняя фаза каждого типа уже показана; спиннер — для фазы, что сейчас идёт
  const runningTitle = item.runningPhase && PHASE_ORDER.includes(item.runningPhase as PipelinePhaseKey)
    ? PHASE_TITLES[item.runningPhase as PipelinePhaseKey]
    : null;
  const lastIdx = item.phases.length - 1;

  return (
    <div style={{
      border: `1px solid ${C.borderLight}`, borderRadius: 12, background: C.bgWhite,
      overflow: 'hidden', boxShadow: SHADOW.card,
    }}>
      {/* Шапка: иконка + «Конвейер» + задача */}
      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 8, padding: '10px 13px', borderBottom: `1px solid ${C.divider}` }}>
        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke={C.accent} strokeWidth="2"
          strokeLinecap="round" strokeLinejoin="round" style={{ flexShrink: 0, marginTop: 2 }}>
          <path d="M3 12h4l3 8 4-16 3 8h4" />
        </svg>
        <div style={{ flex: 1, minWidth: 0 }}>
          <span style={{ fontFamily: FONT.serif, fontSize: 14, fontWeight: 700, color: C.textHeading }}>Конвейер</span>
          {item.task && (
            <div style={{ fontSize: 12.5, color: C.textSecondary, marginTop: 2, lineHeight: 1.4 }}>{item.task}</div>
          )}
        </div>
        {isRunning && <Spinner />}
        {isRunning && onCancel && (
          <button onClick={onCancel} style={{
            flexShrink: 0, fontSize: 11.5, padding: '3px 9px', borderRadius: 6,
            border: `1px solid ${C.border}`, background: C.bgWhite, cursor: 'pointer',
            color: C.textSecondary, fontFamily: FONT.sans,
          }}>
            Отменить
          </button>
        )}
      </div>

      {/* Завершённые фазы (последняя раскрыта по умолчанию) */}
      {item.phases.map((p, i) => (
        <PhaseRow key={`${p.phase}-${p.round}-${i}`} entry={p} defaultOpen={i === lastIdx && p.phase !== 'execute'} />
      ))}

      {/* Текущая идущая фаза — строка со спиннером */}
      {isRunning && runningTitle && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px', borderTop: `1px solid ${C.divider}` }}>
          <span style={{ flexShrink: 0, fontSize: 10.5, fontWeight: 700, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.05em', fontFamily: FONT.sans }}>
            {runningTitle}
          </span>
          <span style={{ flex: 1, fontSize: 12.5, color: C.textSecondary, fontFamily: FONT.sans }}>идёт…</span>
          <Spinner />
        </div>
      )}

      {/* Конвейер прерван / план не прошёл ревью */}
      {item.status === 'error' && (
        <div style={{
          padding: '8px 12px', borderTop: `1px solid ${C.divider}`,
          background: C.dangerBg, color: C.dangerText, fontSize: 12.5,
        }}>
          {item.error ?? 'Конвейер прерван'}
        </div>
      )}
    </div>
  );
}

import { useState } from 'react';
import type { Session, Persona } from '../../types';
import { C, R, FONT } from '../../lib/design';
import { NewChatSetup } from './NewChatSetup';
import { useAssistantName } from './contexts';
import { personaLabel, personaTitleLines } from '../../lib/personas';
import { PersonaAvatar } from '../../features/agents/PersonaAvatar';

// Чипы-подсказки для empty state проектного чата
const HINTS = ['Объясни структуру проекта', 'Найди и почини падающие тесты'];

// Чипы-подсказки для чата вне проекта — универсальный ассистент (тексты, поиск, генерация медиа)
const CHAT_HINTS = ['Найди информацию в интернете', 'Напиши пост для соцсетей', 'Сгенерируй картинку'];

// Empty state пустого чата: приветствие/чипы-подсказки; для проекта без CLAUDE.md — CTA /init.
// Внизу — настройка будущего чата (модель + усилие рассуждения), пока не отправлено первое сообщение.
export function ChatEmptyState({ hasProject, hasCLAUDEmd, onHint, session, onSessionUpdated, isMobile, personas, selectedPersonaId, onPickPersona }: {
  hasProject: boolean;
  hasCLAUDEmd: boolean | null;
  onHint: (hint: string) => void;
  session?: Session;
  onSessionUpdated?: (s: Session) => void;
  isMobile?: boolean;
  // Доступные персоны (олицетворённые агенты) — ряд «Поговорить с…» для пустого чата
  personas?: Persona[];
  selectedPersonaId?: string;
  onPickPersona?: (p: Persona) => void;
}) {
  const asstName = useAssistantName();
  return (
          <div style={{
            flex: 1, display: 'flex', flexDirection: 'column',
            alignItems: 'center', justifyContent: 'center',
            gap: 12, paddingTop: 40,
          }}>
            {/* Логотип */}
            <img src="/favicon.svg" alt="" width={46} height={46} style={{ display: 'block' }} />

            {!hasProject ? (
              <>
                {/* Приветствие чата вне проекта — general-purpose ассистент */}
                <div style={{
                  fontFamily: '"PT Serif", Georgia, serif',
                  fontWeight: 500, fontSize: 20, color: C.textHeading, letterSpacing: '-0.01em',
                }}>
                  Чем помочь?
                </div>

                <div style={{ fontSize: 13, color: C.textMuted, textAlign: 'center', maxWidth: 320 }}>
                  Спросите что угодно — тексты и идеи, поиск в интернете, генерация картинок
                </div>

                {/* Чипы */}
                <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'center', marginTop: 4 }}>
                  {CHAT_HINTS.map(hint => (
                    <button
                      key={hint}
                      onClick={() => onHint(hint)}
                      style={{
                        background: C.bgWhite, border: `1px solid ${C.borderLight}`,
                        borderRadius: 10, padding: '9px 12px',
                        fontSize: 13, color: C.textPrimary, cursor: 'pointer',
                        fontFamily: 'inherit',
                      }}
                      onMouseEnter={e => (e.currentTarget.style.background = C.accentLight)}
                      onMouseLeave={e => (e.currentTarget.style.background = C.bgWhite)}
                    >
                      {hint}
                    </button>
                  ))}
                </div>
              </>
            ) : hasCLAUDEmd === false ? (
              <>
                {/* Заголовок */}
                <div style={{
                  fontFamily: '"PT Serif", Georgia, serif',
                  fontWeight: 500, fontSize: 20, color: C.textHeading, letterSpacing: '-0.01em',
                }}>
                  Новый проект
                </div>

                {/* Подзаголовок */}
                <div style={{ fontSize: 13, color: C.textMuted, textAlign: 'center', maxWidth: 260 }}>
                  Запустите /init, чтобы {asstName} изучил проект и создал CLAUDE.md
                </div>

                {/* Кнопка CTA */}
                <button
                  onClick={() => onHint('/init')}
                  style={{
                    marginTop: 4,
                    background: C.accent, border: 'none',
                    borderRadius: 10, padding: '10px 20px',
                    fontSize: 13, color: C.onAccent, cursor: 'pointer',
                    fontFamily: 'inherit', fontWeight: 500,
                  }}
                  onMouseEnter={e => (e.currentTarget.style.opacity = '0.85')}
                  onMouseLeave={e => (e.currentTarget.style.opacity = '1')}
                >
                  Инициализировать проект
                </button>
              </>
            ) : (
              <>
                {/* Заголовок */}
                <div style={{
                  fontFamily: '"PT Serif", Georgia, serif',
                  fontWeight: 500, fontSize: 20, color: C.textHeading, letterSpacing: '-0.01em',
                }}>
                  Чем помочь?
                </div>

                {/* Подзаголовок */}
                <div style={{ fontSize: 13, color: C.textMuted, textAlign: 'center' }}>
                  Опишите задачу или начните с подсказки
                </div>

                {/* Чипы */}
                <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'center', marginTop: 4 }}>
                  {HINTS.map(hint => (
                    <button
                      key={hint}
                      onClick={() => onHint(hint)}
                      style={{
                        background: C.bgWhite, border: `1px solid ${C.borderLight}`,
                        borderRadius: 10, padding: '9px 12px',
                        fontSize: 13, color: C.textPrimary, cursor: 'pointer',
                        fontFamily: 'inherit',
                      }}
                      onMouseEnter={e => (e.currentTarget.style.background = C.accentLight)}
                      onMouseLeave={e => (e.currentTarget.style.background = C.bgWhite)}
                    >
                      {hint}
                    </button>
                  ))}
                </div>
              </>
            )}

            {/* Ряд агентов «Поговорить с…» — назначить персону текущему пустому чату.
                В проекте команда проекта видна сразу, глобальные свёрнуты за кнопкой;
                если проектных агентов нет — глобальные показываются сразу. */}
            {personas && personas.length > 0 && onPickPersona && (
              <PersonaPills
                personas={personas}
                hasProject={hasProject}
                selectedPersonaId={selectedPersonaId}
                onPick={onPickPersona}
              />
            )}

            {/* Настройка чата — модель и усилие рассуждения (до первого сообщения) */}
            {session && (
              <NewChatSetup session={session} onSessionUpdated={onSessionUpdated} isMobile={isMobile} />
            )}
          </div>
  );
}

// Одна пилюля-аватар агента (роль над именем)
function PersonaPill({ p, active, onPick }: { p: Persona; active: boolean; onPick: (p: Persona) => void }) {
  return (
    <button
      onClick={() => onPick(p)}
      title={`Поговорить с «${personaLabel(p)}»`}
      style={{
        display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 4,
        border: 'none', background: 'none', cursor: 'pointer', padding: 2, width: 64,
      }}
    >
      <span style={{ borderRadius: R.full, padding: 2, border: `2px solid ${active ? C.accent : 'transparent'}` }}>
        <PersonaAvatar persona={p} size={44} />
      </span>
      <span style={{
        fontFamily: FONT.sans, fontSize: 11.5, color: C.textSecondary,
        maxWidth: 64, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}>
        {personaTitleLines(p).primary}
      </span>
      {personaTitleLines(p).secondary && (
        <span style={{
          fontFamily: FONT.sans, fontSize: 10.5, color: C.textMuted,
          maxWidth: 64, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {personaTitleLines(p).secondary}
        </span>
      )}
    </button>
  );
}

// Ряд «Поговорить с…»: в проекте команда видна сразу, глобальные — за кнопкой-раскрывашкой.
// Без проектных агентов (или вне проекта) глобальные показываются сразу.
function PersonaPills({ personas, hasProject, selectedPersonaId, onPick }: {
  personas: Persona[];
  hasProject: boolean;
  selectedPersonaId?: string;
  onPick: (p: Persona) => void;
}) {
  const [showGlobals, setShowGlobals] = useState(false);
  const projectAgents = personas.filter(p => p.scope === 'project');
  const globalAgents = personas.filter(p => p.scope === 'global');
  // Скрываем глобальных только в проекте и только когда есть своя команда
  const collapseGlobals = hasProject && projectAgents.length > 0 && !showGlobals;
  const visible = collapseGlobals ? projectAgents : [...projectAgents, ...globalAgents];

  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 8, marginTop: 6 }}>
      <div style={{ fontSize: 11.5, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: 0.4 }}>
        Поговорить с…
      </div>
      <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', justifyContent: 'center', maxWidth: 480 }}>
        {visible.map(p => (
          <PersonaPill key={p.id} p={p} active={p.id === selectedPersonaId} onPick={onPick} />
        ))}
        {collapseGlobals && globalAgents.length > 0 && (
          <button
            onClick={() => setShowGlobals(true)}
            title="Показать глобальных агентов"
            style={{
              display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 4,
              border: 'none', background: 'none', cursor: 'pointer', padding: 2, width: 64,
            }}
          >
            <span style={{
              width: 44, height: 44, borderRadius: R.full, margin: 2,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              background: C.bgWhite, border: `1px dashed ${C.border}`,
              fontFamily: FONT.sans, fontSize: 13, fontWeight: 600, color: C.textMuted,
            }}>
              +{globalAgents.length}
            </span>
            <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted }}>ещё</span>
          </button>
        )}
      </div>
    </div>
  );
}

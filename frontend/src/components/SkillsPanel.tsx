import { useState, useEffect } from 'react';
import type { AgentInfo, SkillInfo, SkillsData } from '../types';
import { C, R, FONT } from '../lib/design';
import { api } from '../lib/api';
import { agentDotColor } from './AgentSelector';

interface Props {
  projectId: string;
}

export function SkillsPanel({ projectId }: Props) {
  const [data, setData] = useState<SkillsData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setLoading(true);
    api.skills.list(projectId)
      .then(d => { setData(d); setError(null); })
      .catch(() => setError('Не удалось загрузить скиллы и агенты'))
      .finally(() => setLoading(false));
  }, [projectId]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {/* Тело */}
      <div style={{ flex: 1, overflowY: 'auto', padding: '8px 0' }}>
        {loading && <LoadingSkeleton />}

        {!loading && error && (
          <div style={{
            margin: '20px 16px',
            padding: '12px 14px',
            background: C.dangerBg,
            border: `1px solid ${C.dangerBorder}`,
            borderRadius: R.lg,
            fontSize: 13,
            color: C.dangerText,
            fontFamily: FONT.sans,
          }}>
            {error}
          </div>
        )}

        {!loading && !error && data && (
          <>
            {/* Глобальные скиллы */}
            {data.skills.length > 0 && (
              <Section label="Глобальные скиллы">
                {data.skills.map(skill => (
                  <SkillCard key={skill.name} skill={skill} />
                ))}
              </Section>
            )}

            {/* Агенты проекта */}
            {data.agents.length > 0 && (
              <Section label="Агенты проекта">
                {data.agents.map(agent => (
                  <AgentCard key={agent.fileName} agent={agent} />
                ))}
              </Section>
            )}

            {/* Пустое состояние */}
            {data.skills.length === 0 && data.agents.length === 0 && (
              <EmptyState />
            )}
          </>
        )}
      </div>
    </div>
  );
}

// --- Секция с заголовком ---

function Section({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div style={{ marginBottom: 8 }}>
      <div style={{
        padding: '6px 16px 4px',
        fontSize: 10.5,
        fontWeight: 700,
        color: C.textMuted,
        textTransform: 'uppercase',
        letterSpacing: '0.07em',
        fontFamily: FONT.sans,
      }}>
        {label}
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 2, padding: '0 8px' }}>
        {children}
      </div>
    </div>
  );
}

// --- Карточка скилла ---

function SkillCard({ skill }: { skill: SkillInfo }) {
  return (
    <div style={{
      padding: '9px 12px',
      borderRadius: R.lg,
      background: 'transparent',
      display: 'flex',
      flexDirection: 'column',
      gap: 3,
    }}>
      {/* Команда */}
      <span style={{
        fontFamily: FONT.mono,
        fontSize: 12.5,
        fontWeight: 600,
        color: C.accent,
      }}>
        /{skill.name}
        {skill.argumentHint && (
          <span style={{ color: C.textMuted, fontWeight: 400 }}> {skill.argumentHint}</span>
        )}
      </span>
      {/* Описание */}
      {skill.description && (
        <span style={{
          fontSize: 12,
          color: C.textSecondary,
          lineHeight: 1.5,
          fontFamily: FONT.sans,
        }}>
          {skill.description}
        </span>
      )}
    </div>
  );
}

// --- Карточка агента ---

function AgentCard({ agent }: { agent: AgentInfo }) {
  const dot = agentDotColor(agent.color);

  return (
    <div style={{
      padding: '10px 12px',
      borderRadius: R.lg,
      background: 'transparent',
      display: 'flex',
      flexDirection: 'column',
      gap: 4,
    }}>
      {/* Шапка: dot + имя */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <span style={{
          width: 8,
          height: 8,
          borderRadius: '50%',
          background: dot,
          flexShrink: 0,
          boxShadow: `0 0 0 2px ${dot}28`,
        }} />
        <span style={{
          fontSize: 13,
          fontWeight: 700,
          color: C.textHeading,
          fontFamily: FONT.sans,
        }}>
          {agent.name}
        </span>
      </div>

      {/* Описание */}
      {agent.description && (
        <span style={{
          fontSize: 12,
          color: C.textSecondary,
          lineHeight: 1.5,
          fontFamily: FONT.sans,
          paddingLeft: 16, // выравнивание под имя (8px dot + 8px gap)
        }}>
          {agent.description}
        </span>
      )}

      {/* Инструменты */}
      {agent.tools && agent.tools.length > 0 && (
        <div style={{
          paddingLeft: 16,
          display: 'flex',
          flexWrap: 'wrap',
          gap: 4,
          marginTop: 2,
        }}>
          {agent.tools.map(tool => (
            <span key={tool} style={{
              fontSize: 10.5,
              fontFamily: FONT.mono,
              color: C.textMuted,
              background: C.bgInset,
              border: `1px solid ${C.borderLight}`,
              borderRadius: R.sm,
              padding: '1px 6px',
              lineHeight: 1.6,
            }}>
              {tool}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

// --- Скелетон загрузки ---

function LoadingSkeleton() {
  const bar = (w: string, h = 11, mb = 0) => (
    <div style={{
      width: w,
      height: h,
      borderRadius: R.sm,
      background: C.bgInset,
      marginBottom: mb,
    }} />
  );

  return (
    <div style={{ padding: '8px 8px', display: 'flex', flexDirection: 'column', gap: 2 }}>
      {/* Заголовок секции */}
      <div style={{ padding: '6px 8px 4px' }}>{bar('60px', 9)}</div>

      {[1, 2, 3].map(i => (
        <div key={i} style={{
          padding: '10px 12px',
          borderRadius: R.lg,
          display: 'flex',
          flexDirection: 'column',
          gap: 6,
        }}>
          {bar('110px', 11)}
          {bar('75%', 9)}
        </div>
      ))}

      {/* Вторая секция */}
      <div style={{ padding: '14px 8px 4px' }}>{bar('80px', 9)}</div>

      {[1, 2].map(i => (
        <div key={i} style={{
          padding: '10px 12px',
          display: 'flex',
          alignItems: 'center',
          gap: 8,
        }}>
          <div style={{ width: 8, height: 8, borderRadius: '50%', background: C.bgInset, flexShrink: 0 }} />
          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', gap: 5 }}>
            {bar('90px', 11)}
            {bar('60%', 9)}
          </div>
        </div>
      ))}
    </div>
  );
}

// --- Пустое состояние ---

function EmptyState() {
  return (
    <div style={{
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'center',
      justifyContent: 'center',
      padding: '40px 24px',
      gap: 8,
      textAlign: 'center',
    }}>
      <span style={{ fontSize: 28, lineHeight: 1 }}>✦</span>
      <span style={{ fontSize: 13, fontWeight: 600, color: C.textSecondary, fontFamily: FONT.sans }}>
        Скиллов и агентов пока нет
      </span>
      <span style={{ fontSize: 12, color: C.textMuted, fontFamily: FONT.sans, maxWidth: 240, lineHeight: 1.5 }}>
        Скиллы Claude Code добавляются в&nbsp;глобальную конфигурацию, агенты — в&nbsp;папку&nbsp;.claude/agents проекта
      </span>
    </div>
  );
}


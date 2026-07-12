import { useState, useEffect, useCallback } from 'react';
import { Search, Trash2 } from 'lucide-react';
import type { AgentInfo, SkillInfo, SkillsData } from '../types';
import { C, R, FONT } from '../lib/design';
import { api } from '../lib/api';
import { agentDotColor } from './AgentSelector';
import { SkillSearchDialog } from './SkillSearchDialog';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';

interface Props {
  projectId: string;
}

export function SkillsPanel({ projectId }: Props) {
  const [data, setData] = useState<SkillsData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showSearch, setShowSearch] = useState(false);

  const load = useCallback(() => {
    setLoading(true);
    return api.skills.list(projectId)
      .then(d => { setData(d); setError(null); })
      .catch(() => setError('Не удалось загрузить скиллы и агенты'))
      .finally(() => setLoading(false));
  }, [projectId]);

  useEffect(() => { void load(); }, [load]);

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {/* Шапка с кнопкой поиска навыка */}
      <div style={{
        display: 'flex', justifyContent: 'flex-end', padding: '8px 12px 4px', flexShrink: 0,
      }}>
        <button
          onClick={() => setShowSearch(true)}
          onMouseEnter={e => { e.currentTarget.style.borderColor = C.accent; e.currentTarget.style.color = C.accent; }}
          onMouseLeave={e => { e.currentTarget.style.borderColor = C.border; e.currentTarget.style.color = C.textSecondary; }}
          style={{
            display: 'inline-flex', alignItems: 'center', gap: 6,
            border: `1px solid ${C.border}`, background: C.bgWhite, color: C.textSecondary,
            borderRadius: R.lg, padding: '6px 12px', fontSize: 12.5, fontWeight: 600,
            cursor: 'pointer', fontFamily: FONT.sans, transition: 'border-color 0.15s, color 0.15s',
          }}
        >
          <Search size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          Найти навык
        </button>
      </div>

      {showSearch && (
        <SkillSearchDialog
          projectId={projectId}
          onClose={() => setShowSearch(false)}
          onInstalled={() => void load()}
        />
      )}

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
            {/* Скиллы проекта (.claude/skills) — с удалением */}
            {data.projectSkills.length > 0 && (
              <Section label="Скиллы проекта">
                {data.projectSkills.map(skill => (
                  <SkillCard
                    key={skill.name}
                    skill={skill}
                    onRemove={async () => {
                      if (!confirm(`Удалить навык «${skill.name}» из проекта?`)) return;
                      try { await api.skills.uninstall(skill.name, 'project', projectId); await load(); }
                      catch { alert('Не удалось удалить навык'); }
                    }}
                  />
                ))}
              </Section>
            )}

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
            {data.skills.length === 0 && data.projectSkills.length === 0 && data.agents.length === 0 && (
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

function SkillCard({ skill, onRemove }: { skill: SkillInfo; onRemove?: () => void }) {
  const [hover, setHover] = useState(false);
  return (
    <div
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        padding: '9px 12px',
        borderRadius: R.lg,
        background: 'transparent',
        display: 'flex',
        flexDirection: 'column',
        gap: 3,
        position: 'relative',
      }}
    >
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
      {onRemove && hover && (
        <button
          onClick={onRemove}
          aria-label="Удалить навык"
          title="Удалить навык из проекта"
          style={{
            position: 'absolute', top: 6, right: 8, width: 26, height: 26, border: 'none',
            background: 'transparent', borderRadius: R.md, cursor: 'pointer',
            color: C.textMuted, display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}
        >
          <Trash2 size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </button>
      )}
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


// Вкладка «Проактивность» персоны: правила «событие → действие». Персона сама реагирует
// на события (таймер, файлы, заметки, коммиты, смена статуса задач, @упоминания). Правила
// живут в Persona.AutomationRules; мутации — мгновенно через REST, список обновляется по
// realtime personas_changed (как привязки/память).

import { useState } from 'react';
import { Plus, Zap, FlaskConical, Trash2, Pencil } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { Persona, PersonaAutomationRule, Project } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { api } from '../../lib/api';
import { Toggle } from '../../components/ui';
import { AutomationRuleDialog } from './AutomationRuleDialog';

export function PersonaAutomationPanel({ persona, projects, accent, isMobile }: {
  persona: Persona; projects: Project[]; accent: string; isMobile?: boolean;
}) {
  const rules = persona.automationRules ?? [];
  const [editing, setEditing] = useState<PersonaAutomationRule | null>(null);
  const [creating, setCreating] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);

  const enabledCount = rules.filter(r => r.enabled).length;

  async function toggle(rule: PersonaAutomationRule, enabled: boolean) {
    try { await api.personas.updateAutomation(persona.id, rule.id, { enabled }); }
    catch { /* realtime вернёт актуальное состояние */ }
  }
  async function test(rule: PersonaAutomationRule) {
    setBusyId(rule.id);
    try { await api.personas.testAutomation(persona.id, rule.id); }
    catch { /* молча: ход пойдёт в фоне */ }
    finally { setBusyId(null); }
  }
  async function remove(rule: PersonaAutomationRule) {
    if (!window.confirm(`Удалить правило «${rule.name || 'без названия'}»?`)) return;
    try { await api.personas.removeAutomation(persona.id, rule.id); } catch { /* noop */ }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      <div style={{
        display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12,
        padding: isMobile ? '14px 16px 10px' : '18px 22px 12px', flexShrink: 0,
      }}>
        <div style={{ fontFamily: FONT.sans, fontSize: 13, color: C.textSecondary }}>
          {rules.length > 0
            ? <>Правил — <b style={{ color: C.textHeading }}>{rules.length}</b>
              {enabledCount > 0 && <>, <b style={{ color: accent }}>{enabledCount}</b> активно</>}</>
            : 'Правил автоматизации нет'}
        </div>
        <button onClick={() => setCreating(true)} style={addBtn(accent)}>
          <Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
          Добавить правило
        </button>
      </div>

      <div style={{ flex: 1, overflowY: 'auto', padding: isMobile ? '4px 16px 20px' : '4px 22px 24px' }}>
        {rules.length === 0 ? (
          <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 14, padding: '50px 24px', textAlign: 'center' }}>
            <Zap size={46} strokeWidth={1.5} color={C.dashed} style={{ flexShrink: 0 }} />
            <div style={{ maxWidth: 340, fontFamily: FONT.sans, fontSize: 13.5, color: C.textMuted, lineHeight: 1.5 }}>
              Персона будет сама реагировать на события: таймер, изменения файлов и заметок, новые
              коммиты, смену статуса задач и @упоминания. По умолчанию она сначала решает, стоит ли
              вмешиваться, и пишет в отдельный чат правила. Создайте первое правило.
            </div>
          </div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            {rules.map(rule => (
              <div key={rule.id} style={cardStyle(rule.enabled)}>
                <div style={{ display: 'flex', alignItems: 'flex-start', gap: 10 }}>
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                      <span style={{ fontFamily: FONT.sans, fontSize: 14, fontWeight: 600, color: C.textHeading }}>
                        {rule.name || 'Без названия'}
                      </span>
                      <span style={typeBadge(accent)}>{triggerLabel(rule)}</span>
                    </div>
                    <div style={{ marginTop: 4, fontFamily: FONT.sans, fontSize: 12, color: C.textMuted, lineHeight: 1.45 }}>
                      {actionLabel(rule)}
                    </div>
                  </div>
                  <Toggle checked={rule.enabled} onChange={v => toggle(rule, v)} />
                </div>
                <div style={{ display: 'flex', gap: 6, marginTop: 10, justifyContent: 'flex-end' }}>
                  <button onClick={() => test(rule)} disabled={busyId === rule.id} style={miniBtn}>
                    <FlaskConical size={13} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                    {busyId === rule.id ? 'Запускаю…' : 'Проверить'}
                  </button>
                  <button onClick={() => setEditing(rule)} title="Изменить" style={miniBtn}>
                    <Pencil size={13} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                  </button>
                  <button onClick={() => remove(rule)} title="Удалить" style={miniBtnDanger}>
                    <Trash2 size={13} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
                  </button>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {(creating || editing) && (
        <AutomationRuleDialog
          persona={persona}
          projects={projects}
          rule={editing}
          onClose={() => { setCreating(false); setEditing(null); }}
        />
      )}
    </div>
  );
}

// ─── Подписи для карточки ─────────────────────────────────────────────────────

function argsOf(rule: PersonaAutomationRule): Record<string, any> {
  return ((rule.trigger.args?.schedule as Record<string, any>) ?? rule.trigger.args ?? {}) as Record<string, any>;
}

export function triggerLabel(rule: PersonaAutomationRule): string {
  switch (rule.trigger.type) {
    case 'timer': {
      const a = argsOf(rule);
      if (a.intervalMinutes) return `таймер · каждые ${a.intervalMinutes} мин`;
      const type = a.type === 'weekdays' ? 'по будням' : a.type === 'weekly' ? 'по дням нед.' : 'ежедневно';
      return `таймер · ${type}${a.time ? ` ${a.time}` : ''}`;
    }
    case 'file': return `файлы${rule.trigger.args?.glob ? `: ${rule.trigger.args.glob}` : ''}`;
    case 'note': return 'заметки';
    case 'gitCommit': return 'новые коммиты';
    case 'taskStatus': return 'смена статуса задачи';
    case 'mention': return '@упоминание';
    default: return rule.trigger.type;
  }
}

function actionLabel(rule: PersonaAutomationRule): string {
  const w = rule.action.weight === 'work' ? 'Разобраться (полный ход)' : 'Сообщить в чат';
  const instr = rule.action.instruction ? ` — ${rule.action.instruction.slice(0, 90)}` : '';
  return `${w}${instr}`;
}

// ─── Стили ──────────────────────────────────────────────────────────────────────

function addBtn(accent: string): React.CSSProperties {
  return {
    display: 'inline-flex', alignItems: 'center', gap: 7, flexShrink: 0,
    border: `1px solid ${accent}`, background: C.accentLight, color: accent,
    borderRadius: R.lg, padding: '8px 14px', cursor: 'pointer',
    fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
  };
}

function cardStyle(enabled: boolean): React.CSSProperties {
  return {
    background: C.bgWhite, borderRadius: R.lg,
    border: `1px solid ${C.border}`, padding: '12px 14px',
    opacity: enabled ? 1 : 0.65,
  };
}

function typeBadge(accent: string): React.CSSProperties {
  return {
    fontSize: 10.5, fontWeight: 600, letterSpacing: '0.02em', padding: '1px 7px',
    borderRadius: R.pill, background: `${accent}1F`, color: accent, whiteSpace: 'nowrap',
  };
}

const miniBtn: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: 5, border: `1px solid ${C.border}`,
  background: C.bgPanel, color: C.textSecondary, borderRadius: R.md, padding: '5px 9px',
  cursor: 'pointer', fontFamily: FONT.sans, fontSize: 12, fontWeight: 500,
};

const miniBtnDanger: React.CSSProperties = {
  ...miniBtn, color: C.danger, borderColor: `${C.danger}55`,
};

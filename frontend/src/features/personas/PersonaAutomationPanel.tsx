// Вкладка «Проактивность» персоны: правила «событие → действие». Персона сама
// реагирует на события (таймер, файлы, заметки, коммиты, смена статуса задач,
// @упоминания). Правила живут в Persona.AutomationRules; мутации — мгновенно
// через REST, список обновляется по realtime personas_changed (как привязки/память).
//
// Вёрстка повторяет карточку привязки 1-в-1 (PersonaBindingsPanel): тот же
// контейнер 680 по центру, плоский бордер без тени, hover→accent, однорядный
// лейаут (круглая иконка типа + имя + сводка-подзаголовок + бейдж статуса + ⋯-меню).

import { useState } from 'react';
import type { CSSProperties } from 'react';
import {
  Plus, Zap, FlaskConical, Pencil, Trash2,
  CheckCircle2, Power,
} from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { Persona, PersonaAutomationRule, Project, AutomationTriggerType } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { api } from '../../lib/api';
import { Menu, MenuItem, ConfirmDialog } from '../../components/ui';
import { SectionLabel } from '../tasks/bits';
import { AutomationRuleDialog } from './AutomationRuleDialog';
import { TRIGGER_META, ACTION_META, triggerDetails, rulesCounter } from './automationMeta';

export function PersonaAutomationPanel({ persona, projects, accent, isMobile }: {
  persona: Persona; projects: Project[]; accent: string; isMobile?: boolean;
}) {
  const rules = persona.automationRules ?? [];
  const [editing, setEditing] = useState<PersonaAutomationRule | null>(null);
  const [creating, setCreating] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [menuId, setMenuId] = useState<string | null>(null);
  const [hoveredId, setHoveredId] = useState<string | null>(null);
  const [confirmRule, setConfirmRule] = useState<PersonaAutomationRule | null>(null);

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
    try { await api.personas.removeAutomation(persona.id, rule.id); } catch { /* noop */ }
    finally { setConfirmRule(null); }
  }

  return (
    <div style={{ height: '100%', overflowY: 'auto', background: C.bgMain }}>
      <div style={{
        maxWidth: 680, margin: '0 auto', boxSizing: 'border-box',
        padding: isMobile ? '20px 16px 32px' : '26px 32px 40px',
        display: 'flex', flexDirection: 'column', gap: 0, fontFamily: FONT.sans,
      }}>
        {/* Заголовок секции + счётчик + подзаголовок (как в «Умениях и правилах») */}
        <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 10 }}>
          <SectionLabel>Проактивность</SectionLabel>
          <span style={{ fontSize: 11.5, color: C.textMuted, flexShrink: 0 }}>
            {rulesCounter(rules)}
          </span>
        </div>
        <div style={{ fontSize: 12.5, color: C.textMuted, lineHeight: 1.5, marginTop: 4 }}>
          Персона сама реагирует на события — таймер, файлы, заметки, коммиты, смену статуса задач и @упоминания. Изменения сохраняются сразу.
        </div>

        {/* Список правил */}
        {rules.length > 0 && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 14 }}>
            {rules.map(rule => (
              <RuleCard
                key={rule.id}
                rule={rule}
                accent={accent}
                projects={projects}
                isMobile={isMobile}
                busy={busyId === rule.id}
                menuOpen={menuId === rule.id}
                hovered={hoveredId === rule.id}
                onHover={v => setHoveredId(h => (v ? rule.id : (h === rule.id ? null : h)))}
                onToggle={v => { setMenuId(null); void toggle(rule, v); }}
                onTest={() => { setMenuId(null); void test(rule); }}
                onEdit={() => { setMenuId(null); setEditing(rule); }}
                onAskDelete={() => { setMenuId(null); setConfirmRule(rule); }}
                onToggleMenu={() => setMenuId(m => m === rule.id ? null : rule.id)}
                onCloseMenu={() => setMenuId(null)}
              />
            ))}
          </div>
        )}

        {/* Пустое состояние — внутри 680-контейнера, в духе привязок */}
        {rules.length === 0 && (
          <EmptyState onCreate={() => setCreating(true)} />
        )}

        {/* Кнопка добавления под списком (как «Добавить привязку» в соседней вкладке) */}
        {rules.length > 0 && (
          <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap', marginTop: 12 }}>
            <AddRuleButton onClick={() => setCreating(true)} accent={accent} />
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

      {confirmRule && (
        <ConfirmDialog
          title={`Удалить правило «${confirmRule.name || 'без названия'}»?`}
          subtitle="Персона перестанет реагировать на это событие. Действие нельзя отменить."
          confirmLabel="Удалить"
          confirmVariant="danger"
          onConfirm={() => remove(confirmRule)}
          onCancel={() => setConfirmRule(null)}
        />
      )}
    </div>
  );
}

// ─── Карточка правила (хром 1-в-1 как карточка привязки) ───────────────────────

function RuleCard({ rule, accent, projects, isMobile, busy, menuOpen, hovered, onHover,
  onToggle, onTest, onEdit, onAskDelete, onToggleMenu, onCloseMenu,
}: {
  rule: PersonaAutomationRule;
  accent: string;
  projects: Project[];
  isMobile?: boolean;
  busy: boolean;
  menuOpen: boolean;
  hovered: boolean;
  onHover: (v: boolean) => void;
  onToggle: (v: boolean) => void;
  onTest: () => void;
  onEdit: () => void;
  onAskDelete: () => void;
  onToggleMenu: () => void;
  onCloseMenu: () => void;
}) {
  const trig = TRIGGER_META[rule.trigger.type] ?? TRIGGER_META.timer;
  const act = ACTION_META[rule.action.weight] ?? ACTION_META.gate;
  const details = triggerDetails(rule, projects);
  const dim = !rule.enabled;
  const subtitle = `${trig.label}${details ? ' · ' + details : ''} · ${act.label}`;

  return (
    <div
      onMouseEnter={() => onHover(true)}
      onMouseLeave={() => onHover(false)}
      style={{
        background: C.bgWhite,
        border: `1px solid ${hovered || menuOpen ? accent : C.border}`,
        borderRadius: R.xl, padding: '10px 14px',
        transition: 'border-color 0.15s, background 0.6s',
      }}
    >
      {/* Однорядный лейаут: иконка типа · имя+сводка · бейдж+⋯ */}
      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
        <TriggerTypeIcon type={rule.trigger.type} dim={dim} />

        <div style={{ flex: 1, minWidth: 0, opacity: dim ? 0.55 : 1 }}>
          <div style={{
            fontSize: 13.5, fontWeight: 600, color: C.textHeading,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {rule.name || 'Без названия'}
          </div>
          <div style={{
            fontSize: 12, color: C.textSecondary, marginTop: 1,
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {subtitle}
          </div>
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexShrink: 0 }}>
          <RuleStatusBadge enabled={rule.enabled} />
          <div style={{ position: 'relative' }}>
            <button
              onClick={onToggleMenu}
              aria-label="Действия"
              disabled={busy}
              style={{
                width: isMobile ? 36 : 28, height: isMobile ? 36 : 28, border: 'none',
                background: 'transparent', borderRadius: R.md, cursor: 'pointer',
                color: C.textMuted, fontSize: 16, lineHeight: 1,
                visibility: isMobile || hovered || menuOpen ? 'visible' : 'hidden',
              }}
            >⋯</button>
            {menuOpen && (
              <Menu onClose={onCloseMenu} align="right" top={30} minWidth={180}>
                <MenuItem
                  icon={<Pencil size={15} strokeWidth={ICON_STROKE} />}
                  label="Изменить"
                  onClick={onEdit}
                />
                <MenuItem
                  icon={<FlaskConical size={15} strokeWidth={ICON_STROKE} />}
                  label={busy ? 'Запускаю…' : 'Проверить'}
                  disabled={busy}
                  onClick={onTest}
                />
                <MenuItem
                  icon={rule.enabled
                    ? <Power size={15} strokeWidth={ICON_STROKE} />
                    : <CheckCircle2 size={15} strokeWidth={ICON_STROKE} />}
                  label={rule.enabled ? 'Выключить' : 'Включить'}
                  onClick={() => onToggle(!rule.enabled)}
                />
                <div style={{ height: 1, background: C.borderLight, margin: '4px 6px' }} />
                <MenuItem
                  danger
                  icon={<Trash2 size={15} strokeWidth={ICON_STROKE} />}
                  label="Удалить"
                  onClick={onAskDelete}
                />
              </Menu>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

// ─── Круглая иконка типа триггера (как BindingTypeIcon — без залитой плитки) ───

function TriggerTypeIcon({ type, size = 32, dim }: { type: AutomationTriggerType; size?: number; dim?: boolean }) {
  const meta = TRIGGER_META[type] ?? TRIGGER_META.timer;
  return (
    <span style={{
      width: size, height: size, borderRadius: R.full, flexShrink: 0,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
      background: dim ? C.bgSelected : meta.bg, color: dim ? C.textMuted : meta.fg,
    }}>
      <meta.Icon size={size >= 32 ? 16 : 13} strokeWidth={ICON_STROKE} />
    </span>
  );
}

// ─── Бейдж статуса правила (как BindingModeBadge: активно/выкл) ────────────────

function RuleStatusBadge({ enabled }: { enabled: boolean }) {
  const bg = enabled ? C.accentLight : C.bgSelected;
  const fg = enabled ? C.accent : C.textMuted;
  return (
    <span style={{
      borderRadius: R.pill, padding: '2px 8px', fontSize: 10.5, fontWeight: 600,
      letterSpacing: '0.02em', background: bg, color: fg, flexShrink: 0, whiteSpace: 'nowrap',
    }}>
      {enabled ? 'активно' : 'выкл'}
    </span>
  );
}

// ─── Empty-state (внутри 680-контейнера, в духе привязок) ──────────────────────

function EmptyState({ onCreate }: { onCreate: () => void }) {
  return (
    <div style={{
      marginTop: 14, border: `1.5px dashed ${C.dashed}`, borderRadius: R.xl,
      padding: '24px 22px', textAlign: 'center',
      display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 12,
    }}>
      <div style={{ color: C.textMuted }}>
        <Zap size={26} strokeWidth={1.5} />
      </div>
      <div style={{ fontSize: 13.5, fontWeight: 600, color: C.textHeading }}>
        Подключи первое правило
      </div>
      <div style={{ maxWidth: 360, fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5 }}>
        Персона будет сама реагировать на события: таймер, изменения файлов и заметок,
        новые коммиты, смену статуса задач и @упоминания. По умолчанию она сначала решает,
        стоит ли вмешиваться.
      </div>
      <button onClick={onCreate} style={emptyAddBtn}>
        <Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        Создать правило
      </button>
    </div>
  );
}

// Пунктирная кнопка «+ Добавить правило» (как AddBindingButton)
function AddRuleButton({ onClick, accent }: { onClick: () => void; accent: string }) {
  return (
    <button
      onClick={onClick}
      onMouseEnter={e => { e.currentTarget.style.borderColor = accent; e.currentTarget.style.color = accent; }}
      onMouseLeave={e => { e.currentTarget.style.borderColor = C.dashed; e.currentTarget.style.color = C.textSecondary; }}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 6,
        border: `1.5px dashed ${C.dashed}`, background: 'transparent', color: C.textSecondary,
        borderRadius: R.lg, padding: '7px 14px', fontSize: 12.5, fontWeight: 600,
        cursor: 'pointer', fontFamily: FONT.sans, transition: 'border-color 0.15s, color 0.15s',
      }}
    >
      <Plus size={14} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      Добавить правило
    </button>
  );
}

// Метаданные триггеров/действий и triggerDetails/rulesCounter — в общем ./automationMeta

// ─── Стили ──────────────────────────────────────────────────────────────────────

const emptyAddBtn: CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: 7,
  border: `1px solid ${C.accent}`, background: C.accent, color: C.onAccent,
  borderRadius: R.lg, padding: '8px 14px', cursor: 'pointer',
  fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
};

// Вкладка «Проактивность» персоны: правила «событие → действие». Персона сама
// реагирует на события (таймер, файлы, заметки, коммиты, смена статуса задач,
// @упоминания). Правила живут в Persona.AutomationRules; мутации — мгновенно
// через REST, список обновляется по realtime personas_changed (как привязки/память).
//
// Вёрстка повторяет соседние вкладки студии (Задачи/Умения/Память): хедер-сводка,
// карточки сущностей с ясной иерархией, empty-state с lucide-иконкой. Карточка
// правила — иконка триггера + имя + строки «Событие»/«Действие» + тумблер + ⋯-меню.

import { useState } from 'react';
import {
  Plus, Zap, FlaskConical, Pencil, Trash2, MoreHorizontal,
  Clock, FileText, StickyNote, GitBranch, ListChecks, AtSign,
  MessageSquare, Wrench,
} from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import type { Persona, PersonaAutomationRule, Project, AutomationTriggerType, AutomationActionWeight } from '../../types';
import { C, FONT, R, SHADOW } from '../../lib/design';
import { api } from '../../lib/api';
import { Toggle, Menu, MenuItem, ConfirmDialog } from '../../components/ui';
import { AutomationRuleDialog } from './AutomationRuleDialog';

export function PersonaAutomationPanel({ persona, projects, accent, isMobile }: {
  persona: Persona; projects: Project[]; accent: string; isMobile?: boolean;
}) {
  const rules = persona.automationRules ?? [];
  const [editing, setEditing] = useState<PersonaAutomationRule | null>(null);
  const [creating, setCreating] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [menuId, setMenuId] = useState<string | null>(null);
  const [confirmRule, setConfirmRule] = useState<PersonaAutomationRule | null>(null);

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
    try { await api.personas.removeAutomation(persona.id, rule.id); } catch { /* noop */ }
    finally { setConfirmRule(null); }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {/* Шапка-сводка (как в «Задачах»): счётчик + кнопка добавления */}
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

      {/* Список правил */}
      <div style={{ flex: 1, overflowY: 'auto', padding: isMobile ? '4px 16px 20px' : '4px 22px 24px' }}>
        {rules.length === 0 ? (
          <EmptyState onCreate={() => setCreating(true)} />
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            {rules.map(rule => (
              <RuleCard
                key={rule.id}
                rule={rule}
                accent={accent}
                projects={projects}
                isMobile={isMobile}
                busy={busyId === rule.id}
                menuOpen={menuId === rule.id}
                onToggle={v => toggle(rule, v)}
                onTest={() => { setMenuId(null); void test(rule); }}
                onEdit={() => { setMenuId(null); setEditing(rule); }}
                onAskDelete={() => { setMenuId(null); setConfirmRule(rule); }}
                onToggleMenu={() => setMenuId(menuId === rule.id ? null : rule.id)}
                onCloseMenu={() => setMenuId(null)}
              />
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

// ─── Карточка правила ──────────────────────────────────────────────────────────

function RuleCard({ rule, accent, projects, isMobile, busy, menuOpen,
  onToggle, onTest, onEdit, onAskDelete, onToggleMenu, onCloseMenu,
}: {
  rule: PersonaAutomationRule;
  accent: string;
  projects: Project[];
  isMobile?: boolean;
  busy: boolean;
  menuOpen: boolean;
  onToggle: (v: boolean) => void;
  onTest: () => void;
  onEdit: () => void;
  onAskDelete: () => void;
  onToggleMenu: () => void;
  onCloseMenu: () => void;
}) {
  const trig = TRIGGER_META[rule.trigger.type] ?? TRIGGER_META.timer;
  const TriggerIcon = trig.Icon;
  const act = ACTION_META[rule.action.weight];
  const ActionIcon = act.Icon;
  const details = triggerDetails(rule, projects);
  const disabled = !rule.enabled;

  return (
    <div style={{
      display: 'flex', gap: 11,
      background: C.bgWhite, border: `1px solid ${C.borderLight}`,
      boxShadow: SHADOW.card, borderRadius: R.xl,
      padding: '11px 12px', opacity: disabled ? 0.6 : 1,
      transition: 'opacity 0.15s',
    }}>
      {/* Плитка-иконка триггера (цветная) */}
      <div style={{
        width: 38, height: 38, borderRadius: R.md, flexShrink: 0,
        background: `${accent}1A`, color: accent,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
      }}>
        <TriggerIcon size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
      </div>

      {/* Контент: имя + строки «Событие» / «Действие» */}
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{
          fontFamily: FONT.sans, fontSize: 14, fontWeight: 600, color: C.textHeading,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {rule.name || 'Без названия'}
        </div>

        {/* Событие */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 7, marginTop: 8, flexWrap: 'wrap' }}>
          <MetaChip Icon={TriggerIcon} label={trig.label} accent={accent} />
          {details && <MetaDetail>{details}</MetaDetail>}
        </div>

        {/* Действие */}
        <div style={{ display: 'flex', alignItems: 'center', gap: 7, marginTop: 4, flexWrap: 'wrap' }}>
          <MetaChip Icon={ActionIcon} label={act.label} />
          {rule.action.instruction && (
            <MetaDetail>{truncate(rule.action.instruction, 80)}</MetaDetail>
          )}
        </div>

        {/* Нижняя строка действий: «Проверить» вынесена отдельно (частое действие) */}
        <div style={{ display: 'flex', justifyContent: 'flex-end', marginTop: 9 }}>
          <button onClick={onTest} disabled={busy} style={testBtn(busy)}>
            <FlaskConical size={13} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
            {busy ? 'Запускаю…' : 'Проверить'}
          </button>
        </div>
      </div>

      {/* Правая колонка: тумблер + ⋯-меню */}
      <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', gap: 6, flexShrink: 0 }}>
        <Toggle checked={rule.enabled} onChange={onToggle} />
        <div style={{ position: 'relative' }}>
          <button
            onClick={onToggleMenu}
            aria-label="Действия"
            disabled={busy}
            style={iconMenuBtn(isMobile)}
          >
            <MoreHorizontal size={16} strokeWidth={ICON_STROKE} />
          </button>
          {menuOpen && (
            <Menu onClose={onCloseMenu} align="right" top={32} minWidth={180}>
              <MenuItem
                icon={<Pencil size={15} strokeWidth={ICON_STROKE} />}
                label="Изменить"
                onClick={onEdit}
              />
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
  );
}

// ─── Empty-state ───────────────────────────────────────────────────────────────

function EmptyState({ onCreate }: { onCreate: () => void }) {
  return (
    <div style={{
      marginTop: 10, border: `1.5px dashed ${C.dashed}`, borderRadius: R.xl,
      padding: '30px 22px', textAlign: 'center',
      display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 12,
    }}>
      <div style={{ color: C.dashed }}>
        <Zap size={40} strokeWidth={1.5} />
      </div>
      <div style={{ maxWidth: 340, fontFamily: FONT.sans, fontSize: 13.5, color: C.textSecondary, lineHeight: 1.5 }}>
        Персона будет сама реагировать на события: таймер, изменения файлов и заметок,
        новые коммиты, смену статуса задач и @упоминания. По умолчанию она сначала решает,
        стоит ли вмешиваться, и пишет в отдельный чат правила.
      </div>
      <button onClick={onCreate} style={emptyAddBtn}>
        <Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
        Создать первое правило
      </button>
    </div>
  );
}

// ─── Чип и детальная подпись в карточке ────────────────────────────────────────

function MetaChip({ Icon, label, accent }: { Icon: React.ComponentType<{ size?: number; strokeWidth?: number; style?: React.CSSProperties }>; label: string; accent?: string }) {
  return (
    <span style={{
      display: 'inline-flex', alignItems: 'center', gap: 4,
      fontSize: 11, fontWeight: 600, letterSpacing: '0.01em',
      padding: '2px 8px', borderRadius: R.pill, whiteSpace: 'nowrap',
      background: accent ? `${accent}1F` : C.bgSelected,
      color: accent ?? C.textSecondary,
    }}>
      <Icon size={11} strokeWidth={ICON_STROKE} style={{ flexShrink: 0 }} />
      {label}
    </span>
  );
}

function MetaDetail({ children }: { children: React.ReactNode }) {
  return (
    <span style={{
      fontFamily: FONT.sans, fontSize: 12, color: C.textMuted, lineHeight: 1.4,
      overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0,
    }}>
      {children}
    </span>
  );
}

// ─── Метаданные триггеров и действий ───────────────────────────────────────────

const TRIGGER_META: Record<AutomationTriggerType, { label: string; Icon: React.ComponentType<{ size?: number; strokeWidth?: number; style?: React.CSSProperties }> }> = {
  timer:      { label: 'Таймер',       Icon: Clock },
  file:       { label: 'Файлы',        Icon: FileText },
  note:       { label: 'Заметки',      Icon: StickyNote },
  gitCommit:  { label: 'Коммиты',      Icon: GitBranch },
  taskStatus: { label: 'Статус задачи', Icon: ListChecks },
  mention:    { label: 'Упоминание',   Icon: AtSign },
};

const ACTION_META: Record<AutomationActionWeight, { label: string; Icon: React.ComponentType<{ size?: number; strokeWidth?: number; style?: React.CSSProperties }> }> = {
  gate: { label: 'Сообщить', Icon: MessageSquare },
  work: { label: 'Полный ход', Icon: Wrench },
};

// Короткие подписи статусов задач для детали триггера taskStatus
const TASK_STATUS_SHORT: Record<string, string> = {
  Todo: 'К выполнению',
  InProgress: 'В работе',
  Done: 'Готово',
};

// Человекопонятная подпись параметров триггера (деталь под чипом типа)
function triggerDetails(rule: PersonaAutomationRule, projects: Project[]): string {
  const a = ((rule.trigger.args?.schedule as Record<string, any>) ?? rule.trigger.args ?? {}) as Record<string, any>;
  switch (rule.trigger.type) {
    case 'timer': {
      if (a.intervalMinutes) return `каждые ${a.intervalMinutes} мин`;
      const sched = rule.trigger.args?.schedule as Record<string, any> | undefined;
      const type = sched?.type ?? a.type;
      const kind = type === 'weekdays' ? 'по будням'
        : type === 'weekly' ? 'по выбранным дням'
        : 'ежедневно';
      const time = sched?.time ?? a.time;
      return time ? `${kind} в ${time}` : kind;
    }
    case 'file': {
      const args = (rule.trigger.args ?? {}) as Record<string, any>;
      const proj = projects.find(p => p.id === args.projectId);
      const glob = String(args.glob ?? '**/*');
      return proj ? `${glob} · ${proj.name}` : glob;
    }
    case 'note': {
      const args = (rule.trigger.args ?? {}) as Record<string, any>;
      const src = args.source ?? args.projectId;
      if (!src || src === 'personal') return 'личный vault';
      const proj = projects.find(p => p.id === src);
      return proj ? `проект «${proj.name}»` : 'заметки';
    }
    case 'gitCommit': {
      const args = (rule.trigger.args ?? {}) as Record<string, any>;
      const proj = projects.find(p => p.id === args.projectId);
      return proj ? proj.name : 'репозиторий проекта';
    }
    case 'taskStatus': {
      const args = (rule.trigger.args ?? {}) as Record<string, any>;
      const parts: string[] = [];
      if (args.from) parts.push(TASK_STATUS_SHORT[String(args.from)] ?? String(args.from));
      if (args.to) parts.push(TASK_STATUS_SHORT[String(args.to)] ?? String(args.to));
      return parts.length ? parts.join(' → ') : 'любая смена';
    }
    case 'mention':
      return 'когда упоминают в чате';
    default:
      return '';
  }
}

function truncate(s: string, n: number): string {
  return s.length > n ? s.slice(0, n).trimEnd() + '…' : s;
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

const emptyAddBtn: React.CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: 7,
  border: `1px solid ${C.accent}`, background: C.accent, color: C.onAccent,
  borderRadius: R.lg, padding: '8px 14px', cursor: 'pointer',
  fontFamily: FONT.sans, fontSize: 13, fontWeight: 600,
};

function testBtn(busy: boolean): React.CSSProperties {
  return {
    display: 'inline-flex', alignItems: 'center', gap: 5,
    border: `1px solid ${C.border}`, background: C.bgPanel, color: C.textSecondary,
    borderRadius: R.md, padding: '5px 10px', cursor: busy ? 'default' : 'pointer',
    fontFamily: FONT.sans, fontSize: 12, fontWeight: 500,
    opacity: busy ? 0.6 : 1,
  };
}

function iconMenuBtn(isMobile?: boolean): React.CSSProperties {
  return {
    width: isMobile ? 36 : 28, height: isMobile ? 36 : 28, border: 'none',
    background: 'transparent', borderRadius: R.md, cursor: 'pointer',
    color: C.textMuted, display: 'flex', alignItems: 'center', justifyContent: 'center',
  };
}

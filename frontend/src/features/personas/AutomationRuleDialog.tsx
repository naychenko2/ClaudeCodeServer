// Диалог создания/редактирования правила автоматизации персоны. Форма: имя + тип
// источника → параметры (зависят от типа) → условие (тихие часы/интервал/фильтр) →
// действие (гейт/полный ход + инструкция + запоминание). Сохранение мгновенное
// (POST/PUT), список обновится по realtime personas_changed.
//
// Визуальный язык — тот же, что у формы задачи/привязок: секции Field, сегменты и
// селекты через UI-кит, toggle-чипы для мультивыбора, нижний футер ModalActions.
// Логика формы → AutomationRuleDto (buildArgs/initialForm) не менялась: ключи Args
// и DTO остались прежними, бэкенд ждёт именно их.

import { useState } from 'react';
import { Modal, ModalActions, Field, TextField, TextArea, SegmentedControl, Toggle } from '../../components/ui';
import type { Persona, PersonaAutomationRule, Project, AutomationTriggerType, AutomationActionWeight, AutomationRuleDto } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { api } from '../../lib/api';
import { bumpPersonas } from '../../lib/personas';
import { EXPIRY_PRESETS, DEFAULT_EXPIRY } from '../../lib/expiry';

type ScheduleType = 'daily' | 'weekdays' | 'weekly' | 'interval';

interface FormState {
  name: string;
  enabled: boolean;
  triggerType: AutomationTriggerType;
  scheduleType: ScheduleType;
  time: string;          // HH:mm
  intervalMinutes: string;
  weekdays: number[];    // ISO 1=Пн..7=Вс
  fileProjectId: string;
  glob: string;
  watchCreated: boolean;
  watchChanged: boolean;
  noteSource: string;    // "personal" | projectId
  noteTags: string;      // через запятую
  noteSection: string;
  gitProjectId: string;
  taskProjectId: string; // "" = любой
  taskFrom: string;      // "" = любой
  taskTo: string;
  onlyIf: string;
  quietFrom: string;     // HH:mm
  quietTo: string;
  minInterval: string;   // минут
  weight: AutomationActionWeight;
  instruction: string;
  remember: boolean;
  ttlEnabled: boolean;
  ttlMinutes: number;
}

const TRIGGER_OPTIONS: { value: AutomationTriggerType; label: string }[] = [
  { value: 'timer', label: 'Таймер' },
  { value: 'file', label: 'Файлы' },
  { value: 'note', label: 'Заметки' },
  { value: 'gitCommit', label: 'Коммиты' },
  { value: 'taskStatus', label: 'Статус задачи' },
  { value: 'mention', label: 'Упоминание' },
];

const WEEKDAY_LABELS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс']; // индексы 0..6 → ISO 1..7

function initialForm(rule: PersonaAutomationRule | null, projects: Project[]): FormState {
  const a = (rule?.trigger.args ?? {}) as Record<string, any>;
  const sched = (a.schedule ?? {}) as Record<string, any>;
  const firstProject = projects[0]?.id ?? '';
  return {
    name: rule?.name ?? '',
    enabled: rule?.enabled ?? true,
    triggerType: rule?.trigger.type ?? 'timer',
    scheduleType: (sched.type as ScheduleType) ?? 'daily',
    time: sched.time ?? '09:00',
    intervalMinutes: sched.intervalMinutes ? String(sched.intervalMinutes) : '60',
    weekdays: Array.isArray(sched.weekdays) ? sched.weekdays : [1, 3, 5],
    fileProjectId: a.projectId ?? firstProject,
    glob: a.glob ?? '**/*.md',
    watchCreated: Array.isArray(a.kinds) ? a.kinds.includes('created') : true,
    watchChanged: Array.isArray(a.kinds) ? a.kinds.includes('changed') : true,
    noteSource: a.source ?? 'personal',
    noteTags: Array.isArray(a.tags) ? a.tags.join(', ') : '',
    noteSection: a.section ?? '',
    gitProjectId: a.projectId ?? firstProject,
    taskProjectId: a.projectId ?? '',
    taskFrom: a.from ?? '',
    taskTo: a.to ?? 'Done',
    onlyIf: rule?.condition?.onlyIf ?? '',
    quietFrom: rule?.condition?.quietFrom ?? '',
    quietTo: rule?.condition?.quietTo ?? '',
    minInterval: rule?.condition?.minIntervalMinutes != null ? String(rule.condition.minIntervalMinutes) : '',
    weight: rule?.action.weight ?? 'gate',
    instruction: rule?.action.instruction ?? '',
    remember: rule?.action.rememberInHistory ?? false,
    // Новое правило — бессрочность по умолчанию 24ч (①); существующее — как сохранено
    // (null у старого правила означает «бессрочно» либо унаследованный дефолт 1440 сервера)
    ttlEnabled: rule ? rule.action.expiresAfterMinutes != null : true,
    ttlMinutes: rule?.action.expiresAfterMinutes ?? DEFAULT_EXPIRY,
  };
}

function buildArgs(f: FormState): Record<string, unknown> {
  switch (f.triggerType) {
    case 'timer': {
      const schedule: Record<string, unknown> = { type: f.scheduleType };
      if (f.scheduleType === 'interval') schedule.intervalMinutes = Number(f.intervalMinutes) || 60;
      else { schedule.time = f.time || '09:00'; if (f.scheduleType === 'weekly') schedule.weekdays = f.weekdays; }
      return { schedule };
    }
    case 'file': {
      const kinds: string[] = [];
      if (f.watchCreated) kinds.push('created');
      if (f.watchChanged) kinds.push('changed');
      return { projectId: f.fileProjectId, glob: f.glob || '**/*', kinds };
    }
    case 'note': {
      const tags = f.noteTags.split(',').map(t => t.trim()).filter(Boolean);
      const out: Record<string, unknown> = { source: f.noteSource || 'personal' };
      if (tags.length) out.tags = tags;
      if (f.noteSection.trim()) out.section = f.noteSection.trim();
      return out;
    }
    case 'gitCommit': return { projectId: f.gitProjectId };
    case 'taskStatus': {
      const out: Record<string, unknown> = {};
      if (f.taskProjectId) out.projectId = f.taskProjectId;
      if (f.taskFrom) out.from = f.taskFrom;
      if (f.taskTo) out.to = f.taskTo;
      return out;
    }
    case 'mention': return {};
    default: return {};
  }
}

export function AutomationRuleDialog({ persona, projects, rule, onClose }: {
  persona: Persona; projects: Project[]; rule: PersonaAutomationRule | null; onClose: () => void;
}) {
  const [f, setF] = useState<FormState>(() => initialForm(rule, projects));
  const [saving, setSaving] = useState(false);
  const set = <K extends keyof FormState>(key: K, value: FormState[K]) => setF(prev => ({ ...prev, [key]: value }));

  async function save() {
    setSaving(true);
    const dto: AutomationRuleDto = {
      name: f.name.trim() || 'Правило',
      enabled: f.enabled,
      triggerType: f.triggerType,
      triggerArgs: buildArgs(f),
      conditionOnlyIf: f.onlyIf.trim() || null,
      quietFrom: f.quietFrom || null,
      quietTo: f.quietTo || null,
      minIntervalMinutes: f.minInterval ? Number(f.minInterval) : null,
      actionWeight: f.weight,
      actionInstruction: f.instruction,
      rememberInHistory: f.remember,
      actionExpiresAfterMinutes: f.ttlEnabled ? f.ttlMinutes : null,
    };
    try {
      if (rule) await api.personas.updateAutomation(persona.id, rule.id, dto);
      else await api.personas.addAutomation(persona.id, dto);
      // Дожидаемся обновления стора персон ДО закрытия — иначе повторное открытие
      // «Изменить» может увидеть снимок до сохранения (гонка с realtime personas_changed)
      await bumpPersonas();
      onClose();
    } catch (e: any) {
      window.alert(e?.message ?? 'Не удалось сохранить правило');
    } finally { setSaving(false); }
  }

  const show = (cond: boolean, node: React.ReactNode) => cond ? node : null;

  return (
    <Modal
      width={560}
      title={rule ? 'Правило автоматизации' : 'Новое правило автоматизации'}
      subtitle="Персона сама отреагирует на событие: решит, стоит ли вмешаться, и напишет в чат правила (или выполнит полный ход)."
      onClose={onClose}
      footer={<ModalActions confirmLabel={rule ? 'Сохранить' : 'Создать'} onConfirm={save} loading={saving} onCancel={onClose} />}
    >
      {/* Имя + активность */}
      <Field label="Название правила">
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <TextField value={f.name} onChange={v => set('name', v)} placeholder="Напр. «Следить за релизами»" autoFocus />
          </div>
          <ToggleLabel checked={f.enabled} onChange={v => set('enabled', v)} />
        </div>
      </Field>

      {/* Тип источника */}
      <Field label="Событие" hint="Что запускает реакцию персоны.">
        <SegmentedControl
          value={f.triggerType}
          options={TRIGGER_OPTIONS}
          onChange={v => set('triggerType', v)}
          columns={3}
        />
      </Field>

      {/* Параметры по типу источника */}
      {show(f.triggerType === 'timer', (
        <Field label="Расписание">
          <SegmentedControl
            value={f.scheduleType}
            options={[
              { value: 'daily', label: 'Ежедневно' },
              { value: 'weekdays', label: 'По будням' },
              { value: 'weekly', label: 'По дням' },
              { value: 'interval', label: 'Интервал' },
            ]}
            onChange={v => set('scheduleType', v)}
            columns={4}
          />
          <div style={{ marginTop: 8 }}>
            {show(f.scheduleType === 'interval', (
              <TextField type="number" value={f.intervalMinutes} onChange={v => set('intervalMinutes', v)} placeholder="60" />
            )) || show(f.scheduleType !== 'interval', (
              <TextField type="time" value={f.time} onChange={v => set('time', v)} style={{ maxWidth: 180 }} />
            ))}
          </div>
          {show(f.scheduleType === 'weekly', (
            <div style={{ display: 'flex', gap: 6, marginTop: 8 }}>
              {WEEKDAY_LABELS.map((lbl, i) => {
                const iso = i + 1;
                const active = f.weekdays.includes(iso);
                return (
                  <button key={iso} type="button"
                    onClick={() => set('weekdays', active ? f.weekdays.filter(d => d !== iso) : [...f.weekdays, iso])}
                    style={weekdayBtn(active)}>{lbl}</button>
                );
              })}
            </div>
          ))}
        </Field>
      ))}

      {show(f.triggerType === 'file', (
        <Field label="Файлы проекта" hint="glob-шаблон: **/*.md, src/**/*.ts. Срабатывает на появление/изменение.">
          <ProjectSelect value={f.fileProjectId} onChange={v => set('fileProjectId', v)} projects={projects} allowPersonal={false} />
          <div style={{ marginTop: 8 }}>
            <TextField value={f.glob} onChange={v => set('glob', v)} placeholder="**/*.md" mono />
          </div>
          <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
            <ToggleChip active={f.watchCreated} onClick={() => set('watchCreated', !f.watchCreated)}>Новые файлы</ToggleChip>
            <ToggleChip active={f.watchChanged} onClick={() => set('watchChanged', !f.watchChanged)}>Изменённые</ToggleChip>
          </div>
        </Field>
      ))}

      {show(f.triggerType === 'note', (
        <Field label="Заметки" hint="Источник: личный vault или notes/ проекта. Можно фильтровать по тегам/разделу.">
          <ProjectSelect value={f.noteSource} onChange={v => set('noteSource', v)} projects={projects} allowPersonal={true} />
          <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
            <div style={{ flex: 1, minWidth: 0 }}><TextField value={f.noteTags} onChange={v => set('noteTags', v)} placeholder="#тег1, #тег2" /></div>
            <div style={{ flex: 1, minWidth: 0 }}><TextField value={f.noteSection} onChange={v => set('noteSection', v)} placeholder="папка/" /></div>
          </div>
        </Field>
      ))}

      {show(f.triggerType === 'gitCommit', (
        <Field label="Репозиторий проекта" hint="Срабатывает на каждый новый коммит в проекте.">
          <ProjectSelect value={f.gitProjectId} onChange={v => set('gitProjectId', v)} projects={projects} allowPersonal={false} />
        </Field>
      ))}

      {show(f.triggerType === 'taskStatus', (
        <Field label="Смена статуса задачи" hint="Реакция на переход статуса (Todo/InProgress/Done). Пусто — любой.">
          <ProjectSelect value={f.taskProjectId} onChange={v => set('taskProjectId', v)} projects={projects} allowPersonal allowAny labelAny="Любой проект" />
          <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <SmallLabel>из статуса</SmallLabel>
              <StatusSelect value={f.taskFrom} onChange={v => set('taskFrom', v)} allowAny />
            </div>
            <div style={{ flex: 1, minWidth: 0 }}>
              <SmallLabel>в статус</SmallLabel>
              <StatusSelect value={f.taskTo} onChange={v => set('taskTo', v)} allowAny />
            </div>
          </div>
        </Field>
      ))}

      {show(f.triggerType === 'mention', (
        <div style={hintBox}>Персона сработает, когда кто-то упомянет её @handle в чате, где она не активный собеседник и не участник группы.</div>
      ))}

      {/* Действие */}
      <Field label="Действие" hint="Гейт — персона решает, стоит ли реагировать, и пишет сообщение. Полный ход — она работает (правит файлы/задачи через инструменты).">
        <SegmentedControl
          value={f.weight}
          options={[{ value: 'gate', label: 'Сообщить' }, { value: 'work', label: 'Полный ход' }]}
          onChange={v => set('weight', v)}
        />
        <div style={{ marginTop: 8 }}>
          <TextArea value={f.instruction} onChange={v => set('instruction', v)} placeholder="Что делать персоне при срабатывании…" autoGrow maxHeight={160} />
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginTop: 10 }}>
          <Toggle checked={f.remember} onChange={v => set('remember', v)} />
          <span style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans }}>
            Запоминать в истории персоны
          </span>
        </div>
      </Field>

      {/* Время жизни чата правила — общий чат, в котором персона отвечает на каждое
          срабатывание (и Сообщить, и Полный ход пишут в него же) */}
      <Field label="Время жизни чата" hint="Чат правила удалится сам вместе с историей, если не будет активности выбранное время.">
        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
          <Toggle checked={f.ttlEnabled} onChange={v => set('ttlEnabled', v)} />
          <span style={{ fontSize: 13, color: C.textSecondary, fontFamily: FONT.sans }}>
            {f.ttlEnabled ? 'Удаляется автоматически' : 'Хранится бессрочно'}
          </span>
        </div>
        {f.ttlEnabled && (
          <div style={{ marginTop: 10 }}>
            <SegmentedControl
              value={String(f.ttlMinutes)}
              options={EXPIRY_PRESETS.map(p => ({ value: String(p.minutes), label: p.label }))}
              onChange={v => set('ttlMinutes', Number(v))}
              columns={4}
            />
          </div>
        )}
      </Field>

      {/* Условие */}
      <Field label="Условие и троттлинг" hint="Тихие часы и минимальный интервал между срабатываниями — опционально.">
        <div style={{ display: 'flex', gap: 8 }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <SmallLabel>тихие часы с</SmallLabel>
            <TextField type="time" value={f.quietFrom} onChange={v => set('quietFrom', v)} style={{ maxWidth: 160 }} />
          </div>
          <div style={{ flex: 1, minWidth: 0 }}>
            <SmallLabel>до</SmallLabel>
            <TextField type="time" value={f.quietTo} onChange={v => set('quietTo', v)} style={{ maxWidth: 160 }} />
          </div>
          <div style={{ flex: 1, minWidth: 0 }}>
            <SmallLabel>интервал, мин</SmallLabel>
            <TextField type="number" value={f.minInterval} onChange={v => set('minInterval', v)} placeholder="5" />
          </div>
        </div>
        <div style={{ marginTop: 8 }}>
          <TextField value={f.onlyIf} onChange={v => set('onlyIf', v)} placeholder="Доп. условие: «реагируй, только если касается деплоя»" />
        </div>
      </Field>
    </Modal>
  );
}

// ─── Под-компоненты ─────────────────────────────────────────────────────────────

function ProjectSelect({ value, onChange, projects, allowPersonal, allowAny, labelAny }: {
  value: string; onChange: (v: string) => void; projects: Project[];
  allowPersonal: boolean; allowAny?: boolean; labelAny?: string;
}) {
  return (
    <select value={value} onChange={e => onChange(e.target.value)} style={selectStyle}>
      {allowAny && <option value="">{labelAny ?? '— любой —'}</option>}
      {allowPersonal && <option value="personal">Личный vault</option>}
      {projects.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
    </select>
  );
}

const STATUSES: { value: string; label: string }[] = [
  { value: 'Todo', label: 'К выполнению' },
  { value: 'InProgress', label: 'В работе' },
  { value: 'Done', label: 'Готово' },
];

function StatusSelect({ value, onChange, allowAny }: { value: string; onChange: (v: string) => void; allowAny?: boolean }) {
  return (
    <select value={value} onChange={e => onChange(e.target.value)} style={selectStyle}>
      {allowAny && <option value="">любой</option>}
      {STATUSES.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
    </select>
  );
}

// Toggle + подпись «вкл/выкл» — используется в строке имени правила
function ToggleLabel({ checked, onChange }: { checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 5, flexShrink: 0 }}>
      <Toggle checked={checked} onChange={onChange} />
      <span style={{ fontSize: 11, color: C.textMuted, fontFamily: FONT.sans }}>{checked ? 'вкл' : 'выкл'}</span>
    </div>
  );
}

// Мультивыбор-пилюля (новые/изменённые файлы, дни недели) — активный сегмент accent
function ToggleChip({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button type="button" onClick={onClick} style={{
      padding: '7px 12px', borderRadius: R.lg, cursor: 'pointer',
      border: `1px solid ${active ? C.accent : C.border}`,
      background: active ? C.accentLight : C.bgWhite,
      color: active ? C.accent : C.textSecondary,
      fontSize: 12.5, fontWeight: 600, fontFamily: FONT.sans,
      transition: 'background 0.15s, border-color 0.15s, color 0.15s',
    }}>
      {children}
    </button>
  );
}

function SmallLabel({ children }: { children: React.ReactNode }) {
  return <div style={{ fontSize: 11, fontWeight: 600, color: C.textMuted, textTransform: 'uppercase', letterSpacing: '0.04em', marginBottom: 4, fontFamily: FONT.sans }}>{children}</div>;
}

// ─── Стили ──────────────────────────────────────────────────────────────────────

const selectStyle: React.CSSProperties = {
  width: '100%', padding: '10px 13px', borderRadius: R.xl, border: `1px solid ${C.border}`,
  background: C.bgWhite, color: C.textHeading, fontSize: 14, fontFamily: FONT.sans, outline: 'none',
  boxSizing: 'border-box', height: 42, cursor: 'pointer',
};

function weekdayBtn(active: boolean): React.CSSProperties {
  return {
    flex: 1, padding: '8px 0', borderRadius: R.lg, border: `1px solid ${active ? C.accent : C.border}`,
    cursor: 'pointer', fontSize: 12, fontWeight: 600, fontFamily: FONT.sans,
    background: active ? C.accentLight : C.bgWhite, color: active ? C.accent : C.textSecondary,
    transition: 'background 0.15s, border-color 0.15s, color 0.15s',
  };
}

const hintBox: React.CSSProperties = {
  background: C.bgPanel, borderRadius: R.md, padding: '12px 14px',
  fontFamily: FONT.sans, fontSize: 12.5, color: C.textSecondary, lineHeight: 1.5,
};

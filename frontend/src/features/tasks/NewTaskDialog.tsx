// Диалог быстрого создания задачи: название, проект, срок, приоритет, исполнитель.
// Остальные поля задаются позже в редактировании.
//
// Дефекты (волна 2): сегмент «Задача | Дефект» в шапке формы и три поля воспроизведения
// (Steps обязателен у дефекта; Expected/Actual опциональны). Steps шлётся целиком —
// бэк заменяет объект Repro, частичный апдейт потерял бы соседние поля (см. CreateTaskDto).

import { useEffect, useMemo, useRef, useState } from 'react';
import type { DefectRepro, Project, Task, TaskAssignee, TaskKind, TaskPriority } from '../../types';
import { C, FONT, R } from '../../lib/design';
import { Button, Field, FieldLabel, Modal, SegmentedControl, TextArea, TextField } from '../../components/ui';
import { api } from '../../lib/api';
import { NO_PROJECT_COLOR, NO_PROJECT_LABEL, PRIORITY_LABEL, PRIORITY_ORDER, createTask, projectColor } from '../../lib/tasks';
import { PriorityFlag } from './bits';
import { ExecutorPicker } from './ExecutorPicker';
import { DueDatePicker } from './DueDatePicker';

interface Props {
  // Название по умолчанию — когда задача заводится ПО поводу (разбор инцидента
  // телеметрии): человек всё равно правит его перед созданием
  defaultTitle?: string;
  // Проект, выбранный по умолчанию (контекст воркспейса или фильтра календаря)
  defaultProjectId?: string;
  // Срок по умолчанию (быстрое создание на день из календаря)
  defaultDueDate?: string;
  // Персона-исполнитель по умолчанию (открыто из вкладки «Задачи» персоны:
  // «Поручить задачу») — предзаполняет исполнителя, assignee станет claude
  defaultPersonaId?: string;
  // Подпись кнопки «создать и открыть редактор» (в календаре — «Подробнее»)
  configureLabel?: string;
  // configure=true — открыть карточку сразу в редактировании
  onCreated: (task: Task, configure: boolean) => void;
  onClose: () => void;
}

export function NewTaskDialog({ defaultTitle, defaultProjectId, defaultDueDate, defaultPersonaId, configureLabel = 'Создать и настроить', onCreated, onClose }: Props) {
  const [title, setTitle] = useState(defaultTitle ?? '');
  const [projects, setProjects] = useState<Project[]>([]);
  // null = «Личное» (задача вне проекта) — дефолт, когда открыто не из проекта
  const [projectId, setProjectId] = useState<string | null>(defaultProjectId ?? null);
  const [priority, setPriority] = useState<TaskPriority>('medium');
  // Персона-исполнитель ⇒ assignee=claude (иначе по умолчанию «Я»)
  const [assignee, setAssignee] = useState<TaskAssignee>(defaultPersonaId ? 'claude' : 'me');
  const [personaId, setPersonaId] = useState(defaultPersonaId ?? '');
  const [dueDate, setDueDate] = useState<string | null>(defaultDueDate ?? null);
  const [dueTime, setDueTime] = useState<string | null>(null);
  // Какая из кнопок создаёт: 'plain' — просто создать, 'configure' — создать и настроить
  const [saving, setSaving] = useState<null | 'plain' | 'configure'>(null);
  const [error, setError] = useState<string | null>(null);
  // Вид карточки: обычная задача или дефект. У дефекта добавляются три поля
  // воспроизведения (Steps обязателен, Expected/Actual опциональны).
  const [kind, setKind] = useState<TaskKind>('task');
  const [steps, setSteps] = useState('');
  const [expected, setExpected] = useState('');
  const [actual, setActual] = useState('');

  useEffect(() => {
    api.projects.list().then(setProjects).catch(() => {});
  }, []);

  // Проектов может быть много (10+) — чипы одной горизонтальной лентой на всех
  // размерах, проект контекста первым, выбранный подъезжает в видимую зону
  const chipsRef = useRef<HTMLDivElement>(null);
  const orderedProjects = useMemo(() => {
    if (!defaultProjectId) return projects;
    const def = projects.find((p: Project) => p.id === defaultProjectId);
    return def ? [def, ...projects.filter((p: Project) => p.id !== defaultProjectId)] : projects;
  }, [projects, defaultProjectId]);

  useEffect(() => {
    if (projects.length === 0) return;
    chipsRef.current?.querySelector('[data-active="true"]')
      ?.scrollIntoView({ inline: 'center', block: 'nearest' });
  }, [projects.length]);

  // Шаги обязательны только у дефекта при попадании на проверку (гейт бэка), но не
  // для самого создания карточки. Завести дефект должно быть можно мгновенно —
  // иначе человек откладывает описание бага, пока соберёт шаги, и теряет контекст.
  // Подсказка под полем шагов остаётся (см. Field hint у дефектного Steps).
  const canCreate = title.trim().length > 0 && !saving;

  const handleCreate = async (configure: boolean) => {
    if (!canCreate) return;
    setSaving(configure ? 'configure' : 'plain');
    setError(null);
    try {
      // Repro собираем только у дефекта: пустые строки в expected/actual не шлём,
      // чтобы бэк не хранил мусорные пробелы рядом с шагами
      const repro: DefectRepro | undefined = kind === 'defect'
        ? {
            steps: steps.trim(),
            ...(expected.trim() ? { expected: expected.trim() } : {}),
            ...(actual.trim() ? { actual: actual.trim() } : {}),
          }
        : undefined;
      const task = await createTask(projectId, {
        title: title.trim(), priority, assignee,
        // Персона-исполнитель имеет смысл только у Claude
        personaId: assignee === 'claude' && personaId ? personaId : undefined,
        dueDate: dueDate ?? undefined, dueTime: dueTime ?? undefined,
        ...(kind === 'defect' ? { kind, repro } : {}),
      });
      onCreated(task, configure);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Не удалось создать задачу');
      setSaving(null);
    }
  };

  // Шапка модалки сразу даёт понять, открыт расширенный диалог для дефекта
  const isDefect = kind === 'defect';

  return (
    <Modal
      title={isDefect ? 'Новый дефект' : 'Новая задача'}
      onClose={onClose}
      width={520}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>Отмена</Button>
          <Button variant="secondary" disabled={!canCreate} loading={saving === 'configure'} onClick={() => handleCreate(true)}>
            {configureLabel}
          </Button>
          <Button variant="primary" disabled={!canCreate} loading={saving === 'plain'} onClick={() => handleCreate(false)}>
            Создать
          </Button>
        </>
      }
    >
      {/* Тип карточки: задача/дефект. Дефолт — задача, переключение раскрывает три поля ниже */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <SegmentedControl<TaskKind>
          value={kind}
          onChange={setKind}
          options={[
            { value: 'task', label: 'Задача' },
            { value: 'defect', label: 'Дефект' },
          ]}
        />
      </div>

      {/* Название */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <FieldLabel>Название</FieldLabel>
        <TextField value={title} onChange={setTitle} placeholder="Что нужно сделать?" autoFocus onEnter={() => handleCreate(false)} />
      </div>

      {/* Поля дефекта. Steps обязателен — бэк отвергнет дефект без него в ревью-колонке,
          UI гейтит кнопку через canCreate. Expected/Actual — пояснения автора/наблюдателя */}
      {isDefect && (
        <>
          <Field
            label="Шаги воспроизведения"
            hint="Необязательно при создании. Бэк попросит шаги при попадании дефекта в колонку проверки."
          >
            <TextArea
              value={steps}
              onChange={setSteps}
              placeholder="1. Открыть…&#10;2. Нажать…&#10;3. Видно:…"
              autoGrow minHeight={92} maxHeight={240}
            />
          </Field>
          <Field
            label="Ожидаемо"
            hint="Что должно было случиться. Необязательно."
          >
            <TextArea
              value={expected}
              onChange={setExpected}
              placeholder="Какое поведение считается правильным"
              autoGrow minHeight={64} maxHeight={200}
            />
          </Field>
          <Field
            label="Фактически"
            hint="Что случилось вместо этого. Необязательно."
          >
            <TextArea
              value={actual}
              onChange={setActual}
              placeholder="Чем наблюдение отличается от ожидаемого"
              autoGrow minHeight={64} maxHeight={200}
            />
          </Field>
        </>
      )}

      {/* Проект */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <FieldLabel>Проект</FieldLabel>
        {/* Одна горизонтальная лента: 10+ проектов не распирают форму.
            Чипы — через ui/Button pill-формы: цветная точка слева, активный — accent. */}
        <div
          ref={chipsRef}
          className="cc-hide-scrollbar"
          style={{ display: 'flex', gap: 8, overflowX: 'auto', paddingBottom: 2 }}
        >
          {/* «Личное» — нейтральная опция первой (задача вне проекта) */}
          <Button
            data-active={projectId === null}
            variant={projectId === null ? 'ghostAccent' : 'ghostFilled'}
            size="sm"
            pill
            onClick={() => setProjectId(null)}
            style={{ flexShrink: 0 }}
            leftIcon={<span style={{ width: 8, height: 8, borderRadius: '50%', background: NO_PROJECT_COLOR.main, flexShrink: 0 }} />}
          >
            {NO_PROJECT_LABEL}
          </Button>
          {orderedProjects.map((p: Project) => {
            const active = p.id === projectId;
            const color = projectColor(p.id);
            return (
              <Button
                key={p.id}
                data-active={active}
                variant={active ? 'ghostAccent' : 'ghostFilled'}
                size="sm"
                pill
                onClick={() => setProjectId(p.id)}
                style={{ flexShrink: 0 }}
                leftIcon={<span style={{ width: 8, height: 8, borderRadius: '50%', background: color.main, flexShrink: 0 }} />}
              >
                {p.name}
              </Button>
            );
          })}
        </div>
      </div>

      {/* Срок — как в редакторе: чипы + календарик с временем */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <FieldLabel>Срок</FieldLabel>
        <DueDatePicker
          dueDate={dueDate}
          dueTime={dueTime}
          onChange={(d, t) => { setDueDate(d); setDueTime(t); }}
        />
      </div>

      {/* Приоритет — четыре квадратные кнопки (контрол из ui/Button). Активный — accent */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <FieldLabel>Приоритет</FieldLabel>
        <div style={{ display: 'flex', gap: 7 }}>
          {PRIORITY_ORDER.map((p: TaskPriority) => {
            const active = p === priority;
            return (
              <Button
                key={p}
                variant={active ? 'ghostAccent' : 'ghostFilled'}
                size="sm"
                title={PRIORITY_LABEL[p]}
                onClick={() => setPriority(p)}
                style={{
                  width: 36, height: 36, padding: 0, minHeight: 36,
                  borderRadius: R.lg,
                }}
                leftIcon={<PriorityFlag priority={p} size={15} />}
              >
                {''}
              </Button>
            );
          })}
        </div>
      </div>

      {/* Исполнитель — единый пикер (Я / Claude / персона) */}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <FieldLabel>Исполнитель</FieldLabel>
        <ExecutorPicker
          assignee={assignee}
          personaId={personaId || null}
          projectId={projectId}
          onChange={v => { setAssignee(v.assignee); setPersonaId(v.personaId ?? ''); }}
        />
      </div>

      {error && (
        <div style={{ fontFamily: FONT.sans, fontSize: 12.5, color: C.danger }}>{error}</div>
      )}
    </Modal>
  );
}

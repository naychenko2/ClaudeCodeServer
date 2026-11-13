// Лента этапов редактора шаблона (F20, вариант A): заголовок, строки с ручкой ⠿, рамки
// «параллельно», подсказки, «+ Добавить этап», пустое состояние. Состояний шаблона не держит:
// список этапов приходит и уходит через onStagesChange, правку этапа — через onStageChange.

import { useEffect, useRef, useState } from 'react';
import { Plus, Workflow } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { Button, Chip, EmptyState } from '../../components/ui';
import type { Persona } from '../personas/types';
import type { SpecialtyCatalogEntry } from '../specialties/types';
import { STAGE_ROLES, SINGLE_ROLES } from './workflowTemplateModel';
import { STAGE_ROLE_LABEL, WorkflowStageRow } from './WorkflowStageRow';
import { addStage, moveStage, normalizeStages } from './workflowGraphModel';
import { afterNames, keyboardTarget, stageKeys, stripColumns, stripGroups, whoLabel } from './workflowStripModel';
import type { WorkflowStage } from './types';

// На сенсорном экране перетаскивание начинается долгим нажатием: короткий жест — прокрутка.
const LONG_PRESS_MS = 350;

interface Props {
  stages: WorkflowStage[];
  readOnly: boolean;
  specialties: SpecialtyCatalogEntry[];
  personas: Persona[];
  mcpChoices: string[];
  // Ошибки по id этапа (роль — запасной ключ, как у прежнего списка).
  errorsFor: (stage: WorkflowStage, index: number) => string[];
  onStagesChange: (next: WorkflowStage[]) => void;
  onStageChange: (index: number, next: WorkflowStage) => void;
  onRenameStage: (index: number, newId: string) => void;
  onRemoveStage: (index: number) => void;
  // Состав «Полного цикла» для пустого шаблона (нет — кнопки «Взять состав» нет).
  fullCycleStages?: WorkflowStage[] | null;
}

export function WorkflowStageStrip({
  stages, readOnly, specialties, personas, mcpChoices, errorsFor,
  onStagesChange, onStageChange, onRenameStage, onRemoveStage, fullCycleStages,
}: Props): React.ReactElement {
  const [picking, setPicking] = useState(false);
  const [drag, setDrag] = useState<{ from: number; over: number } | null>(null);
  const dragRef = useRef<{ from: number; over: number } | null>(null);
  const stagesRef = useRef(stages);
  stagesRef.current = stages;
  const cleanup = useRef<(() => void) | null>(null);
  useEffect(() => () => cleanup.current?.(), []);

  const norm = normalizeStages(stages);
  const columns = stripColumns(stages);
  const groups = stripGroups(stages);
  const keys = stageKeys(stages);
  const specLabels = Object.fromEntries(specialties.map(s => [s.key, s.label]));
  const persons = personas.map(p => ({ id: p.id, name: p.name, role: p.role }));
  const usedSingles = new Set(norm.map(s => s.role).filter(r => SINGLE_ROLES.includes(r as never)));

  const add = (role: string) => {
    const last = norm[norm.length - 1];
    onStagesChange(addStage(stages, { role, after: last ? [last.id ?? ''] : [] }));
    setPicking(false);
  };

  const startDrag = (from: number, e: React.PointerEvent) => {
    if (e.button !== undefined && e.button > 0) return;
    cleanup.current?.();
    const touch = e.pointerType === 'touch';
    let started = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    const begin = () => {
      started = true;
      dragRef.current = { from, over: from };
      setDrag({ from, over: from });
    };
    const stop = () => {
      if (timer) clearTimeout(timer);
      window.removeEventListener('pointermove', move);
      window.removeEventListener('pointerup', up);
      window.removeEventListener('pointercancel', cancel);
      cleanup.current = null;
    };
    const move = (ev: PointerEvent) => {
      if (!started) { if (touch) stop(); return; }
      const row = document.elementFromPoint(ev.clientX, ev.clientY)?.closest('[data-stage-index]');
      const over = row ? Number(row.getAttribute('data-stage-index')) : NaN;
      if (Number.isInteger(over) && dragRef.current && dragRef.current.over !== over) {
        dragRef.current = { from, over };
        setDrag({ from, over });
      }
    };
    const up = () => {
      const d = dragRef.current;
      stop();
      dragRef.current = null;
      setDrag(null);
      if (started && d && d.over !== d.from) onStagesChange(moveStage(stagesRef.current, d.from, d.over));
    };
    const cancel = () => { stop(); dragRef.current = null; setDrag(null); };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    window.addEventListener('pointercancel', cancel);
    cleanup.current = stop;
    if (touch) timer = setTimeout(begin, LONG_PRESS_MS); else begin();
  };

  const onGripKey = (index: number, e: React.KeyboardEvent) => {
    const delta = e.key === 'ArrowUp' ? -1 : e.key === 'ArrowDown' ? 1 : 0;
    if (delta === 0) return;
    e.preventDefault();
    const to = keyboardTarget(index, delta, stages.length);
    if (to !== null) onStagesChange(moveStage(stages, index, to));
  };

  const addButton = (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {picking && (
        <div role="group" aria-label="Роль нового этапа" style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
          {STAGE_ROLES.map(r => (
            <Chip
              key={r} variant="toggle" disabled={usedSingles.has(r)}
              title={usedSingles.has(r) ? 'Такой этап в шаблоне уже есть — он может быть только один' : undefined}
              onClick={() => add(r)}
            >{STAGE_ROLE_LABEL[r] ?? r}</Chip>
          ))}
        </div>
      )}
      <Button
        variant="dashed" size="sm" fullWidth onClick={() => setPicking(p => !p)} aria-expanded={picking}
        leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
      >Добавить этап</Button>
    </div>
  );

  if (stages.length === 0) {
    return (
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <StripHeader />
        <EmptyState
          inline compact
          icon={<Workflow size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
          title="Этапов пока нет"
          subtitle="Соберите маршрут из этапов или возьмите готовый состав."
          action={readOnly ? undefined : (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, justifyContent: 'center' }}>
              <Button size="sm" variant="primary" onClick={() => setPicking(true)}>Добавить этап</Button>
              {fullCycleStages && (
                <Button size="sm" variant="ghost" onClick={() => onStagesChange(structuredClone(fullCycleStages))}>
                  Взять состав «Полного цикла»
                </Button>
              )}
            </div>
          )}
        />
        {!readOnly && picking && addButton}
      </div>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <StripHeader
        action={readOnly ? undefined : (
          <Button size="sm" variant="ghost" onClick={() => setPicking(p => !p)} aria-label="Добавить этап в шапке секции">
            + Этап
          </Button>
        )}
      />
      {stages.map((s, i) => {
        // Рамка «параллельно» — оформление обёртки самой строки (боковые рёбра, у крайних — верх/низ),
        // а не контейнер группы: все обёртки лежат под одним родителем и ключуются id этапа,
        // поэтому перестановка не пересаживает строку и не сбрасывает ей фокус и `open`.
        const g = groups.find(x => i >= x.from && i <= x.to);
        const inFrame = !!g?.parallel;
        const first = inFrame && i === g!.from;
        const last = inFrame && i === g!.to;
        const edge = `1px dashed ${C.accentMuted}`;
        const frameStyle: React.CSSProperties = inFrame ? {
          borderLeft: edge, borderRight: edge,
          borderTop: first ? edge : undefined, borderBottom: last ? edge : undefined,
          borderTopLeftRadius: first ? R.xl : 0, borderTopRightRadius: first ? R.xl : 0,
          borderBottomLeftRadius: last ? R.xl : 0, borderBottomRightRadius: last ? R.xl : 0,
          paddingLeft: SP.sm, paddingRight: SP.sm,
          paddingTop: SP.sm, paddingBottom: last ? SP.sm : 0,
          // Внутри группы гасим зазор ленты, чтобы боковые рёбра шли без разрывов.
          marginTop: first ? 0 : -SP.sm,
        } : {};
        return (
          <div key={keys[i]} style={frameStyle}>
            {first && (
              <span style={{ display: 'block', marginBottom: SP.sm, fontSize: FS.xs, fontWeight: 600, color: C.accent, fontFamily: FONT.sans }}>
                ⇉ колонка {g!.column} · параллельно
              </span>
            )}
            <WorkflowStageRow
              stage={s}
              stages={stages}
              index={i}
              errors={errorsFor(s, i)}
              readOnly={readOnly}
              specialties={specialties}
              personas={personas}
              mcpChoices={mcpChoices}
              onChange={next => onStageChange(i, next)}
              onRenameId={newId => onRenameStage(i, newId)}
              onRemove={() => onRemoveStage(i)}
              column={columns[i]}
              afterText={afterNames(stages, i, STAGE_ROLE_LABEL)}
              who={whoLabel(s, persons, specLabels)}
              grip={{ onPointerDown: e => startDrag(i, e), onKeyDown: e => onGripKey(i, e) }}
              dragging={drag?.from === i}
              dropHere={!!drag && drag.over === i && drag.from !== i}
            />
          </div>
        );
      })}
      {!readOnly && addButton}
      <StripHint>
        Параллельность задаёт поле «После»: у кого одни и те же предшественники, те идут одновременно и встают в одну колонку. Следующий этап ждёт всех, кто у него в «после».
      </StripHint>
      <StripHint>
        Два этапа с одной ролью и линией (например, два <span style={{ fontFamily: FONT.mono }}>qa</span>) обязаны иметь разные имена, иначе в списке и на схеме будут две неотличимые «Проверки». У шагов имя не нужно — их различает линия.
      </StripHint>
      <StripHint>
        Новых видов работ шаблон не вводит: этап — одна из ролей задачи. 🔒 — несъёмные, их нельзя выключить.
      </StripHint>
    </div>
  );
}

function StripHeader({ action }: { action?: React.ReactNode }): React.ReactElement {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
      <span style={{
        flex: 1, minWidth: 0, fontSize: FS.xs, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase',
        letterSpacing: '0.05em', fontFamily: FONT.sans,
      }}>Этапы · порядок = маршрут</span>
      <span style={{ fontSize: FS.xs, color: C.textMuted }}>перетащите ⠿</span>
      {action}
    </div>
  );
}

function StripHint({ children }: { children: React.ReactNode }): React.ReactElement {
  return <p style={{ margin: 0, fontSize: FS.xs, color: C.textMuted, lineHeight: 1.5 }}>{children}</p>;
}

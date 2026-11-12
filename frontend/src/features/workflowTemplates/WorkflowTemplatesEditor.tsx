// Редактор шаблонов процесса фичи (feature-workflows, C1b).
// Один экран на две области: глобальные шаблоны (раздел рядом со специальностями, правит
// только admin) и проектные (настройки проекта, правит владелец — временно, до ответа на
// вопрос 3 Андрею). Область задаёт projectId: нет — глобальная, есть — проектная.
//   • список: встроенные помечены, у встроенного — «Сбросить к исходному», удалить можно
//     только свой; в проекте глобальные видны только на чтение и копируются в проектные
//     (копия с basedOn);
//   • редактор: этапы в порядке маршрута (WorkflowStageRow), общая кнопка «Сохранить».
// Конкурентность: кнопка в состоянии busy (двойной клик — один запрос), ответ старого
// шаблона не перезаписывает форму нового (счётчик формы), 409 — «перечитать», 400 — ошибки
// у полей этапов.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, Plus, RotateCcw, Trash2, Workflow } from 'lucide-react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import {
  Badge, Button, ConfirmDialog, EmptyState, Field, Notice, TextArea, TextField,
} from '../../components/ui';
import { useIsMobile } from '../../lib/breakpoints';
import { usePersonas } from '../../lib/personas';
import { useSpecialtyCatalog } from '../../lib/specialties';
import { showToast } from '../../lib/toast';
import { workflowTemplatesApi } from './api';
import { stageErrors } from './workflowTemplateModel';
import {
  CONFLICT_MESSAGE, blankTemplate, normalizeForSave, showWriteButton, forkToProject, saveGuarded, templateSnapshot, splitEntries,
  type SaveFailure,
} from './workflowEditorLogic';
import { WorkflowStageRow } from './WorkflowStageRow';
import type { WorkflowStage, WorkflowTemplate } from './types';

export interface WorkflowTemplatesEditorProps {
  // Нет — глобальная область, есть — проектная.
  projectId?: string;
  // Может ли текущий человек писать в эту область (admin / владелец проекта).
  canWrite: boolean;
  // Admin: только он сбрасывает встроенные пресеты (серверный запрет, ручка {id}/reset).
  isAdmin: boolean;
  // Ключи MCP, уже выданные (шаблон только сужает).
  mcpChoices: string[];
}

type Scope = 'global' | 'project';
interface Selection { scope: Scope; isNew: boolean }

const STAGE_ERR_RE = /^этап «([^»]+)»:\s*(.*)$/;

export function WorkflowTemplatesEditor({
  projectId, canWrite, isAdmin, mcpChoices,
}: WorkflowTemplatesEditorProps): React.ReactElement {
  const isMobile = useIsMobile();
  const catalog = useSpecialtyCatalog();
  const personas = usePersonas();
  const projectMode = !!projectId;

  const [globals, setGlobals] = useState<WorkflowTemplate[]>([]);
  const [locals, setLocals] = useState<WorkflowTemplate[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [draft, setDraft] = useState<WorkflowTemplate | null>(null);
  const [sel, setSel] = useState<Selection | null>(null);
  const [baseSnap, setBaseSnap] = useState('');
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<SaveFailure | null>(null);
  const [confirm, setConfirm] = useState<'delete' | 'reset' | null>(null);

  // Счётчик формы: растёт при каждой смене показанного шаблона; ответ на сохранение,
  // отправленное для другого значения, форму не трогает. busyRef — синхронная защита от
  // двойного клика (state обновится только на следующем рендере).
  const formSeq = useRef(0);
  const busyRef = useRef(false);

  const load = useCallback(async (): Promise<{ globals: WorkflowTemplate[]; locals: WorkflowTemplate[] }> => {
    // Один GET: с projectId сервер отдаёт глобальные плюс проектные, область — в entry.scope.
    return splitEntries(await workflowTemplatesApi.listTemplates(projectId), !!projectId);
  }, [projectId]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setLoadError(null);
    formSeq.current += 1;
    setDraft(null);
    setSel(null);
    load()
      .then(r => { if (!cancelled) { setGlobals(r.globals); setLocals(r.locals); } })
      .catch(e => { if (!cancelled) setLoadError(e instanceof Error ? e.message : 'Не удалось загрузить шаблоны'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [load]);

  const open = (tpl: WorkflowTemplate, scope: Scope, isNew = false) => {
    formSeq.current += 1;
    setDraft(structuredClone(tpl));
    setSel({ scope, isNew });
    setBaseSnap(isNew ? '' : templateSnapshot(tpl));
    setFailure(null);
  };
  const close = () => { formSeq.current += 1; setDraft(null); setSel(null); setFailure(null); };

  // В проекте глобальные — только чтение; в глобальной области писать может только canWrite.
  const readOnly = !canWrite || (projectMode && sel?.scope === 'global');
  const dirty = !!draft && templateSnapshot(draft) !== baseSnap;
  const canSave = !!draft && !readOnly && dirty && !busy && draft.name.trim() !== '';

  // Подсказки модели + ответ валидатора — по ролям этапов (у полей, а не общей плашкой).
  const errorsByRole = useMemo(() => {
    const map: Record<string, string[]> = {};
    const push = (role: string, msg: string) => {
      const arr = (map[role] ??= []);
      if (!arr.includes(msg)) arr.push(msg);
    };
    for (const line of draft ? stageErrors(draft) : []) {
      const m = STAGE_ERR_RE.exec(line);
      if (m) push(m[1], m[2]);
    }
    for (const [role, msgs] of Object.entries(failure?.stageErrors ?? {})) msgs.forEach(m => push(role, m));
    return map;
  }, [draft, failure]);

  const applySaved = (saved: WorkflowTemplate, scope: Scope) => {
    const setList = scope === 'global' ? setGlobals : setLocals;
    // В глобальной области locals == globals по построению load(): держим оба в одном виде.
    setList(list => list.some(t => t.id === saved.id)
      ? list.map(t => (t.id === saved.id ? saved : t))
      : [...list, saved]);
    if (!projectMode) setLocals(list => list.some(t => t.id === saved.id)
      ? list.map(t => (t.id === saved.id ? saved : t))
      : [...list, saved]);
  };

  const handleSave = async () => {
    if (!draft || !sel || busyRef.current || readOnly) return;
    busyRef.current = true;
    setBusy(true);
    setFailure(null);
    const scope = sel.scope;
    const sentSeq = formSeq.current;
    const res = await saveGuarded({
      tpl: normalizeForSave(draft),
      save: t => workflowTemplatesApi.saveTemplate(t, scope === 'project' ? projectId : undefined),
      activeKey: () => String(formSeq.current),
    });
    busyRef.current = false;
    setBusy(false);
    if (res.kind === 'applied' || res.kind === 'stale') {
      applySaved(res.saved, scope);
      if (res.kind === 'applied') {
        setDraft(structuredClone(res.saved));
        setSel({ scope, isNew: false });
        setBaseSnap(templateSnapshot(res.saved));
        showToast('Шаблоны процесса', 'Шаблон сохранён', 'success');
      }
      return;
    }
    // Отказ показываем, только если форма всё ещё та же; отказ по ушедшей форме не нужен.
    if (formSeq.current === sentSeq) setFailure(res);
  };

  const handleReload = async () => {
    try {
      const r = await load();
      setGlobals(r.globals);
      setLocals(r.locals);
      const id = draft?.id;
      const fresh = id ? (sel?.scope === 'global' ? r.globals : r.locals).find(t => t.id === id) : undefined;
      if (fresh && sel) open(fresh, sel.scope); else close();
    } catch (e) {
      setFailure({
        kind: 'failed', message: e instanceof Error ? e.message : 'Не удалось перечитать',
        stageErrors: {}, generalErrors: [],
      });
    }
  };

  const handleDelete = async () => {
    setConfirm(null);
    if (!draft || !sel || sel.isNew) return;
    const scope = sel.scope;
    try {
      await workflowTemplatesApi.deleteTemplate(draft.id, scope === 'project' ? projectId : undefined);
      const drop = (list: WorkflowTemplate[]) => list.filter(t => t.id !== draft.id);
      if (scope === 'project') setLocals(drop); else { setGlobals(drop); if (!projectMode) setLocals(drop); }
      close();
    } catch (e) {
      setFailure({
        kind: 'failed', message: e instanceof Error ? e.message : 'Не удалось удалить шаблон',
        stageErrors: {}, generalErrors: [],
      });
    }
  };

  const handleReset = async () => {
    setConfirm(null);
    if (!draft || sel?.isNew) return;
    try {
      const fresh = await workflowTemplatesApi.resetBuiltIn(draft.id);
      applySaved(fresh, 'global');
      open(fresh, 'global');
    } catch (e) {
      setFailure({
        kind: 'failed', message: e instanceof Error ? e.message : 'Не удалось сбросить шаблон',
        stageErrors: {}, generalErrors: [],
      });
    }
  };

  const setStage = (idx: number, next: WorkflowStage) =>
    setDraft(d => (d ? { ...d, stages: d.stages.map((s, i) => (i === idx ? next : s)) } : d));

  // === Список ===
  const renderItem = (t: WorkflowTemplate, scope: Scope) => {
    const active = sel?.scope === scope && !sel.isNew && draft?.id === t.id;
    return (
      <button
        key={`${scope}:${t.id}`}
        type="button"
        onClick={() => open(t, scope)}
        aria-current={active ? 'true' : undefined}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, width: '100%', minHeight: 44,
          padding: `${SP.sm}px ${SP.md}px`, textAlign: 'left', cursor: 'pointer',
          fontFamily: FONT.sans, fontSize: FS.base, color: C.textHeading,
          background: active ? C.accentLight : C.bgWhite,
          border: `1px solid ${active ? C.accent : C.border}`, borderRadius: R.lg,
        }}
      >
        <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: 600 }}>
          {t.name}
        </span>
        {t.builtInKey && <Badge tone="accent" size="xs">встроенный</Badge>}
        {t.basedOn && <Badge tone="neutral" size="xs" title={`Основан на «${t.basedOn.templateId}» v${t.basedOn.version}`}>копия</Badge>}
      </button>
    );
  };

  const listBlock = (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      {projectMode && <ListLabel>Проектные</ListLabel>}
      {locals.length === 0 && projectMode && (
        <div style={{ fontSize: FS.sm, color: C.textMuted }}>Своих шаблонов у проекта нет.</div>
      )}
      {(projectMode ? locals : globals).map(t => renderItem(t, projectMode ? 'project' : 'global'))}
      {canWrite && (
        <Button
          variant="dashed" size="sm" fullWidth
          leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          onClick={() => open(blankTemplate(), projectMode ? 'project' : 'global', true)}
        >Новый шаблон</Button>
      )}
      {projectMode && globals.length > 0 && (
        <>
          <ListLabel>Глобальные</ListLabel>
          {globals.map(t => renderItem(t, 'global'))}
        </>
      )}
    </div>
  );

  // === Редактор ===
  const editor = draft && sel && (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.lg, minWidth: 0 }}>
      {isMobile && (
        <div>
          <Button variant="ghost" size="sm" onClick={close}>← К списку</Button>
        </div>
      )}
      {readOnly && (
        <Notice tone="info" icon={AlertTriangle}
          action={projectMode && sel.scope === 'global' && canWrite ? (
            <Button size="sm" variant="secondary" onClick={() => open(forkToProject(draft), 'project', true)}>
              Создать проектный
            </Button>
          ) : undefined}
        >
          {projectMode && sel.scope === 'global'
            ? 'Глобальный шаблон правит администратор. Здесь его можно скопировать в проект.'
            : 'Править шаблоны может только администратор.'}
        </Notice>
      )}
      {failure && (
        <Notice tone="danger" icon={AlertTriangle} title={failure.message}
          action={failure.kind === 'conflict' ? <Button size="sm" variant="secondary" onClick={() => void handleReload()}>Перечитать</Button> : undefined}
        >
          {failure.generalErrors.map((e, i) => <div key={i}>{e}</div>)}
          {failure.kind === 'conflict' && <div>{CONFLICT_MESSAGE}</div>}
        </Notice>
      )}

      <Field label="Название">
        <TextField value={draft.name} onChange={v => setDraft(d => (d ? { ...d, name: v } : d))} disabled={readOnly} />
      </Field>
      <Field label="Описание" hint="Читает рекомендатель маршрута, когда выбирает шаблон под фичу.">
        <TextArea
          value={draft.description ?? ''}
          onChange={v => setDraft(d => (d ? { ...d, description: v } : d))}
          readOnly={readOnly} autoGrow minHeight={60} maxHeight={200}
        />
      </Field>

      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <ListLabel>Этапы маршрута</ListLabel>
        {draft.stages.map((s, i) => (
          <WorkflowStageRow
            key={`${s.role}:${i}`}
            stage={s}
            errors={errorsByRole[s.role] ?? []}
            readOnly={readOnly}
            specialties={catalog ?? []}
            personas={personas}
            mcpChoices={mcpChoices}
            onChange={next => setStage(i, next)}
          />
        ))}
      </div>

      <div style={{ display: 'flex', flexWrap: 'wrap', gap: SP.sm, alignItems: 'center' }}>
        {showWriteButton(canWrite, readOnly) && (
          <Button onClick={() => void handleSave()} disabled={!canSave} loading={busy}>
            {busy ? 'Сохраняю…' : 'Сохранить'}
          </Button>
        )}
        {dirty && !busy && !readOnly && <span style={{ fontSize: FS.xs, color: C.textMuted }}>Есть несохранённые изменения</span>}
        <div style={{ flex: 1 }} />
        {!sel.isNew && draft.builtInKey && isAdmin && !projectMode && (
          <Button variant="ghost" size="sm" onClick={() => setConfirm('reset')}
            leftIcon={<RotateCcw size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}>Сбросить к исходному</Button>
        )}
        {!sel.isNew && !draft.builtInKey && canWrite && !readOnly && (
          <Button variant="danger" size="sm" onClick={() => setConfirm('delete')}
            leftIcon={<Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}>Удалить</Button>
        )}
      </div>
    </div>
  );

  if (loading) return <div style={{ padding: SP.lg, color: C.textMuted, fontSize: FS.sm }}>Загружаю шаблоны…</div>;
  if (loadError) {
    return (
      <Notice tone="danger" icon={AlertTriangle} title="Шаблоны не загрузились">{loadError}</Notice>
    );
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.lg, minWidth: 0 }}>
      {isMobile ? (editor || listBlock) : (
        <div style={{ display: 'flex', gap: SP.xl, alignItems: 'flex-start' }}>
          <div style={{ width: 260, flexShrink: 0 }}>{listBlock}</div>
          <div style={{ flex: 1, minWidth: 0 }}>
            {editor || (
              <EmptyState icon={<Workflow size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />} title="Выберите шаблон" subtitle="Слева — шаблоны процесса; откройте один, чтобы править этапы." />
            )}
          </div>
        </div>
      )}
      {confirm && draft && (
        <ConfirmDialog
          title={confirm === 'delete' ? 'Удалить шаблон?' : 'Сбросить к исходному?'}
          subtitle={confirm === 'delete'
            ? `Шаблон «${draft.name}» будет удалён.`
            : `Содержимое «${draft.name}» вернётся к коду; ваши правки пропадут.`}
          confirmLabel={confirm === 'delete' ? 'Удалить' : 'Сбросить'}
          cancelLabel="Отмена"
          confirmVariant="danger"
          onConfirm={() => void (confirm === 'delete' ? handleDelete() : handleReset())}
          onCancel={() => setConfirm(null)}
        />
      )}
    </div>
  );
}

function ListLabel({ children }: { children: React.ReactNode }): React.ReactElement {
  return (
    <div style={{
      fontSize: FS.xs, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase',
      letterSpacing: '0.05em', fontFamily: FONT.sans,
    }}>{children}</div>
  );
}

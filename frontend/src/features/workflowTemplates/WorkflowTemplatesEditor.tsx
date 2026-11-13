// Редактор шаблонов процесса фичи (feature-workflows, C1b).
// Один экран на две области: глобальные шаблоны (раздел «Процессы фичи» рядом со
// специальностями, правит только admin) и проектные (секция «Процессы» в настройках
// проекта, правит владелец — временно, до ответа на вопрос 3 Андрею). Область задаёт
// projectId: нет — глобальная, есть — проектная.
//   • список (экран 1, F22): остров с шапкой «Процессы» + «+ Новый»; секции «Глобальные ·
//     правит админ» (сверху) и «Проект «Имя» · правит владелец»; у элемента — мета-строка
//     B08 (itemMetaLine); активный — белая карточка с тенью, наведённый — bg-selected,
//     на мобиле высота ≥56; черновик нового — строка «Новый шаблон» с тегом «не сохранён»;
//     пустой проект — «Других своих процессов в проекте нет» + «Копия глобального…»;
//     встроенные помечены, у встроенного — «Сбросить к исходному», удалить можно
//     только свой; в проекте глобальные видны только на чтение и копируются в проектные
//     (копия с basedOn);
//   • редактор: этапы в порядке маршрута (WorkflowStageStrip); клик по строке выбирает этап и
//     открывает «Настройку этапа» (WorkflowStageSettings: шторка 540px, на мобиле — экран, F23);
//     общая кнопка «Сохранить».
// Конкурентность: кнопка в состоянии busy (двойной клик — один запрос), ответ старого
// шаблона не перезаписывает форму нового (счётчик формы), 409 — «перечитать», 400 — ошибки
// у полей этапов.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AlertTriangle, Workflow } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import {
  Badge, Button, ConfirmDialog, EmptyState, Field, Island, IslandHeader, Notice, TextArea, TextField,
} from '../../components/ui';
import { useIsMobile } from '../../lib/breakpoints';
import { usePersonas } from '../../lib/personas';
import { useSpecialtyCatalog } from '../../lib/specialties';
import { showToast } from '../../lib/toast';
import { workflowTemplatesApi } from './api';
import { stageErrors } from './workflowTemplateModel';
import {
  blankTemplate, normalizeForSave, forkToProject, saveGuarded,
  templateSnapshot, splitEntries, entryMetaMap, toolbarSubtitle, errorSummaryItems,
  discardChanges, saveAsNew, canSave as canSaveDraft, failureAfterEdit,
  itemMetaLine, draftMetaLine,
  type SaveFailure,
} from './workflowEditorLogic';
import { WorkflowStageStrip } from './WorkflowStageStrip';
import { WorkflowStageSettings } from './WorkflowStageSettings';
import { EditorFooter, EditorNotices } from './WorkflowEditorParts';
import { WorkflowGraphPreview } from './WorkflowGraphPreview';
import { normalizeStages, removeStageReconnect, renameStageId } from './workflowGraphModel';
import { stageKeys } from './workflowStripModel';
import type { WorkflowEntryMeta, WorkflowStage, WorkflowTemplate } from './types';

export interface WorkflowTemplatesEditorProps {
  // Нет — глобальная область, есть — проектная.
  projectId?: string;
  // Имя проекта для подписи секции «Проект «Имя» · правит владелец».
  projectName?: string;
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
  projectId, projectName, canWrite, isAdmin, mcpChoices,
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
  // Последний сохранённый вид открытого шаблона — к нему возвращает «Отменить».
  const [savedTpl, setSavedTpl] = useState<WorkflowTemplate | null>(null);
  const [metas, setMetas] = useState<Record<string, WorkflowEntryMeta>>({});
  // Версия на сервере при 409 — для «теперь это vN» и «Загрузить vN».
  const [conflictVersion, setConflictVersion] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<SaveFailure | null>(null);
  const [confirm, setConfirm] = useState<'delete' | 'reset' | null>(null);
  // Выбранный этап (ключ из stageKeys): открывает «Настройку этапа»; у сохранённого шаблона и у
  // нового — свой, поэтому сбрасывается при каждой смене показанного шаблона.
  const [selectedStageId, setSelectedStageId] = useState<string | null>(null);

  // Счётчик формы: растёт при каждой смене показанного шаблона; ответ на сохранение,
  // отправленное для другого значения, форму не трогает. busyRef — синхронная защита от
  // двойного клика (state обновится только на следующем рендере).
  const formSeq = useRef(0);
  const busyRef = useRef(false);

  const load = useCallback(async (): Promise<{
    globals: WorkflowTemplate[]; locals: WorkflowTemplate[]; metas: Record<string, WorkflowEntryMeta>;
  }> => {
    // Один GET: с projectId сервер отдаёт глобальные плюс проектные, область — в entry.scope.
    const entries = await workflowTemplatesApi.listTemplates(projectId);
    return { ...splitEntries(entries, !!projectId), metas: entryMetaMap(entries) };
  }, [projectId]);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setLoadError(null);
    formSeq.current += 1;
    setDraft(null);
    setSel(null);
    load()
      .then(r => { if (!cancelled) { setGlobals(r.globals); setLocals(r.locals); setMetas(r.metas); } })
      .catch(e => { if (!cancelled) setLoadError(e instanceof Error ? e.message : 'Не удалось загрузить шаблоны'); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [load]);

  const open = (tpl: WorkflowTemplate, scope: Scope, isNew = false) => {
    formSeq.current += 1;
    setDraft(structuredClone(tpl));
    setSelectedStageId(null);
    setSel({ scope, isNew });
    setBaseSnap(isNew ? '' : templateSnapshot(tpl));
    setSavedTpl(isNew ? null : structuredClone(tpl));
    setFailure(null);
    setConflictVersion(null);
  };
  const close = () => { formSeq.current += 1; setDraft(null); setSelectedStageId(null); setSel(null); setFailure(null); setConflictVersion(null); };

  // В проекте глобальные — только чтение; в глобальной области писать может только canWrite.
  const readOnly = !canWrite || (projectMode && sel?.scope === 'global');
  const dirty = !!draft && templateSnapshot(draft) !== baseSnap;
  const canSave = !!draft && !readOnly && dirty && !busy && draft.name.trim() !== '' && canSaveDraft(draft, failure);
  const errorItems = draft && !readOnly ? errorSummaryItems(draft, failure) : [];

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
        setSavedTpl(structuredClone(res.saved));
        void load().then(r => setMetas(r.metas)).catch(() => undefined);
        showToast('Шаблоны процесса', 'Шаблон сохранён', 'success');
      }
      return;
    }
    // Отказ показываем, только если форма всё ещё та же; отказ по ушедшей форме не нужен.
    if (formSeq.current !== sentSeq) return;
    setFailure(res);
    if (res.kind === 'conflict') {
      // Версию сервера узнаём перечитыванием списка; не вышло — текст без номера.
      try {
        const r = await load();
        if (formSeq.current !== sentSeq) return;
        const fresh = (scope === 'global' ? r.globals : r.locals).find(t => t.id === draft.id);
        setConflictVersion(fresh?.version ?? null);
      } catch { setConflictVersion(null); }
    }
  };

  const handleSaveAsNew = () => {
    if (!draft || !sel) return;
    open(saveAsNew(draft), sel.scope, true);
  };

  const handleReload = async () => {
    try {
      const r = await load();
      setGlobals(r.globals);
      setLocals(r.locals);
      setMetas(r.metas);
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

  // Любая правка полей и этапов снимает отказ 400, чтобы «Сохранить» не оставалась серой.
  const editDraft = (fn: (d: WorkflowTemplate | null) => WorkflowTemplate | null) => {
    setDraft(fn);
    setFailure(failureAfterEdit);
  };

  const renameStage = (oldId: string, newId: string) => {
    editDraft(d => (d ? { ...d, stages: renameStageId(d.stages, oldId, newId) } : d));
    // Ключ выбранного — его id: при переименовании выбор едет за этапом.
    setSelectedStageId(cur => (cur === oldId ? newId : cur));
  };
  const dropStage = (id: string) => {
    editDraft(d => (d ? { ...d, stages: removeStageReconnect(d.stages, id) } : d));
    setSelectedStageId(cur => (cur === id ? null : cur));
  };

  const setStage = (idx: number, next: WorkflowStage) =>
    editDraft(d => (d ? { ...d, stages: d.stages.map((s, i) => (i === idx ? next : s)) } : d));

  // === Список (экран 1, F22) ===
  // Элемент — две строки (имя с тегом + мета B08), без рамки: обычный — прозрачный,
  // наведённый — bg-selected, активный — белая карточка с тенью (не accent-подсветка).
  // Мобильная высота ≥56 (T10). Черновик нового — строка с тегом «не сохранён» (T12):
  // она активна всегда, когда открыт, и не кликается — редактор уже открыт.
  const itemStyle = (active: boolean): React.CSSProperties => ({
    display: 'flex', flexDirection: 'column', gap: SP.xxs, width: '100%',
    padding: `${SP.sm}px ${SP.md}px`, textAlign: 'left', cursor: 'pointer',
    fontFamily: FONT.sans, fontSize: FS.base,
    background: active ? C.bgWhite : 'transparent',
    boxShadow: active ? SHADOW.card : 'none',
    border: 'none', borderRadius: R.lg,
    ...(isMobile ? { minHeight: 56, justifyContent: 'center' as const } : {}),
  });
  const itemNameStyle: React.CSSProperties = {
    flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
    fontWeight: 600, color: C.textHeading,
  };
  const itemMetaStyle: React.CSSProperties = { fontSize: FS.xs, color: C.textMuted };
  const hoverIn = (e: React.MouseEvent<HTMLButtonElement>, active: boolean) => {
    if (!active) e.currentTarget.style.background = C.bgSelected;
  };
  const hoverOut = (e: React.MouseEvent<HTMLButtonElement>, active: boolean) => {
    // «transparent» — как в itemStyle: возвращаем именно то состояние, что было до наведения.
    if (!active) e.currentTarget.style.background = 'transparent';
  };

  const renderItem = (t: WorkflowTemplate, scope: Scope) => {
    const active = sel?.scope === scope && !sel.isNew && draft?.id === t.id;
    const metaLine = itemMetaLine(t, metas[`${scope}:${t.id}`]);
    return (
      <button
        key={`${scope}:${t.id}`}
        type="button"
        onClick={() => open(t, scope)}
        aria-current={active ? 'true' : undefined}
        onMouseEnter={e => hoverIn(e, active)}
        onMouseLeave={e => hoverOut(e, active)}
        style={itemStyle(active)}
      >
        <span style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
          <span style={itemNameStyle}>{t.name}</span>
          {t.builtInKey && <Badge tone="accent" size="xs">встроенный</Badge>}
          {t.basedOn && <Badge tone="neutral" size="xs" title={`Основан на «${t.basedOn.templateId}» v${t.basedOn.version}`}>копия</Badge>}
        </span>
        {metaLine && <span style={itemMetaStyle}>{metaLine}</span>}
      </button>
    );
  };

  const draftRow = draft && (
    <div aria-current="true" style={itemStyle(true)}>
      <span style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
        <span style={itemNameStyle}>{draft.name.trim() || 'Новый шаблон'}</span>
        <Badge tone="warning" size="xs">не сохранён</Badge>
      </span>
      <span style={itemMetaStyle}>{draftMetaLine(draft.stages)}</span>
    </div>
  );

  const emptyProjectBlock = (
    <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: SP.sm, padding: `${SP.md}px` }}>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, textAlign: 'center' }}>
        Других своих процессов в проекте нет — фичи берут глобальные.
      </div>
      {canWrite && globals.length > 0 && (
        <Button variant="ghost" size="xs" dataTestId="copy-global" onClick={() => open(forkToProject(globals[0]), 'project', true)}>
          Копия глобального…
        </Button>
      )}
    </div>
  );

  const listBlock = (
    <Island>
      <IslandHeader
        title="Процессы"
        actions={canWrite ? (
          <Button variant="ghost" size="xs" title="Новый шаблон" dataTestId="new-template"
            onClick={() => open(blankTemplate(), projectMode ? 'project' : 'global', true)}>
            + Новый
          </Button>
        ) : undefined}
      />
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, padding: SP.md, minWidth: 0 }}>
        <ListLabel>Глобальные · правит админ</ListLabel>
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
          {sel && sel.isNew && sel.scope === 'global' && draftRow}
          {globals.map(t => renderItem(t, 'global'))}
        </div>
        {projectMode && (
          <>
            <ListLabel>Проект «{projectName ?? ''}» · правит владелец</ListLabel>
            <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
              {sel && sel.isNew && sel.scope === 'project' && draftRow}
              {locals.map(t => renderItem(t, 'project'))}
              {locals.length === 0 && emptyProjectBlock}
            </div>
          </>
        )}
      </div>
    </Island>
  );

  // === Редактор ===
  const isBuiltIn = !!draft?.builtInKey && !sel?.isNew;
  const meta = draft && sel ? metas[`${sel.scope}:${draft.id}`] : undefined;
  const subtitle = draft && sel && !sel.isNew ? toolbarSubtitle(draft, meta) : null;
  const basedOnGlobal = meta?.basedOn ? globals.find(t => t.id === meta.basedOn!.templateId) : undefined;
  const jumpToStage = (index: number) => {
    const el = document.querySelector(`[data-stage-index="${index}"]`);
    if (el instanceof HTMLElement) { el.scrollIntoView({ block: 'center' }); el.focus?.(); }
  };
  // Индекс выбранного этапа (-1 — не выбран или уже убран). Настройка открыта при выборе; у пустого
  // шаблона на десктопе показывает «Этап не выбран» (макет T50), на мобиле пустую шторку не открываем.
  const settingsIndex = draft && selectedStageId !== null ? stageKeys(draft.stages).indexOf(selectedStageId) : -1;
  const settingsStage = draft && settingsIndex >= 0 ? draft.stages[settingsIndex] : null;
  const settingsOpen = settingsStage !== null || (!!draft && !isMobile && draft.stages.length === 0);
  const closeSettings = useCallback(() => setSelectedStageId(null), []);
  const editor = draft && sel && (
    <div style={{ display: 'flex', flexDirection: 'column', gap: SP.lg, minWidth: 0 }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0 }}>
        {isMobile && (
          <div>
            <Button variant="ghost" size="sm" onClick={close}>‹ Процессы</Button>
          </div>
        )}
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', minWidth: 0 }}>
          <span style={{
            fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 600, color: C.textHeading,
            minWidth: 0, overflowWrap: 'anywhere',
          }}>{draft.name.trim() || 'Новый шаблон'}</span>
          {isBuiltIn && <Badge tone="accent" size="xs">встроенный</Badge>}
          {sel.scope === 'project' && <Badge tone="neutral" size="xs">проект</Badge>}
        </div>
        {subtitle && (
          <div style={{ fontSize: FS.sm, color: C.textMuted, fontFamily: FONT.sans }}>
            {subtitle.text}
            {subtitle.staleNote && (
              <>
                {' — '}
                {basedOnGlobal ? (
                  <button
                    type="button" onClick={() => open(basedOnGlobal, 'global')}
                    style={{
                      background: 'none', border: 'none', padding: 0, cursor: 'pointer', font: 'inherit',
                      color: C.accent, textDecoration: 'underline',
                    }}
                  >{subtitle.staleNote}</button>
                ) : subtitle.staleNote}
              </>
            )}
          </div>
        )}
      </div>
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
      <EditorNotices
        isBuiltIn={isBuiltIn} readOnly={readOnly} canReset={isAdmin && !projectMode}
        failure={failure} conflictVersion={conflictVersion} errorItems={errorItems}
        onReset={() => setConfirm('reset')} onReload={() => void handleReload()}
        onSaveAsNew={handleSaveAsNew} onJumpToStage={jumpToStage}
      />

      <Field label="Название">
        <TextField value={draft.name} onChange={v => editDraft(d => (d ? { ...d, name: v } : d))} disabled={readOnly} />
      </Field>
      <Field label="Описание" hint="Читает рекомендатель маршрута, когда выбирает шаблон под фичу.">
        <TextArea
          value={draft.description ?? ''}
          onChange={v => editDraft(d => (d ? { ...d, description: v } : d))}
          readOnly={readOnly} autoGrow minHeight={60} maxHeight={200}
        />
      </Field>

      <div style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <ListLabel>Маршрут, который увидит фича</ListLabel>
        <WorkflowGraphPreview
          stages={draft.stages}
          selectedKey={settingsIndex >= 0 ? selectedStageId : null}
          onSelectStage={setSelectedStageId}
        />
        <WorkflowStageStrip
          stages={draft.stages}
          readOnly={readOnly}
          specialties={catalog ?? []}
          personas={personas}
          errorsFor={(s, i) => errorsByRole[normalizeStages(draft.stages)[i].id ?? s.role] ?? errorsByRole[s.role] ?? []}
          onStagesChange={stages => editDraft(d => (d ? { ...d, stages } : d))}
          selectedKey={settingsIndex >= 0 ? selectedStageId : null}
          onSelectStage={setSelectedStageId}
          fullCycleStages={globals.find(t => t.builtInKey === 'full-cycle')?.stages ?? null}
        />
      </div>

      {settingsOpen && (
        <WorkflowStageSettings
          stage={settingsStage}
          stages={draft.stages}
          index={Math.max(settingsIndex, 0)}
          readOnly={readOnly}
          specialties={catalog ?? []}
          personas={personas}
          mcpChoices={mcpChoices}
          onChange={next => setStage(settingsIndex, next)}
          onRenameId={newId => renameStage(selectedStageId ?? '', newId)}
          onRemove={() => dropStage(selectedStageId ?? '')}
          onClose={closeSettings}
        />
      )}

      <EditorFooter
        isNew={sel.isNew} isBuiltIn={isBuiltIn} isMobile={isMobile} canWrite={canWrite} readOnly={readOnly}
        canSave={canSave} dirty={dirty} busy={busy}
        onDelete={() => setConfirm('delete')}
        onSave={() => void handleSave()}
        onDiscard={() => {
          if (sel.isNew || !savedTpl) { close(); return; }
          setDraft(discardChanges(savedTpl)); setFailure(null); setConflictVersion(null);
        }}
      />
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

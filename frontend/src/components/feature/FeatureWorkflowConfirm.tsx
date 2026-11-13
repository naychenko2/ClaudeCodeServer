// Блок подтверждения маршрута фичи (feature-workflows, C2; разрез docs/research/
// ai-fabrika-workflow-cut-2026-11.md §4–§5): показывает предложение модели (шаблон и
// обоснование), даёт сменить шаблон и снять необязательные этапы, «Подтвердить маршрут»
// → confirmWorkflow, «Подобрать маршрут» → recommendWorkflow (обе ручки из
// features/workflowTemplates/api.ts). Подтверждает только человек (И7): после подтверждения
// сервер вещает task_changed, своего refetch эпика здесь нет.
//
// Состояние (выбор шаблона, снятие этапов, локальное предложение) привязано к featureId:
// при смене фичи оно сбрасывается, идущий «Подобрать» прерывается AbortSignal, а его ответ
// всё равно не применился бы (applyRecommend в модели). Двойной клик любой кнопки — один
// запрос: ref-guard синхронный, state busy догорает до ответа. Ошибка — текст в блоке
// (role=alert), необработанных rejection нет.
//
// Этапы — по id: порядок как в маршруте (normalizeStages, G6a), линия (бэк/фронт) — рядом
// с подписью роли, только когда задана; переключатель — только у съёмных optional.
// Вид карточки — как FeatureAskBlock/FeaturePageAlert: поля SP.md, R.lg, спокойный тон
// bgSelected/borderLight; тревожная строка («шаблон изменился», отказ) — токены Notice.
// Мобила 360: кнопки полной шириной, тач-цели ≥40 (Checkbox — 40×40 из ui-кита).
// Встраивание в шапку страницы фичи и мини-граф — вне этого шага (C3b / хвосты).

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Badge, Button, Checkbox, Select } from '../../components/ui';
import { laneWord } from '../../lib/roleLabel';
import { workflowTemplatesApi } from '../../features/workflowTemplates/api';
import { normalizeStages } from '../../features/workflowTemplates/workflowGraphModel';
import { STAGE_ROLE_LABEL } from '../../features/workflowTemplates/WorkflowStageRow';
import {
  applyRecommend, confirmBodyOf, initialRemovedIds, isRemovableStage,
  pickInitialTemplate, templateUpdateText, type ConfirmedRoute,
} from './FeatureWorkflowConfirmModel';
import type { WorkflowProposal, WorkflowTemplateEntry, WorkflowTemplateScope } from '../../features/workflowTemplates/types';

export interface FeatureWorkflowConfirmProps {
  // id эпика: ключ состояния и обеих ручек. Смена — выбор сбрасывается.
  featureId: string;
  // Область каталога шаблонов: нет — глобальная, есть — глобальная + проектная.
  projectId?: string;
  // epic.workflowProposal как есть (null/нет — предложения нет: предлагает человек).
  proposal?: WorkflowProposal | null;
  // epic.workflow как есть (null/нет — маршрут не подтверждён).
  confirmed?: ConfirmedRoute | null;
  isMobile: boolean;
}

interface SelectedKey { id: string; scope: WorkflowTemplateScope }

const errText = (e: unknown): string =>
  e instanceof Error && e.message ? e.message : 'Не получилось, попробуйте ещё раз';

export function FeatureWorkflowConfirm({
  featureId, projectId, proposal, confirmed, isMobile,
}: FeatureWorkflowConfirmProps): React.ReactElement {
  const [entries, setEntries] = useState<WorkflowTemplateEntry[] | null>(null);
  const [selKey, setSelKey] = useState<SelectedKey | null>(null);
  // null — человек ничего не трогал: снятие берётся из предложения модели.
  const [removedTouched, setRemovedTouched] = useState<string[] | null>(null);
  const [localProposal, setLocalProposal] = useState<WorkflowProposal | null>(null);
  const [busyConfirm, setBusyConfirm] = useState(false);
  const [busyRecommend, setBusyRecommend] = useState(false);
  const [errorText, setErrorText] = useState<string | null>(null);

  const featureIdRef = useRef(featureId);
  // ref-guard, а не state: state обновился бы только со следующим рендером, а двойной
  // клик приходит двумя обработчиками за один (тот же приём, что busyRef в редакторе).
  const confirmBusyRef = useRef(false);
  const recommendBusyRef = useRef(false);
  const abortRef = useRef<AbortController | null>(null);

  // Смена фичи — синхронный сброс до рендера: выбор, снятие и локальное предложение
  // принадлежат одной фиче; идущий recommend прерывается (его ответ всё равно не применился бы).
  if (featureIdRef.current !== featureId) {
    featureIdRef.current = featureId;
    setSelKey(null);
    setRemovedTouched(null);
    setLocalProposal(null);
    setErrorText(null);
    abortRef.current?.abort();
  }

  const load = useCallback(() => workflowTemplatesApi.listTemplates(projectId), [projectId]);
  useEffect(() => {
    let cancelled = false;
    setEntries(null);
    load()
      .then(r => { if (!cancelled) setEntries(r); })
      .catch(e => { if (!cancelled) { setEntries([]); setErrorText(errText(e)); } });
    return () => { cancelled = true; };
  }, [load]);

  // Уход с экрана: висящий recommend (модель ждёт десятки секунд) не должен доживать здесь.
  useEffect(() => () => { abortRef.current?.abort(); }, []);

  const shownProposal = localProposal ?? proposal ?? null;
  const selectedEntry = useMemo(() => {
    if (!entries) return null;
    if (selKey) return entries.find(e => e.template?.id === selKey.id && e.scope === selKey.scope) ?? null;
    return pickInitialTemplate(entries, shownProposal, confirmed ?? null);
  }, [entries, selKey, shownProposal, confirmed]);

  const removedIds = useMemo(
    () => (selectedEntry ? removedTouched ?? initialRemovedIds(selectedEntry.template, shownProposal) : []),
    [selectedEntry, removedTouched, shownProposal],
  );
  const removedSet = useMemo(() => new Set(removedIds), [removedIds]);

  const toggleRemoved = (id: string) => {
    setRemovedTouched(removedSet.has(id) ? removedIds.filter(x => x !== id) : [...removedIds, id]);
  };

  const pickTemplate = (key: string) => {
    // Ключ Select — «scope:id» (один id может жить в двух областях). Смена шаблона —
    // новое начало: снятие предложений к другому шаблону не переносим.
    const sep = key.indexOf(':');
    const scope = key.slice(0, sep) as WorkflowTemplateScope;
    const id = key.slice(sep + 1);
    setSelKey({ id, scope });
    setRemovedTouched(null);
  };

  const onConfirm = () => {
    const entry = selectedEntry;
    if (!entry || confirmBusyRef.current) return;
    confirmBusyRef.current = true;
    setBusyConfirm(true);
    setErrorText(null);
    workflowTemplatesApi
      // Состояние эпика после подтверждения придёт штатным task_changed — здесь только
      // отказ показать; успешный ответ тела не имеет.
      .confirmWorkflow(featureId, confirmBodyOf(entry.template, entry.scope, removedIds))
      .catch(e => setErrorText(errText(e)))
      .finally(() => { confirmBusyRef.current = false; setBusyConfirm(false); });
  };

  const onRecommend = () => {
    if (recommendBusyRef.current) return;
    // Фича на момент запроса: ответ сверяется с ней же (applyRecommend), а не с текущим
    // значением пропов — смена фичи во время ожидания не принимает чужое предложение.
    const requested = featureIdRef.current;
    const ac = new AbortController();
    abortRef.current = ac;
    recommendBusyRef.current = true;
    setBusyRecommend(true);
    setErrorText(null);
    workflowTemplatesApi
      .recommendWorkflow(requested, ac.signal)
      .then(resp => {
        const applied = applyRecommend(requested, featureIdRef.current, resp);
        if (!applied) return;
        // Новое предложение — новый выбор: свой выбор шаблона/снятия человека не переживает
        // чужой подбор, ручка пишет предложение, а не маршрут.
        setLocalProposal(applied);
        setSelKey(null);
        setRemovedTouched(null);
      })
      .catch(e => {
        // Отмена нашим же AbortController — не отказ и не ошибка: человек ушёл с экрана.
        if (e instanceof Error && e.name === 'AbortError') return;
        setErrorText(errText(e));
      })
      .finally(() => {
        recommendBusyRef.current = false;
        setBusyRecommend(false);
        abortRef.current = null;
      });
  };

  const stages = selectedEntry ? normalizeStages(selectedEntry.template.stages ?? []) : [];
  const loading = entries === null;
  // Предложенного/подтверждённого шаблона нет в каталоге (например, удалён): сказать прямо,
  // человек выбирает другой — «удалённый шаблон не ломает идущие фичи» (§4).
  const missingTemplate = !loading && !selectedEntry && (!!shownProposal || !!confirmed);
  const updateText = templateUpdateText(confirmed ?? null, selectedEntry?.template ?? null);
  const canConfirm = !!selectedEntry && !busyConfirm;

  const templateOptions = (entries ?? []).map(e => ({
    value: `${e.scope}:${e.template.id}`,
    label: e.scope === 'project' ? `${e.template.name} (проект)` : e.template.name,
  }));

  return (
    <div
      data-testid="feature-workflow-confirm"
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.md,
        padding: SP.md, borderRadius: R.lg,
        background: C.bgSelected, border: `1px solid ${C.borderLight}`,
        fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary,
      }}
    >
      <div style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted }}>Маршрут фичи</div>

      {confirmed && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
          <Badge tone="success" size="xs">маршрут подтверждён · v{confirmed.confirmedVersion}</Badge>
        </div>
      )}

      {updateText && (
        <div role="alert" style={{ fontSize: FS.sm, fontWeight: 600, color: C.warningText }}>{updateText}</div>
      )}

      {shownProposal && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
          <div style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted }}>
            Предложение модели{shownProposal.model ? `: ${shownProposal.model}` : ''}
          </div>
          {shownProposal.rationale && (
            <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>{shownProposal.rationale}</div>
          )}
        </div>
      )}

      <div style={{ display: 'flex', flexDirection: isMobile ? 'column' : 'row', gap: SP.sm, alignItems: isMobile ? 'stretch' : 'flex-end' }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <Select
            value={selectedEntry ? `${selectedEntry.scope}:${selectedEntry.template.id}` : ''}
            onChange={pickTemplate}
            disabled={loading || templateOptions.length === 0}
            placeholder={loading ? 'Загрузка шаблонов…' : 'Выберите шаблон'}
            options={templateOptions}
            title="Шаблон маршрута"
          />
        </div>
      </div>

      {missingTemplate && (
        <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
          Предложенный шаблон не найден в каталоге — выберите другой.
        </div>
      )}

      {selectedEntry && stages.length > 0 && (
        <div style={{ display: 'flex', flexDirection: 'column' }}>
          {stages.map(s => {
            const label = STAGE_ROLE_LABEL[s.role] ?? s.role;
            const lane = laneWord(s.lane ?? null);
            const off = (s.presence ?? '').trim() === 'off';
            const dropped = removedSet.has(s.id ?? '');
            const dimmed = off || dropped;
            return (
              <div
                key={s.id}
                style={{
                  display: 'flex', alignItems: 'center', gap: SP.sm,
                  minHeight: 40, padding: `${SP.xxs}px 0`,
                }}
              >
                <span
                  style={{
                    fontSize: FS.base, fontWeight: 600,
                    color: dimmed ? C.textMuted : C.textHeading,
                    textDecoration: dimmed ? 'line-through' : undefined,
                  }}
                >
                  {label}
                  {s.id !== s.role && (
                    <span style={{ marginLeft: SP.sm, fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 400, color: C.textMuted }}>
                      {s.id}
                    </span>
                  )}
                </span>
                {lane && <Badge tone="neutral" size="xs">{lane}</Badge>}
                {off && <span style={{ fontSize: FS.xs, color: C.textMuted }}>не в маршруте</span>}
                {isRemovableStage(s) && (
                  <span style={{ marginLeft: 'auto' }}>
                    <Checkbox
                      checked={!dropped}
                      onChange={() => toggleRemoved(s.id ?? '')}
                      ariaLabel={`Этап «${label}» включён в маршрут`}
                    />
                  </span>
                )}
              </div>
            );
          })}
        </div>
      )}

      {errorText && (
        <div role="alert" style={{ fontSize: FS.sm, fontWeight: 600, color: C.dangerText }}>{errorText}</div>
      )}

      <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
        <Button
          variant="primary"
          size="sm"
          loading={busyConfirm}
          disabled={!canConfirm}
          fullWidth={isMobile}
          style={isMobile ? { minHeight: 40 } : undefined}
          dataTestId="feature-workflow-confirm-submit"
          onClick={onConfirm}
        >
          Подтвердить маршрут
        </Button>
        <Button
          variant="secondary"
          size="sm"
          loading={busyRecommend}
          fullWidth={isMobile}
          style={isMobile ? { minHeight: 40 } : undefined}
          dataTestId="feature-workflow-confirm-recommend"
          onClick={onRecommend}
        >
          Подобрать маршрут
        </Button>
      </div>
    </div>
  );
}

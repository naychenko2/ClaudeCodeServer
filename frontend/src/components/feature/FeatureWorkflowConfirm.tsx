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
// Этапы рисует WorkflowLadder (F02): колонки, снятие кликом по чипу, имена и линии — там.
// Тон карточки по состоянию (F03): предложение модели — planLight, подтверждённый — bgWhite,
// прочее (нет предложения, «Подбираем») — bgSelected; тревожные строки — токены Notice.
// Состояния: «предлагает» (тег «ждёт вас» только при needsHuman), «правит человек» (тег
// «сменили вы», вместо обоснования пометка), «подтверждено» (✓ маршрут / авто, «Почему так»),
// «Подбираем» (идущий recommend) и «Модель не предложила маршрут» (proposal.state='none').
// Мобила 360: кнопки полной шириной, тач-цели ≥40.
// Встраивание в шапку страницы фичи и мини-граф — вне этого шага (C3b / хвосты).

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { Badge, Button, Select } from '../../components/ui';
import { WorkflowLadder } from './WorkflowLadder';
import { workflowTemplatesApi } from '../../features/workflowTemplates/api';
import { normalizeStages } from '../../features/workflowTemplates/workflowGraphModel';
import {
  applyRecommend, confirmBodyOf, confirmedByText, confirmedLineText, confirmedSummaryText, doubtReasonText,
  initialRemovedIds, pickInitialTemplate, rationaleByText, routeBlockState, templateUpdateText,
  type ConfirmedRoute,
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
  // Тихий режим (шапка страницы фичи, C3b): блок рисуется, только когда от человека что-то
  // нужно — есть предложение без подтверждения или «шаблон изменился». Подтверждённый маршрут
  // без перемен не занимает места. Каталог шаблонов тот же один запрос, что и без флага.
  quiet?: boolean;
  // epic.routeRequired: фича в режиме маршрута. Без предложения и подтверждения блок
  // всё равно виден — состояние «предложения нет» (M5).
  routeRequired?: boolean | null;
}

interface SelectedKey { id: string; scope: WorkflowTemplateScope }

const errText = (e: unknown): string =>
  e instanceof Error && e.message ? e.message : 'Не получилось, попробуйте ещё раз';

export function FeatureWorkflowConfirm({
  featureId, projectId, proposal, confirmed, isMobile, quiet, routeRequired,
}: FeatureWorkflowConfirmProps): React.ReactElement {
  const [entries, setEntries] = useState<WorkflowTemplateEntry[] | null>(null);
  const [selKey, setSelKey] = useState<SelectedKey | null>(null);
  // null — человек ничего не трогал: снятие берётся из предложения модели.
  const [removedTouched, setRemovedTouched] = useState<string[] | null>(null);
  const [localProposal, setLocalProposal] = useState<WorkflowProposal | null>(null);
  const [busyConfirm, setBusyConfirm] = useState(false);
  const [busyRecommend, setBusyRecommend] = useState(false);
  const [errorText, setErrorText] = useState<string | null>(null);
  // Тихий режим: подтверждённый маршрут свёрнут в строку, «Сменить» разворачивает блок.
  const [expanded, setExpanded] = useState(false);
  // «Почему так» у подтверждённого маршрута: раскрытое обоснование под строкой.
  const [whyOpen, setWhyOpen] = useState(false);
  // «Выбрать вручную» в состоянии «Подбираем»: показать Select, не дожидаясь ответа модели.
  const [manual, setManual] = useState(false);

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
    setExpanded(false);
    setWhyOpen(false);
    setManual(false);
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

  // {state:'none'} — «модель не предложила маршрут»: шаблона в нём нет, предложением оно не считается.
  const rawProposal = localProposal ?? proposal ?? null;
  const shownProposal = rawProposal && rawProposal.state !== 'none' ? rawProposal : null;
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

  const loading = entries === null;
  // Предложенного/подтверждённого шаблона нет в каталоге (например, удалён): сказать прямо,
  // человек выбирает другой — «удалённый шаблон не ломает идущие фичи» (§4).
  const missingTemplate = !loading && !selectedEntry && (!!shownProposal || !!confirmed);
  // Строка «шаблон изменился» — только про правку ПОДТВЁЖДЁННОГО шаблона (§4): ищем в
  // каталоге его (по id+scope), а не выбранный в Select. Выбор другого шаблона — смена
  // выбора, не правка маршрута (ложной строки нет); правка подтверждённого шаблона при
  // выбранном другом — истинный сигнал не теряется.
  const confirmedTemplate = useMemo(() => {
    if (!entries || !confirmed) return null;
    return entries.find(e => e.template?.id === confirmed.templateId && e.scope === confirmed.scope)?.template ?? null;
  }, [entries, confirmed]);
  const updateText = templateUpdateText(confirmed ?? null, confirmedTemplate);
  const canConfirm = !!selectedEntry && !busyConfirm;
  // Тихий режим: нужен ответ человека (предложение ждёт подтверждения, подобрали новое) или
  // шаблон подтверждённого маршрута изменился; ошибка и идущий запрос блок не прячут.
  const blockState = routeBlockState(routeRequired, rawProposal, confirmed ?? null);
  const actionable = (!!shownProposal && !confirmed) || !!localProposal || !!updateText
    || blockState === 'none' || !!errorText || busyConfirm || busyRecommend;
  // Пока каталог не загружен, имя шаблона неизвестно: строку не рисуем, иначе она мигнёт id → имя.
  const lineReady = !!confirmed && !loading;
  const lineText = lineReady ? confirmedLineText(confirmed!, confirmedTemplate) : null;

  // Обоснование подтверждённого: из самого маршрута, а если там нет — из предложения эпика
  // (сервер его не стирает). Показывается только по «Почему так».
  const confirmedRationale = (confirmed?.rationale ?? proposal?.rationale ?? '').trim();
  const confirmedModel = proposal?.model ?? null;
  const confirmedProposedAt = confirmed?.proposedAt ?? proposal?.proposedAt ?? null;

  // Строка подтверждённого: тег, сводка, кто подтвердил, «Почему так» (R25–R27). Один вид для
  // тихой строки и раскрытого блока.
  const confirmedRow = (withChange: boolean) => (
    <div data-testid="feature-workflow-confirm-confirmed" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <Badge tone="success" size="xs">✓ маршрут</Badge>
        {confirmed!.autoConfirmed === true && <Badge tone="plan" size="xs">авто</Badge>}
        <span data-testid="feature-workflow-confirm-line" style={{ fontSize: FS.sm, color: C.textSecondary, minWidth: 0 }}>
          {confirmedSummaryText(confirmed!, confirmedTemplate)}
          <span style={{ color: C.textMuted }}>{` · ${confirmedByText(confirmed!)}`}</span>
        </span>
        <span style={{ flex: 1 }} />
        <Button
          variant="ghost" size={isMobile ? 'sm' : 'xs'} dataTestId="feature-workflow-confirm-why"
          ariaExpanded={whyOpen} style={isMobile ? { minHeight: 40 } : undefined}
          onClick={() => setWhyOpen(v => !v)}
        >
          Почему так
        </Button>
        {withChange && (
          <Button variant="ghost" size={isMobile ? 'sm' : 'xs'} style={isMobile ? { minHeight: 40 } : undefined} dataTestId="feature-workflow-confirm-change" onClick={() => setExpanded(true)}>
            Сменить
          </Button>
        )}
      </div>
      {whyOpen && (
        <div data-testid="feature-workflow-confirm-why-text">
          {confirmedRationale
            ? quote(confirmedRationale, rationaleByText(confirmedModel, confirmedProposedAt, confirmed!.autoConfirmed === true ? 'выбрано моделью автоматически' : 'подтвердил человек'))
            : <div style={{ fontSize: FS.sm, color: C.textMuted }}>Обоснования нет: маршрут подтверждён без предложения модели.</div>}
        </div>
      )}
    </div>
  );

  if (quiet && !actionable) {
    if (!confirmed || !lineText) return <></>;
    if (!expanded) {
      return (
        <div
          data-testid="feature-workflow-confirm-compact"
          style={{
            padding: `${SP.sm}px ${SP.md}px`, borderRadius: R.lg, marginBottom: SP.md,
            background: C.bgWhite, border: `1px solid ${C.borderLight}`, fontFamily: FONT.sans,
          }}
        >
          {confirmedRow(true)}
        </div>
      );
    }
  }

  const templateOptions = (entries ?? []).map(e => ({
    value: `${e.scope}:${e.template.id}`,
    label: `${e.template.name} · ${e.scope === 'project' ? 'проект' : 'глобальный'} · v${e.template.version}`,
  }));

  // Состояния блока (R2): предложение модели, пусто/«Подбираем», подтверждено. «Правит человек» —
  // предложение, у которого человек выбрал другой шаблон.
  const showProposal = !!shownProposal && !confirmed;
  const edited = showProposal && !!selKey && !!selectedEntry
    && (selectedEntry.template.id !== shownProposal!.templateId || selectedEntry.scope !== shownProposal!.scope);
  const needsHuman = showProposal && shownProposal!.needsHuman === true;
  const doubt = needsHuman ? doubtReasonText(shownProposal!.doubtReason) : null;
  // «Подбираем» и «Предложения нет» по данным различимы только идущим запросом (proposal.state
  // из B03 даёт 'none'; пока ответа нет — подбор идёт).
  const searching = busyRecommend && !showProposal && !confirmed && !manual;
  const noneState = !showProposal && !confirmed && !searching && blockState === 'none';
  const isConfirmed = !!confirmed;
  const tone = isConfirmed
    ? { bg: C.bgWhite, border: C.borderLight }
    : showProposal ? { bg: C.planLight, border: C.planBorder } : { bg: C.bgSelected, border: C.borderLight };
  const showEditor = !searching && (!isConfirmed || !quiet || expanded);
  const recommendLabel = showProposal ? 'Подобрать заново' : noneState ? 'Подобрать ещё раз' : 'Подобрать маршрут';
  const recommendBtn = (
    <Button
      variant="ghost" size={isMobile ? 'sm' : 'xs'} loading={busyRecommend}
      style={isMobile ? { minHeight: 40 } : undefined}
      dataTestId="feature-workflow-confirm-recommend" onClick={onRecommend}
    >
      {recommendLabel}
    </Button>
  );

  return (
    <div
      data-testid="feature-workflow-confirm"
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.md,
        padding: SP.md, borderRadius: R.lg,
        background: tone.bg, border: `1px solid ${tone.border}`,
        fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary,
        // В шапке страницы отступ вниз несёт сам блок: когда он молчит, зазора нет.
        ...(quiet ? { marginBottom: SP.md } : {}),
      }}
    >
      {isConfirmed && lineReady && confirmedRow(false)}

      {!isConfirmed && (
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
          {needsHuman && (
            <Badge tone="warning" size="xs" title={doubt ?? undefined}>ждёт вас</Badge>
          )}
          <span style={{ fontSize: FS.sm, fontWeight: 600, color: C.textHeading }}>
            {showProposal ? 'Маршрут предлагает модель'
              : searching ? 'Подбираем маршрут…'
                : noneState ? 'Модель не предложила маршрут' : 'Маршрут фичи'}
          </span>
          <span style={{ flex: 1 }} />
          {!searching && recommendBtn}
        </div>
      )}

      {doubt && (
        <div data-testid="feature-workflow-confirm-doubt" style={{ fontSize: FS.sm, color: C.warningText }}>
          Сомневаюсь: {doubt}.
        </div>
      )}

      {searching && (
        <div data-testid="feature-workflow-confirm-searching" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, alignItems: 'flex-start' }}>
          <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
            Модель обычно отвечает до полуминуты: она читает название и описание фичи и выбирает из шаблонов проекта и глобальных.
          </div>
          <Button
            variant="ghost" size={isMobile ? 'sm' : 'xs'} style={isMobile ? { minHeight: 40 } : undefined}
            dataTestId="feature-workflow-confirm-manual" onClick={() => setManual(true)}
          >
            Выбрать вручную
          </Button>
        </div>
      )}

      {noneState && (
        <div data-testid="feature-workflow-confirm-none" style={{ fontSize: FS.sm, color: C.textSecondary }}>
          Подбор не ответил или ответ не прошёл проверку. Выберите шаблон сами, этапы появятся ниже.
        </div>
      )}

      {updateText && (
        <div role="alert" style={{ fontSize: FS.sm, fontWeight: 600, color: C.warningText }}>{updateText}</div>
      )}

      {showEditor && (
        <>
          <div style={{ display: 'flex', flexDirection: isMobile ? 'column' : 'row', gap: SP.sm, alignItems: isMobile ? 'stretch' : 'center', flexWrap: 'wrap' }}>
            <span style={{ fontSize: FS.xs, color: C.textMuted }}>Шаблон</span>
            <div style={{ flex: isMobile ? undefined : '0 1 auto', minWidth: 0 }}>
              <Select
                value={selectedEntry ? `${selectedEntry.scope}:${selectedEntry.template.id}` : ''}
                onChange={pickTemplate}
                disabled={loading || templateOptions.length === 0}
                placeholder={loading ? 'Загрузка шаблонов…' : 'Выберите шаблон…'}
                options={templateOptions}
                title="Шаблон маршрута"
              />
            </div>
            {edited && <Badge tone="neutral" size="xs">сменили вы</Badge>}
          </div>

          {showProposal && (edited
            ? (
              <div data-testid="feature-workflow-confirm-edited">
                {quote(
                  'Модель предлагала другой шаблон. Вы выбрали этот — обоснование к нему не относится, решение за вами.',
                  rationaleByText(shownProposal!.model, shownProposal!.proposedAt, ''),
                )}
              </div>
            )
            : (shownProposal!.rationale ?? '').trim() && (
              <div data-testid="feature-workflow-confirm-rationale">
                {quote(shownProposal!.rationale!.trim(), rationaleByText(shownProposal!.model, shownProposal!.proposedAt, 'обоснование модели'))}
              </div>
            ))}

          {missingTemplate && (
            <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
              Предложенный шаблон не найден в каталоге — выберите другой.
            </div>
          )}

          <WorkflowLadder
            stages={selectedEntry ? normalizeStages(selectedEntry.template.stages ?? []) : null}
            removedIds={removedIds}
            onToggle={toggleRemoved}
            isMobile={isMobile}
          />
        </>
      )}

      {errorText && (
        <div role="alert" style={{ fontSize: FS.sm, fontWeight: 600, color: C.dangerText }}>{errorText}</div>
      )}

      {showEditor && (
        <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap', alignItems: 'center' }}>
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
          {!isConfirmed && (
            <span style={{ fontSize: FS.sm, color: C.textMuted }}>
              {selectedEntry ? 'Пока не подтвердите, исполнители фичи не стартуют.' : 'Сначала шаблон.'}
            </span>
          )}
          {isConfirmed && recommendBtn}
        </div>
      )}
    </div>
  );
}

// Обоснование модели цитатой с акцентной полосой и подписью «Flash · дата · …» (R6).
function quote(text: string, by: string): React.ReactElement {
  return (
    <div style={{ borderLeft: `3px solid ${C.accent}`, paddingLeft: SP.md, display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
      <div style={{ fontSize: FS.sm, color: C.textSecondary, lineHeight: 1.5 }}>{text}</div>
      {by && <div style={{ fontSize: FS.xs, color: C.textMuted }}>{by}</div>}
    </div>
  );
}

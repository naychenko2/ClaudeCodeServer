// Блок подтверждения маршрута фичи (feature-workflows, C2; разрез docs/research/
// ai-fabrika-workflow-cut-2026-11.md §4–§5): показывает предложение модели (шаблон и
// обоснование), даёт сменить шаблон и снять необязательные этапы, «Подтвердить маршрут»
// → confirmWorkflow, «Подобрать маршрут» → recommendWorkflow (обе ручки из
// features/workflowTemplates/api.ts). Подтверждает только человек (И7): свой ответ confirm —
// записанный сервером маршрут с новой версией — блок применяет себе сразу (Ф-2.15), своего
// refetch эпика здесь нет. Дальше настоящее состояние эпика приходит штатным task_changed и
// заменяет собой этот ответ (сброс applied).
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
// «Шаблон изменился» (F04): янтарная карточка — лестница v2 (readonly, новые этапы с зелёной рамкой),
// «Обновить маршрут до vN» (confirm с прежним skipped из confirmed) и «Оставить vN» (keep-version).
// Мобила 360: кнопки полной шириной, тач-цели ≥40.
// Встраивание в шапку страницы фичи и мини-граф — вне этого шага (C3b / хвосты).

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { C, FONT, FS, R, SP, TB } from '../../lib/design';
import { Badge, Button, Select } from '../../components/ui';
import { WorkflowLadder } from './WorkflowLadder';
import { WorkflowRouteSheet } from './WorkflowRouteSheet';
import { STAGE_ROLE_LABEL } from '../../features/workflowTemplates/WorkflowStageRow';
import { workflowTemplatesApi } from '../../features/workflowTemplates/api';
import { normalizeStages } from '../../features/workflowTemplates/workflowGraphModel';
import {
  applyConfirmed, applyRecommend, confirmBodyOf, confirmedLineText, doubtReasonText,
  initialRemovedIds, newStageIds, pickInitialTemplate, rationaleByText, routeBlockState, shownConfirmed,
  templateUpdateText, updateExplainText,
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
  // Мобильный вид (F05): шапка страницы запросила открыть шторку («Подтвердить» из области
  // этапа). Работает при isMobile && quiet; иначе игнорируется.
  sheetOpenRequest?: boolean;
  // Шторку закрыли — страница снимает свой флаг запроса (R-F05). Пока флаг не снят, он
  // остаётся true, и повторное «Подтвердить» не открывает шторку снова (мёртвая кнопка).
  onSheetClosed?: () => void;
}

interface SelectedKey { id: string; scope: WorkflowTemplateScope }

const errText = (e: unknown): string =>
  e instanceof Error && e.message ? e.message : 'Не получилось, попробуйте ещё раз';

// Ф-2.42 (замечание человека): фиолетовая карточка предложения не объясняла, что означает
// цвет и почему этапы показаны до подтверждения. Одна строка — только в состоянии
// предложения (тон plan); в подтверждённом и «предложения нет» её нет. Про запуск
// исполнителей здесь не говорится — это строка шапки и подпись под кнопкой.
const PLAN_HINT_TEXT = 'Фиолетовый цвет означает: это предложение модели. Пока маршрут не подтверждён, предложенные этапы не выполняются.';

export function FeatureWorkflowConfirm({
  featureId, projectId, proposal, confirmed: confirmedState, isMobile, quiet, routeRequired, sheetOpenRequest, onSheetClosed,
}: FeatureWorkflowConfirmProps): React.ReactElement {
  const [entries, setEntries] = useState<WorkflowTemplateEntry[] | null>(null);
  const [selKey, setSelKey] = useState<SelectedKey | null>(null);
  // null — человек ничего не трогал: снятие берётся из предложения модели.
  const [removedTouched, setRemovedTouched] = useState<string[] | null>(null);
  const [localProposal, setLocalProposal] = useState<WorkflowProposal | null>(null);
  // Ответ собственного подтверждения (Ф-2.15): записанный сервером маршрут — им блок и
  // показывает, пока настоящее состояние эпика не пришло штатным task_changed.
  const [applied, setApplied] = useState<ConfirmedRoute | null>(null);
  const [busyConfirm, setBusyConfirm] = useState(false);
  const [busyRecommend, setBusyRecommend] = useState(false);
  const [busyKeep, setBusyKeep] = useState(false);
  const [errorText, setErrorText] = useState<string | null>(null);
  // Тихий режим: подтверждённый маршрут свёрнут в строку, «Сменить» разворачивает блок.
  const [expanded, setExpanded] = useState(false);
  // «Почему так» у подтверждённого маршрута: раскрытое обоснование под строкой.
  const [whyOpen, setWhyOpen] = useState(false);
  // «Выбрать вручную» в состоянии «Подбираем»: показать Select, не дожидаясь ответа модели.
  const [manual, setManual] = useState(false);
  // Мобила (F05): шторка «Маршрут фичи» и раскрытое по ⓘ обоснование в ней.
  const [sheetOpen, setSheetOpen] = useState(false);
  const [infoOpen, setInfoOpen] = useState(false);

  const featureIdRef = useRef(featureId);
  // ref-guard, а не state: state обновился бы только со следующим рендером, а двойной
  // клик приходит двумя обработчиками за один (тот же приём, что busyRef в редакторе).
  const confirmBusyRef = useRef(false);
  const recommendBusyRef = useRef(false);
  const keepBusyRef = useRef(false);
  const abortRef = useRef<AbortController | null>(null);
  // Признак «пришло настоящее состояние эпика»: ответ своего подтверждения держится только
  // до него (ссылка props меняется на каждом refetch — тот же приём, что featureIdRef).
  const confirmedRef = useRef(confirmedState);

  // Смена фичи — синхронный сброс до рендера: выбор, снятие и локальное предложение
  // принадлежат одной фиче; идущий recommend прерывается (его ответ всё равно не применился бы).
  if (featureIdRef.current !== featureId) {
    featureIdRef.current = featureId;
    setSelKey(null);
    setRemovedTouched(null);
    setLocalProposal(null);
    setApplied(null);
    setErrorText(null);
    setExpanded(false);
    setWhyOpen(false);
    setManual(false);
    setSheetOpen(false);
    setInfoOpen(false);
    confirmedRef.current = confirmedState;
    abortRef.current?.abort();
  }
  // Пришёл новый confirmed эпика (refetch по task_changed) — свой ответ больше не нужен:
  // истина уже в состоянии, держаться поверх него он не должен.
  if (confirmedRef.current !== confirmedState) {
    confirmedRef.current = confirmedState;
    setApplied(null);
  }
  // Подтверждённый маршрут, с которым работает весь блок: состояние эпика или ответ своего
  // подтверждения этой же фичи (Ф-2.15: версия читается сразу, а не через полторы секунды
  // дебаунса и сети).
  const confirmed = shownConfirmed(confirmedState ?? null, applied);
  // Шторка открывается по ЗАПРОСУ страницы (уровень флага). Флаг снимает сама страница
  // в onSheetClosed (R-F05): пока он не снят, он остаётся true, и повторное «Подтвердить»
  // не открывает шторку снова (мёртвая кнопка).
  useEffect(() => { if (sheetOpenRequest) setSheetOpen(true); }, [sheetOpenRequest]);

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

  // Применить ответ подтверждения: он становится видом блока, только если относится к
  // показанной фиче (поздний ответ другой фичи — нет, тот же приём, что applyRecommend).
  const applyConfirm = (answer: ConfirmedRoute | null, requested: string) => {
    const next = applyConfirmed(requested, featureIdRef.current, answer);
    if (next) setApplied(next);
  };

  const onConfirm = () => {
    const entry = selectedEntry;
    if (!entry || confirmBusyRef.current) return;
    confirmBusyRef.current = true;
    setBusyConfirm(true);
    setErrorText(null);
    const requested = featureId;
    workflowTemplatesApi
      // Успешный ответ — записанный сервером маршрут: его и показываем. Отказ — только
      // показать, состояние остаётся прежним.
      .confirmWorkflow(featureId, confirmBodyOf(entry.template, entry.scope, removedIds))
      .then(r => applyConfirm(r, requested))
      .catch(e => setErrorText(errText(e)))
      .finally(() => { confirmBusyRef.current = false; setBusyConfirm(false); });
  };

  // «Обновить маршрут до vN»: то же подтверждение, но с прежним снятием из confirmed.skipped
  // (confirmBodyOf отсеивает то, что в новой версии уже не съёмно), а не из предложения модели.
  // Ответ сервера несёт записанную версию — она и становится видимой сразу (Ф-2.15), карточка
  // «шаблон изменился» при новой подтверждённой версии больше не предлагается.
  const onUpdateRoute = () => {
    if (!confirmed || !confirmedTemplate || confirmBusyRef.current) return;
    confirmBusyRef.current = true;
    setBusyConfirm(true);
    setErrorText(null);
    const requested = featureId;
    workflowTemplatesApi
      .confirmWorkflow(featureId, confirmBodyOf(confirmedTemplate, confirmed.scope, confirmed.skipped ?? []))
      .then(r => applyConfirm(r, requested))
      .catch(e => setErrorText(errText(e)))
      .finally(() => { confirmBusyRef.current = false; setBusyConfirm(false); });
  };

  // «Оставить vN»: маршрут не меняется, предупреждение закрывается до следующей версии шаблона.
  const onKeepVersion = () => {
    if (!confirmedTemplate || keepBusyRef.current) return;
    keepBusyRef.current = true;
    setBusyKeep(true);
    setErrorText(null);
    workflowTemplatesApi
      .keepVersion(featureId, confirmedTemplate.version)
      .catch(e => setErrorText(errText(e)))
      .finally(() => { keepBusyRef.current = false; setBusyKeep(false); });
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
    || blockState === 'none' || !!errorText || busyConfirm || busyRecommend || busyKeep;
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
          {confirmedLineText(confirmed!, confirmedTemplate)}
        </span>
        <span style={{ flex: 1 }} />
        <Button
          variant="ghost" size={isMobile ? 'sm' : 'xs'} dataTestId="feature-workflow-confirm-why"
          ariaExpanded={whyOpen} style={isMobile ? { minHeight: TB.iconHitMobile } : undefined}
          onClick={() => setWhyOpen(v => !v)}
        >
          Почему так
        </Button>
        {withChange && (
          <Button variant="ghost" size={isMobile ? 'sm' : 'xs'} style={isMobile ? { minHeight: TB.iconHitMobile } : undefined} dataTestId="feature-workflow-confirm-change" onClick={() => setExpanded(true)}>
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

  // Мобильный вид шапки (F05): компактная строка вместо блока, всё остальное — в шторке.
  const sheetMode = isMobile && !!quiet;
  if (quiet && !actionable && !(sheetMode && sheetOpen)) {
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
  // Карточка «шаблон изменился» (R28–R32): ждёт решения человека, обычный редактор под ней молчит.
  const updateCardOn = !!updateText && !!confirmed && !!confirmedTemplate;
  const showEditor = !searching && !updateCardOn && (!isConfirmed || !quiet || expanded);
  const touch = isMobile ? { minHeight: TB.iconHitMobile } : undefined;
  const updateCard = updateCardOn ? (
    <div
      data-testid="feature-workflow-confirm-update"
      role="alert"
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.sm, padding: SP.md, borderRadius: R.lg,
        background: C.warningBg, border: `1px solid ${C.warning}`,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <Badge tone="warning" size="xs">шаблон изменился</Badge>
        <span data-testid="feature-workflow-confirm-update-title" style={{ fontSize: FS.sm, fontWeight: 600, color: C.warningText }}>
          {`«${confirmedTemplate!.name || confirmed!.templateId}»: v${confirmed!.confirmedVersion} → v${confirmedTemplate!.version}`}
        </span>
      </div>
      <WorkflowLadder
        stages={normalizeStages(confirmedTemplate!.stages ?? [])}
        removedIds={confirmed!.skipped ?? []}
        mode="readonly"
        newIds={newStageIds(confirmed!, confirmedTemplate!)}
        isMobile={isMobile}
      />
      <div data-testid="feature-workflow-confirm-update-text" style={{ fontSize: FS.sm, color: C.textSecondary }}>
        {updateExplainText(confirmed!, confirmedTemplate!, role => STAGE_ROLE_LABEL[role] ?? role)}
      </div>
      <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
        <Button
          variant="primary" size="sm" loading={busyConfirm} disabled={busyConfirm || busyKeep}
          fullWidth={isMobile} style={touch} dataTestId="feature-workflow-confirm-update-apply" onClick={onUpdateRoute}
        >
          {`Обновить маршрут до v${confirmedTemplate!.version}`}
        </Button>
        <Button
          variant="ghost" size="sm" loading={busyKeep} disabled={busyConfirm || busyKeep}
          fullWidth={isMobile} style={touch} dataTestId="feature-workflow-confirm-update-keep" onClick={onKeepVersion}
        >
          {`Оставить v${confirmed!.confirmedVersion}`}
        </Button>
      </div>
    </div>
  ) : null;
  const recommendLabel = showProposal ? 'Подобрать заново' : noneState ? 'Подобрать ещё раз' : 'Подобрать маршрут';
  const recommendBtn = (
    <Button
      variant="ghost" size={isMobile ? 'sm' : 'xs'} loading={busyRecommend}
      style={isMobile ? { minHeight: TB.iconHitMobile } : undefined}
      dataTestId="feature-workflow-confirm-recommend" onClick={onRecommend}
    >
      {recommendLabel}
    </Button>
  );

  if (sheetMode) {
    // Шаблон и снятые этапы строки: предложение/выбор — из редактора, «изменился»/подтверждено — из маршрута.
    const rowTemplate = updateCardOn || isConfirmed ? confirmedTemplate : selectedEntry?.template ?? null;
    const rowRemoved = updateCardOn || isConfirmed ? confirmed?.skipped ?? [] : removedIds;
    const rowName = rowTemplate?.name
      || (updateCardOn || isConfirmed ? confirmed?.templateId : shownProposal?.templateId) || '';
    let rowCount = '';
    if (rowTemplate) {
      const all = normalizeStages(rowTemplate.stages ?? []);
      const off = new Set(rowRemoved);
      rowCount = ` · ${all.filter(x => !off.has(x.id ?? '')).length} из ${all.length} этапов`;
    }
    const rowTag = updateCardOn ? 'шаблон изменился' : noneState || searching ? 'нет маршрута' : 'ждёт вас';
    const rowText = searching ? 'Подбираем маршрут…'
      : noneState ? 'Модель не предложила маршрут'
        : `${rowName}${rowCount}`;
    // Закрытие шторки снимает и запрос страницы (onSheetClosed): иначе флаг остаётся true,
    // и повторное «Подтвердить» не открывает шторку снова (R-F05).
    const closeSheet = () => { setSheetOpen(false); setInfoOpen(false); onSheetClosed?.(); };
    // Подвал по состоянию (R36–R38): подтвердить + подобрать заново / обновить + оставить /
    // закрыть; в «Подбираем» и «нет предложения» — «Подобрать заново» (на десктопе подпись другая).
    const fb = { fullWidth: true, style: { minHeight: TB.iconHitMobile } } as const;
    const redo = (
      <Button variant="ghost" size="sm" {...fb} loading={busyRecommend} disabled={loading}
        dataTestId="feature-workflow-confirm-recommend" onClick={onRecommend}>
        Подобрать заново
      </Button>
    );
    const sheetFooter = isConfirmed && !updateCardOn ? (
      <Button variant="primary" size="sm" {...fb} dataTestId="feature-workflow-confirm-close" onClick={closeSheet}>
        Закрыть
      </Button>
    ) : updateCardOn ? (
      <>
        <Button variant="primary" size="sm" {...fb} loading={busyConfirm} disabled={busyConfirm || busyKeep}
          dataTestId="feature-workflow-confirm-update-apply" onClick={onUpdateRoute}>
          {`Обновить маршрут до v${confirmedTemplate!.version}`}
        </Button>
        <Button variant="ghost" size="sm" {...fb} loading={busyKeep} disabled={busyConfirm || busyKeep}
          dataTestId="feature-workflow-confirm-update-keep" onClick={onKeepVersion}>
          {`Оставить v${confirmed!.confirmedVersion}`}
        </Button>
      </>
    ) : searching ? redo : (
      <>
        <Button variant="primary" size="sm" {...fb} loading={busyConfirm} disabled={!canConfirm}
          dataTestId="feature-workflow-confirm-submit" onClick={onConfirm}>
          Подтвердить маршрут
        </Button>
        {redo}
      </>
    );
    const rationaleText = (confirmed ? confirmedRationale : (shownProposal?.rationale ?? '').trim()) || '';
    return (
      <>
        {(!isConfirmed || updateCardOn) && (
          <div
            data-testid="feature-workflow-confirm-mobile-row"
            style={{
              display: 'flex', alignItems: 'center', gap: SP.sm, padding: `${SP.sm}px ${SP.md}px`,
              borderRadius: R.lg, marginBottom: SP.md, fontFamily: FONT.sans, fontSize: FS.sm,
              color: C.textPrimary, background: updateCardOn ? C.warningBg : C.planLight,
              border: `1px solid ${updateCardOn ? C.warning : C.planBorder}`,
            }}
          >
            <Badge tone="warning" size="xs">{rowTag}</Badge>
            <span
              data-testid="feature-workflow-confirm-mobile-row-text"
              style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
            >
              {rowText}
            </span>
            <Button variant="primary" size="sm" style={{ minHeight: TB.iconHitMobile, flexShrink: 0 }}
              dataTestId="feature-workflow-confirm-mobile-open" onClick={() => setSheetOpen(true)}>
              Посмотреть
            </Button>
          </div>
        )}
        {sheetOpen && (
          <WorkflowRouteSheet onClose={closeSheet} footer={sheetFooter}>
            {isConfirmed && !updateCardOn && lineReady && confirmedRow(false)}
            {doubt && (
              <div data-testid="feature-workflow-confirm-doubt" style={{ fontSize: FS.sm, color: C.warningText }}>
                Сомневаюсь: {doubt}.
              </div>
            )}
            {searching && (
              <div data-testid="feature-workflow-confirm-searching" style={{ fontSize: FS.sm, color: C.textSecondary }}>
                Модель обычно отвечает до полуминуты: она читает название и описание фичи и выбирает из шаблонов проекта и глобальных.
              </div>
            )}
            {noneState && (
              <div data-testid="feature-workflow-confirm-none" style={{ fontSize: FS.sm, color: C.textSecondary }}>
                Подбор не ответил или ответ не прошёл проверку. Выберите шаблон сами, этапы появятся ниже.
              </div>
            )}
            {showProposal && (
              <div data-testid="feature-workflow-confirm-plan-hint" style={{ fontSize: FS.sm, color: C.textSecondary }}>
                {PLAN_HINT_TEXT}
              </div>
            )}
            {updateCardOn && (
              <div data-testid="feature-workflow-confirm-update-text" style={{ fontSize: FS.sm, color: C.textSecondary }}>
                <b>{`«${confirmedTemplate!.name || confirmed!.templateId}»: v${confirmed!.confirmedVersion} → v${confirmedTemplate!.version}. `}</b>
                {updateExplainText(confirmed!, confirmedTemplate!, role => STAGE_ROLE_LABEL[role] ?? role)}
              </div>
            )}
            {!searching && !isConfirmed && !updateCardOn && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
                <span style={{ fontSize: FS.xs, color: C.textMuted }}>Шаблон</span>
                <Select
                  value={selectedEntry ? `${selectedEntry.scope}:${selectedEntry.template.id}` : ''}
                  onChange={pickTemplate}
                  disabled={loading || templateOptions.length === 0}
                  placeholder={loading ? 'Загрузка шаблонов…' : 'Выберите шаблон…'}
                  options={templateOptions}
                  title="Шаблон маршрута"
                />
                {edited && <Badge tone="neutral" size="xs">сменили вы</Badge>}
              </div>
            )}
            {missingTemplate && !isConfirmed && (
              <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
                Предложенный шаблон не найден в каталоге — выберите другой.
              </div>
            )}
            {!searching && (
              <WorkflowLadder
                stages={updateCardOn || isConfirmed
                  ? confirmedTemplate ? normalizeStages(confirmedTemplate.stages ?? []) : null
                  : selectedEntry ? normalizeStages(selectedEntry.template.stages ?? []) : null}
                removedIds={updateCardOn || isConfirmed ? confirmed?.skipped ?? [] : removedIds}
                mode={updateCardOn || isConfirmed ? 'readonly' : 'interactive'}
                newIds={updateCardOn ? newStageIds(confirmed!, confirmedTemplate!) : undefined}
                onToggle={updateCardOn || isConfirmed ? undefined : toggleRemoved}
                isMobile
              />
            )}
            {rationaleText && !updateCardOn && !isConfirmed && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
                <Button variant="ghost" size="sm" style={{ minHeight: TB.iconHitMobile, alignSelf: 'flex-start' }}
                  ariaExpanded={infoOpen} dataTestId="feature-workflow-confirm-info"
                  onClick={() => setInfoOpen(v => !v)}>
                  ⓘ Почему так
                </Button>
                {infoOpen && (
                  <div data-testid="feature-workflow-confirm-rationale">
                    {edited
                      ? quote('Модель предлагала другой шаблон. Вы выбрали этот — обоснование к нему не относится, решение за вами.',
                        rationaleByText(shownProposal!.model, shownProposal!.proposedAt, ''))
                      : quote(rationaleText, rationaleByText(shownProposal?.model, shownProposal?.proposedAt, 'обоснование модели'))}
                  </div>
                )}
              </div>
            )}
            {errorText && (
              <div role="alert" style={{ fontSize: FS.sm, fontWeight: 600, color: C.dangerText }}>{errorText}</div>
            )}
          </WorkflowRouteSheet>
        )}
      </>
    );
  }

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

      {showProposal && (
        <div data-testid="feature-workflow-confirm-plan-hint" style={{ fontSize: FS.sm, color: C.textSecondary }}>
          {PLAN_HINT_TEXT}
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
            variant="ghost" size={isMobile ? 'sm' : 'xs'} style={isMobile ? { minHeight: TB.iconHitMobile } : undefined}
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

      {updateCard}

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
            style={isMobile ? { minHeight: TB.iconHitMobile } : undefined}
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

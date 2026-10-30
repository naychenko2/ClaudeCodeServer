// Вкладка «Шаги» страницы фичи (Ш7; макет feature-page-simplify-v1-proposal, П6/П12).
//
// Строки — из run.stepChains (+ шаги плана без задачи, как в «Производстве»): шаг = одна
// строка, доработка корзину не плодит. Корзину шага отдаёт бэк (FeatureStepAccounting) через
// карточку ленты (buildProdCards): статус по полям задачи фронт НЕ выводит.
// Фильтры = корзины stepProgress: «в работе · в ревью · не заведено · принято», нулевые
// скрыты; сумма строк по ним = stepProgress.total. «В master» — ортогональный признак
// (в сумму не входит), «снято» — вне знаменателя. Служебные задачи (эпик, разрез, план,
// ревью, проверка, финал, мерж и всё, что не вошло в цепочки) — под фильтром «служебные».
// «Нужен ответ» — слово из общего источника (BRIEF_WAIT_WORD): «ждёт вас» говорит одна тревога.
//   • строка = аватар · название · роль · корзина · пара «чат · задача»;
//   • «Показать все» при длинном списке: порог 8 (макет LIM);
//   • пустое: «Шагов нет» (фича создана, конвейер не запускался).

import { useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { roleLabel } from '../../lib/roleLabel';
import { openChatById } from '../../lib/openChat';
import { openTaskInSection } from '../../lib/tasks';
import { formatDuration } from '../../lib/chatGroups';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { EMPTY_NAV_MARKS, buildNavMarks, type NavMarks } from '../../lib/featureNavMarks';
import { ExecutorAvatar } from './FeatureTimelineBits';
import { NavTargetPair } from './NavTargetButton';
import { buildProdCards, type ProdCard } from './FeatureProdCards';
import { BRIEF_WAIT_WORD } from './FeatureBriefModel';
import { Button } from '../ui/Button';
import type { FeatureRun, FeatureStepChain, FeatureTask } from '../../types/featureRun';

// Порог «Показать все» (макет: LIM=8 у событий; тот же порог и у шагов).
export const TASKS_COLLAPSED_LIMIT = 8;

// === Чистые функции (тестируются в node, без DOM) ===

// Все задачи фичи в плоском списке: эпик + шаги + ревью + проверки + финал +
// мерж + разрезы + планы. Контракт: UI обязан пережить отсутствие любого списка
// (старые данные) — пустые/отсутствующие раскладываем как [].
export function allFeatureTasks(run: FeatureRun): FeatureTask[] {
  return [
    ...(run.epic ? [run.epic] : []),
    ...(run.steps ?? []),
    ...(run.reviews ?? []),
    ...(run.qa ?? []),
    ...(run.final ? [run.final] : []),
    ...(run.merge ? [run.merge] : []),
    ...(run.cut ?? []),
    ...(run.plan ?? []),
  ];
}

// Корзина шага (взаимоисключающие): четыре входят в stepProgress.total, withdrawn — вне него.
export type StepBucket = 'work' | 'review' | 'accepted' | 'notCreated' | 'withdrawn';

// Корзина из карточки ленты: состояние карточки уже построено по корзине бэка (bucketOf в
// FeatureProdCards), здесь только раскладка по словарю stepProgress.
export function cardBucket(card: ProdCard): StepBucket {
  if (card.planned) return 'notCreated';
  if (card.state === 'done') return 'accepted';
  if (card.state === 'withdrawn') return 'withdrawn';
  return card.awaitingReview ? 'review' : 'work';
}

export const BUCKET_LABEL: Record<StepBucket, string> = {
  work: 'в работе',
  review: 'в ревью',
  accepted: 'принято',
  notCreated: 'не заведено',
  withdrawn: 'снято',
};

export interface StepRow {
  card: ProdCard;
  bucket: StepBucket;
  // «В master» — ортогональный признак цепочки; в корзины не входит.
  inMaster: boolean;
  chain: FeatureStepChain | null;
  // Первая задача цепочки (шапка шага): цель кнопки «задача»; у шага плана без задачи — null.
  headTask: FeatureTask | null;
  // Исполнитель шага ждёт ответа человека (awaitingHumanSince у идущей задачи цепочки).
  needsAnswer: boolean;
}

export interface StepsTabModel {
  steps: StepRow[];
  // Задачи вне цепочек шагов: эпик, разрез, план, ревью, проверки, финал, мерж, сироты.
  service: FeatureTask[];
}

export function buildStepsTab(run: FeatureRun): StepsTabModel {
  const chains = run.stepChains ?? [];
  const chainByRef = new Map<string, FeatureStepChain>();
  for (const ch of chains) chainByRef.set(ch.stepRef, ch);
  const all = allFeatureTasks(run);
  const taskById = new Map<string, FeatureTask>();
  for (const t of all) taskById.set(t.id, t);

  const steps: StepRow[] = buildProdCards(run).map(card => {
    const chain = chainByRef.get(card.stepRef) ?? null;
    const own = chain ? chain.taskIds.map(id => taskById.get(id)).filter((t): t is FeatureTask => !!t) : [];
    return {
      card,
      bucket: cardBucket(card),
      inMaster: chain?.inMaster === true,
      chain,
      headTask: own[0] ?? null,
      needsAnswer: own.some(t => t.status === 'inProgress' && !!t.awaitingHumanSince),
    };
  });

  const inChains = new Set(chains.flatMap(c => c.taskIds));
  return { steps, service: sortTasksByTime(all.filter(t => !inChains.has(t.id))) };
}

// Ключ сортировки служебных: последняя активность (completedAt → claudeStartedAt → createdAt).
function taskSortKey(t: FeatureTask): string {
  return t.completedAt ?? t.claudeStartedAt ?? t.createdAt ?? '';
}

// Сортировка по времени: последняя активность — сначала (desc), стабильная.
export function sortTasksByTime(tasks: FeatureTask[]): FeatureTask[] {
  return [...tasks].sort((a, b) => {
    const ka = taskSortKey(a);
    const kb = taskSortKey(b);
    if (ka === kb) return 0;
    return ka < kb ? 1 : -1;
  });
}

export type StepsFilter = 'all' | StepBucket | 'inMaster' | 'service';

export interface StepsFilterChip {
  key: StepsFilter;
  label: string;
  count: number;
}

// Порядок как в полосе трекера: в работе · в ревью · не заведено · принято; затем «в master»,
// «снято», «служебные». «Все» — всегда, остальные только с числом > 0.
const CHIP_ORDER: StepBucket[] = ['work', 'review', 'notCreated', 'accepted'];

export function stepsFilterChips(model: StepsTabModel): StepsFilterChip[] {
  const count = (b: StepBucket) => model.steps.filter(r => r.bucket === b).length;
  const chips: StepsFilterChip[] = [{ key: 'all', label: 'все', count: model.steps.length }];
  for (const b of CHIP_ORDER) if (count(b) > 0) chips.push({ key: b, label: BUCKET_LABEL[b], count: count(b) });
  const master = model.steps.filter(r => r.inMaster).length;
  if (master > 0) chips.push({ key: 'inMaster', label: 'в master', count: master });
  if (count('withdrawn') > 0) chips.push({ key: 'withdrawn', label: BUCKET_LABEL.withdrawn, count: count('withdrawn') });
  if (model.service.length > 0) chips.push({ key: 'service', label: 'служебные', count: model.service.length });
  return chips;
}

export function filterStepRows(steps: StepRow[], filter: Exclude<StepsFilter, 'service'>): StepRow[] {
  if (filter === 'all') return steps;
  if (filter === 'inMaster') return steps.filter(r => r.inMaster);
  return steps.filter(r => r.bucket === filter);
}

// Подпись состояния служебной задачи (у неё цепочки нет, корзины нет): факт по статусу задачи.
export function serviceStatusLabel(t: FeatureTask): string {
  if (t.status === 'done') return 'готово';
  if (t.status === 'inProgress') return 'в работе';
  return 'впереди';
}

// === Компоненты ===

// Тег состояния (макет .tst): 11/600, padding 0 6, R.sm, line-height 16.
type TagTone = 'ok' | 'info' | 'work' | 'warn' | 'danger' | 'muted';
function tagStyle(tone: TagTone): CSSProperties {
  switch (tone) {
    case 'ok': return { background: C.successBg, color: C.successText };
    case 'info': return { background: C.infoBg, color: C.textPrimary };
    case 'work': return { background: C.bgSelected, color: C.textHeading };
    case 'warn': return { background: C.warningBg, color: C.warningText };
    case 'danger': return { background: C.dangerBg, color: C.dangerText };
    default: return { background: 'transparent', color: C.textMuted, boxShadow: `inset 0 0 0 1px ${C.border}` };
  }
}
const BUCKET_TONE: Record<StepBucket, TagTone> = {
  accepted: 'ok', review: 'info', work: 'work', notCreated: 'muted', withdrawn: 'muted',
};

function StatusTag({ tone, text, testId, isMobile }: { tone: TagTone; text: string; testId: string; isMobile: boolean }) {
  return (
    <span
      data-testid={testId}
      style={{
        fontSize: FS.xs, fontWeight: 600, padding: '0 6px', borderRadius: R.sm,
        lineHeight: '16px', whiteSpace: 'nowrap', flexShrink: 0,
        minHeight: isMobile ? 40 : undefined, display: 'inline-flex', alignItems: 'center',
        ...tagStyle(tone),
      }}
    >
      {text}
    </span>
  );
}

// Общая строка вкладки: аватар · название · роль · теги · пара «чат · задача».
// Без задачи (шаг плана, ещё не заведённый) пары нет, название — не кнопка.
interface RowProps {
  testId: string;
  title: string;
  role: string;
  personaId: string | null;
  tags: { tone: TagTone; text: string; testId: string }[];
  meta?: string | null;
  task: FeatureTask | null;
  chatId: string | null;
  featureId: string;
  featureTitle: string | null;
  marks?: NavMarks;
  isMobile: boolean;
}

function StepsRow({ testId, title, role, personaId, tags, meta = null, task, chatId, featureId, featureTitle, marks = EMPTY_NAV_MARKS, isMobile }: RowProps) {
  const [hover, setHover] = useState(false);
  const taskClosed = task?.status === 'done';

  // Переход в задачу: openTaskInSection (id + projectId) + точка возврата (Ф9b).
  const openTask = () => {
    if (!task) return;
    recordFeatureReturn(featureId, { kind: 'task', id: task.id }, featureTitle);
    openTaskInSection({ id: task.id, projectId: task.projectId ?? undefined });
  };
  // Переход в чат исполнителя: openChatById + точка возврата.
  const openChat = () => {
    if (!chatId) return;
    recordFeatureReturn(featureId, { kind: 'chat', id: chatId }, featureTitle);
    void openChatById(chatId);
  };

  const titleNode = (
    <>
      <span
        data-testid="task-title"
        style={{
          fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          minWidth: 0, flex: '1 1 auto',
        }}
      >
        {title}
      </span>
      {role && (
        <span
          data-testid="task-role"
          style={{
            fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
            background: C.bgSelected, borderRadius: R.sm, padding: '0 5px',
            whiteSpace: 'nowrap', flexShrink: 0,
          }}
        >
          {role}
        </span>
      )}
      {tags.map(t => <StatusTag key={t.testId} tone={t.tone} text={t.text} testId={t.testId} isMobile={isMobile} />)}
      {meta && (
        <span data-testid="task-meta" style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, whiteSpace: 'nowrap', flexShrink: 0 }}>
          {meta}
        </span>
      )}
    </>
  );

  return (
    <div
      data-testid={testId}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
        margin: `0 -${SP.sm}px`, padding: `0 ${SP.sm}px`,
        background: hover ? C.bgSelected : 'transparent',
        borderRadius: R.md,
        minHeight: isMobile ? 52 : 44,
      }}
    >
      <ExecutorAvatar personaId={personaId} />
      {task ? (
        <button
          type="button"
          data-testid="task-open"
          onClick={openTask}
          title={`Открыть задачу${taskClosed ? ' (закрыта — для просмотра)' : ''}. Страница фичи скроется, вернётся по плашке.`}
          aria-label={`Открыть задачу «${title}»`}
          style={{
            flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap',
            background: 'transparent', border: 'none', cursor: 'pointer', padding: `${SP.xxs}px 0`,
            fontFamily: FONT.sans, textAlign: 'left',
          }}
        >
          {titleNode}
        </button>
      ) : (
        <div style={{ flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', padding: `${SP.xxs}px 0` }}>
          {titleNode}
        </div>
      )}
      {task && (
        <span style={{ marginLeft: 'auto', display: 'inline-flex', gap: SP.xs, flexShrink: 0 }}>
          <NavTargetPair
            chatId={chatId}
            chatArchived={marks.chatArchived(chatId)}
            onOpenChat={openChat}
            chatTitle="Открыть чат. Страница фичи скроется, вернётся по плашке."
            showNoChat
            taskId={task.id}
            taskClosed={taskClosed}
            onOpenTask={openTask}
            taskTitle="Открыть задачу. Страница фичи скроется, вернётся по плашке."
            isMobile={isMobile}
          />
        </span>
      )}
    </div>
  );
}

// Поясняющая строка (макет §9а, .navnote): куда ведёт строка и кнопка «чат».
function TasksNavNote() {
  return (
    <div
      data-testid="tasks-nav-note"
      style={{
        display: 'flex', gap: 6, alignItems: 'flex-start',
        background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
        padding: '6px 10px', marginTop: SP.sm, fontFamily: FONT.sans,
        fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.45,
      }}
    >
      <span aria-hidden style={{ flexShrink: 0, marginTop: 1 }}>ⓘ</span>
      <span>
        Строка ведёт в задачу шага, кнопка «чат» — в чат исполнителя. Страница фичи скроется
        и вернётся по плашке на этой же вкладке.
      </span>
    </div>
  );
}

export interface FeatureTasksTabProps {
  // Агрегат фичи: stepChains + stepProgress — источник шагов, задачи — служебные строки.
  run: FeatureRun;
  // Id фичи (навигация: точка возврата).
  featureId: string;
  // Пометки (страница даёт с чатами; без пропа — из агрегата).
  marks?: NavMarks;
  isMobile?: boolean;
}

export function FeatureTasksTab({ run, featureId, marks: marksProp, isMobile = false }: FeatureTasksTabProps) {
  const marks = useMemo(() => marksProp ?? buildNavMarks(run), [marksProp, run]);
  const model = useMemo(() => buildStepsTab(run), [run]);
  // Название фичи (epic.title) — для «← к фиче {название}» в шапке чата (Ф-pl-b).
  const featureTitle = run.epic?.title ?? null;
  const [filter, setFilter] = useState<StepsFilter>('all');
  const [expanded, setExpanded] = useState(false);

  const chips = useMemo(() => stepsFilterChips(model), [model]);
  // Выбранный фильтр мог исчезнуть (корзина опустела после обновления) — откат на «все».
  const active: StepsFilter = chips.some(c => c.key === filter) ? filter : 'all';
  const stepRows = useMemo(
    () => (active === 'service' ? [] : filterStepRows(model.steps, active)),
    [model, active],
  );
  const serviceRows = active === 'service' ? model.service : [];
  const total = active === 'service' ? serviceRows.length : stepRows.length;
  const shownSteps = expanded ? stepRows : stepRows.slice(0, TASKS_COLLAPSED_LIMIT);
  const shownService = expanded ? serviceRows : serviceRows.slice(0, TASKS_COLLAPSED_LIMIT);

  // Пустое: ни шагов, ни задач (фича создана, конвейер не запускался).
  if (model.steps.length === 0 && model.service.length === 0) {
    return (
      <div data-testid="tasks-empty" style={{ padding: `${SP.xl}px 0`, textAlign: 'center', fontFamily: FONT.sans }}>
        <div style={{ fontSize: FS.md, fontWeight: 700, color: C.textHeading, marginBottom: SP.xs }}>Шагов нет</div>
        <div style={{ fontSize: FS.sm, color: C.textMuted }}>Фича создана, но конвейер ещё не запускался.</div>
      </div>
    );
  }

  return (
    <div data-testid="tasks" style={{ display: 'flex', flexDirection: 'column', fontFamily: FONT.sans }}>
      {/* Фильтр: «все» + корзины трекера с числом > 0 + «в master», «снято», «служебные». */}
      <div data-testid="tasks-filter" role="group" aria-label="Фильтр шагов" style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs, marginBottom: SP.sm }}>
        {chips.map(chip => {
          const isActive = active === chip.key;
          return (
            <Button
              key={chip.key}
              variant="ghost"
              size="xs"
              dataTestId={`tasks-filter-${chip.key}`}
              ariaPressed={isActive}
              onClick={() => { setFilter(chip.key); setExpanded(false); }}
              style={{
                padding: '1px 8px', borderRadius: R.sm, lineHeight: '18px',
                fontSize: FS.xs, height: 'auto', whiteSpace: 'nowrap',
                background: isActive ? C.accent : C.bgSelected,
                color: isActive ? C.onAccent : C.textSecondary,
                border: 'none',
                minHeight: isMobile ? 40 : undefined,
              }}
            >
              {chip.label} {chip.count}
            </Button>
          );
        })}
      </div>

      <div data-testid="tasks-list" style={{ display: 'flex', flexDirection: 'column', gap: SP.xxs }}>
        {total === 0 ? (
          <div data-testid="tasks-filter-empty" style={{ fontSize: FS.sm, color: C.textMuted, padding: SP.xs }}>
            Шагов пока нет.
          </div>
        ) : (
          <>
            {shownSteps.map(r => {
              const tags: RowProps['tags'] = [{ tone: BUCKET_TONE[r.bucket], text: BUCKET_LABEL[r.bucket], testId: 'task-status' }];
              if (r.card.stopped) tags.push({ tone: 'danger', text: 'стоит', testId: 'task-stopped' });
              if (r.needsAnswer) tags.push({ tone: 'warn', text: BRIEF_WAIT_WORD, testId: 'task-wait' });
              const minutes = r.chain?.durationMinutes ?? 0;
              return (
                <StepsRow
                  key={r.card.stepRef}
                  testId={`step-row-${r.card.stepRef}`}
                  title={r.card.title}
                  role={roleLabel('step', r.card.stepRef, r.chain?.lane ?? null)}
                  personaId={r.card.personaId}
                  tags={tags}
                  meta={minutes > 0 ? formatDuration(minutes) : null}
                  task={r.headTask}
                  chatId={r.card.chatId}
                  featureId={featureId}
                  featureTitle={featureTitle}
                  marks={marks}
                  isMobile={isMobile}
                />
              );
            })}
            {shownService.map(t => {
              const tags: RowProps['tags'] = [{
                tone: t.status === 'done' ? 'ok' : t.status === 'inProgress' ? 'work' : 'muted',
                text: serviceStatusLabel(t), testId: 'task-status',
              }];
              if (t.status === 'inProgress' && t.awaitingHumanSince) tags.push({ tone: 'warn', text: BRIEF_WAIT_WORD, testId: 'task-wait' });
              return (
                <StepsRow
                  key={t.id}
                  testId={`task-row-${t.id}`}
                  title={t.title}
                  role={roleLabel(t.role ?? null, t.stepRef ?? null, t.lane ?? null)}
                  personaId={t.personaId ?? null}
                  tags={tags}
                  task={t}
                  chatId={t.linkedSessionId ?? null}
                  featureId={featureId}
                  featureTitle={featureTitle}
                  marks={marks}
                  isMobile={isMobile}
                />
              );
            })}
          </>
        )}
      </div>

      {/* «Показать все» / «Свернуть» при длинном списке (порог 8). */}
      {total > TASKS_COLLAPSED_LIMIT && (
        <Button
          variant="ghost"
          size="sm"
          fullWidth
          dataTestId="tasks-show-all"
          onClick={() => setExpanded(v => !v)}
          style={{
            marginTop: SP.sm,
            border: `1px solid ${C.border}`, background: C.bgSelected,
            borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`,
            fontSize: FS.sm, color: C.textHeading,
            minHeight: isMobile ? 40 : 32,
          }}
        >
          {expanded ? 'Свернуть' : `Показать все · ${total}`}
        </Button>
      )}

      <TasksNavNote />
    </div>
  );
}

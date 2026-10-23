// Модель карточек «Производства» (волна 4, W4-2F1, вариант 2А2).
//
// Чистая функция: из агрегата FeatureRun (stepChains, rounds, статусы задач) строит
// для каждого шага модель карточки: порядок по старту, узлы цепочки
// «работа → вернул → доработка → принят» со ссылками на чаты витков, счётчик
// циклов, текущий узел, причину возврата. Верстки здесь нет — её делает W4-2F2.
// Честная деградация: чего нет в агрегате (rounds, chatId, старт) — явно null или
// отсутствие узла, ничего не выдумывается.
//
// Источники (спецификация docs/mockups/features-overview-4-spec.md, точка 2):
//   • «работа»      → chain.chatId (чат исполнителя первой задачи цепочки);
//   • «вернул/принят» → round.reviewChatId соответствующего витка;
//   • «доработка» N-я → chatId исполнителя задачи chain.taskIds[N] из
//     run.stages[prod].executors по taskId;
//   • имя в подписи возврата — через необязательный резолвер personaName; без него
//     подпись остаётся без имени («вернул»), сырой UUID не показывается.

import type { FeatureRun, FeatureTask, FeatureStepChain, FeatureRound } from '../../types/featureRun';

// Состояние узла цепочки (тон задаёт рендер по таблице спецификации):
// ok — сделано, ret — возврат, cur — текущий (обведён акцентом), pend — впереди (пунктир).
export type ProdNodeState = 'ok' | 'ret' | 'cur' | 'pend';

export interface ProdNode {
  // Тип узла: работа / возврат / доработка / ревью (пунктирный, впереди) / принят.
  kind: 'work' | 'return' | 'rework' | 'review' | 'accept';
  // Подпись для рендера; с резолвером имени — «{Имя}: вернул», без — «вернул».
  label: string;
  state: ProdNodeState;
  // Чат витка: переход по узлу. null — узел не ссылка (не кнопка).
  chatId: string | null;
}

export interface ProdCard {
  stepRef: string;
  title: string;
  personaId: string | null;
  // Старт шага: claudeStartedAt ?? createdAt первой задачи; null — шага нет в работе.
  startedAt: string | null;
  // Порядок в ленте: 0 — самый ранний старт; без старта — в конце (стабильно).
  order: number;
  // Чат исполнителя шага («старт · чат · задача» меню).
  chatId: string | null;
  // Число циклов (возвратов): 0 — нет; 2+ рендер красит предупреждением.
  cycles: number;
  // Этот шаг — текущий фичи (run.now указывает на него или его задачи).
  isCurrent: boolean;
  // Шаг ждёт ревью (status «ждёт ревью» / outcome pending).
  awaitingReview: boolean;
  nodes: ProdNode[];
  // Причина последнего возврата (round.reason); null — её нет.
  returnReason: string | null;
}

// Карта задач агрегата: все задачи фичи, по id. Только читает, ничего не считает.
function taskMapOf(run: FeatureRun): Map<string, FeatureTask> {
  const m = new Map<string, FeatureTask>();
  for (const t of [run.epic, ...run.steps, ...run.reviews, ...run.qa, run.final, run.merge,
    ...(run.cut ?? []), ...(run.plan ?? [])]) {
    if (t) m.set(t.id, t);
  }
  return m;
}

// Старт цепочки: первая задача → claudeStartedAt ?? createdAt; нет — null.
function chainStart(chain: FeatureStepChain, taskMap: Map<string, FeatureTask>): string | null {
  const first = chain.taskIds.length > 0 ? taskMap.get(chain.taskIds[0]) : undefined;
  if (!first) return null;
  return first.claudeStartedAt ?? first.createdAt ?? null;
}

// Состояние узла по статусу задачи: идёт — cur, закрыта — ok, иначе — впереди (pend).
function taskNodeState(task: FeatureTask | undefined): ProdNodeState {
  if (!task) return 'pend';
  if (task.status === 'inProgress') return 'cur';
  if (task.status === 'done') return 'ok';
  return 'pend';
}

// Чат исполнителя доработки: по taskId из исполнителей стадии «Производство».
// Стадии или исполнителя нет — null (честно, без выдумки).
function reworkChatId(run: FeatureRun, taskId: string): string | null {
  const prod = (run.stages ?? []).find(s => s.key === 'prod');
  if (!prod) return null;
  const x = prod.executors.find(e => e.taskId === taskId);
  return x?.chatId ?? null;
}

// «Ждёт ревью»: готовая строка статуса бэка или фолбэк по outcome (старые ответы
// без status) — тот же принцип, что stepStatusView в FeatureTimelineBits.
function awaitingReviewOf(chain: FeatureStepChain): boolean {
  if (chain.status) return chain.status.toLowerCase().includes('ждет') || chain.status.toLowerCase().includes('ждёт');
  return chain.outcome === 'pending';
}

// Подпись витка: «{Имя}: вернул» / «принят с замечаниями» / «{Имя}: принят».
// Имя — только если резолвер дал; без него — короткая подпись без имени.
function roundLabel(round: FeatureRound, personaName?: (id: string) => string | null): string {
  const who = personaName && round.personaId ? personaName(round.personaId) : null;
  const verb = round.verdict === 'returned' ? 'вернул'
    : round.verdict === 'accepted_with_remarks' ? 'принят с замечаниями'
    : 'принят';
  return who ? `${who}: ${verb}` : verb;
}

// Узлы одной цепочки: «работа» → (вернул → доработка)* → «принят».
// Виток running — пунктирный «ревью» впереди (не кнопка). withdrawn — следа не оставляет.
function chainNodes(
  run: FeatureRun,
  chain: FeatureStepChain,
  taskMap: Map<string, FeatureTask>,
  personaName?: (id: string) => string | null,
): ProdNode[] {
  // Шаг не начат: одна пунктирная «работа», без ссылок (спецификация, краевые).
  if (!chainStart(chain, taskMap)) {
    return [{ kind: 'work', label: 'работа', state: 'pend', chatId: null }];
  }

  const nodes: ProdNode[] = [{
    kind: 'work',
    label: 'работа',
    state: taskNodeState(taskMap.get(chain.taskIds[0])),
    chatId: chain.chatId ?? null,
  }];

  // Доработки идут за возвратами: N-я доработка — chain.taskIds[N] (после задачи шага).
  let reworkIndex = 1;
  for (const round of chain.rounds ?? []) {
    if (round.verdict === 'returned' || round.verdict === 'accepted_with_remarks') {
      nodes.push({
        kind: 'return',
        label: roundLabel(round, personaName),
        state: 'ret',
        chatId: round.reviewChatId ?? null,
      });
      const reworkId = chain.taskIds[reworkIndex];
      if (reworkId) {
        nodes.push({
          kind: 'rework',
          label: 'доработка',
          state: taskNodeState(taskMap.get(reworkId)),
          chatId: reworkChatId(run, reworkId),
        });
        reworkIndex += 1;
      }
    } else if (round.verdict === 'accepted') {
      nodes.push({
        kind: 'accept',
        label: roundLabel(round, personaName),
        state: 'ok',
        chatId: round.reviewChatId ?? null,
      });
    } else if (round.verdict === 'running') {
      // Ревью впереди: пунктирный узел, никуда не ведёт. Метку «текущий» ставим на
      // последний узел-действие цепочки (работа или доработка), а не на давно завершённую
      // работу: пунктирный «ревью» — не узел-действие.
      const active = [...nodes].reverse().find(n => n.kind === 'work' || n.kind === 'rework');
      (active ?? nodes[0]!).state = 'cur';
      nodes.push({ kind: 'review', label: 'ревью', state: 'pend', chatId: null });
    }
    // withdrawn — узла нет: снятый виток цепочки не строит.
  }

  return nodes;
}

// Причина последнего возврата: причина ПОСЛЕДНЕГО витка-возврата. Если у него причины
// нет — null, а не подстановка из более раннего возврата (человек читает причину
// текущего состояния, а не историю).
function lastReturnReason(chain: FeatureStepChain): string | null {
  for (let i = (chain.rounds ?? []).length - 1; i >= 0; i--) {
    const r = chain.rounds[i];
    if (r.verdict === 'returned' || r.verdict === 'accepted_with_remarks') return r.reason ?? null;
  }
  return null;
}

// Модель ленты «Производство»: карточки на шаг в порядке старта (без старта — в конце,
// стабильно). stepChains нет или пусто — пустой список (рендер показывает существующее
// пустое состояние).
export function buildProdCards(
  run: FeatureRun,
  personaName?: (id: string) => string | null,
): ProdCard[] {
  const chains = run.stepChains ?? [];
  if (chains.length === 0) return [];

  const taskMap = taskMapOf(run);

  // Порядок: по старту ascending; без старта — после всех, внутри групп — исходный порядок.
  const ordered = chains.map((chain, i) => ({ chain, start: chainStart(chain, taskMap), i }));
  ordered.sort((a, b) => {
    if (a.start && b.start) return a.start < b.start ? -1 : a.start > b.start ? 1 : a.i - b.i;
    if (a.start) return -1;
    if (b.start) return 1;
    return a.i - b.i;
  });

  return ordered.map(({ chain }, idx) => {
    // «Текущий» — по run.now: шаг или одна из его задач сейчас InProgress.
    const now = run.now ?? [];
    const isCurrent = now.some(n => n.stepRef === chain.stepRef || chain.taskIds.includes(n.taskId));
    return {
      stepRef: chain.stepRef,
      title: chain.title,
      personaId: chain.personaId ?? null,
      startedAt: chainStart(chain, taskMap),
      order: idx,
      chatId: chain.chatId ?? null,
      cycles: chain.loops ?? 0,
      isCurrent,
      awaitingReview: awaitingReviewOf(chain),
      nodes: chainNodes(run, chain, taskMap, personaName),
      returnReason: lastReturnReason(chain),
    };
  });
}

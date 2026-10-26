// Модель карточек «Производства» (волна 4, W4-2F1, вариант 2А2).
//
// Чистая функция: из агрегата FeatureRun (stepChains, rounds, статусы задач) строит
// для каждого шага модель карточки: порядок по старту, узлы цепочки
// «работа → вернул → доработка → принят» со ссылками на чаты витков, счётчик
// циклов, текущий узел, причину возврата. Верстки здесь нет — её делает W4-2F2.
// Честная деградация: чего нет в агрегате (rounds, chatId, старт) — явно null или
// отсутствие узла, ничего не выдумывается.
//
// Правила GC-F3 («Гант на Хронологии»): (а) последнее ревью accepted/accepted_with_remarks
// закрывает шаг — фронт этого не выводит заново: бэк уже учёл хвостовую доработку
// (FeatureStepAccounting: IsAcceptance(lastVerdict) && !reworkOpen), карточка верит
// chain.outcome, второй истины нет; (б) шаг плана без задачи (бэковый notCreated) —
// синтетическая карточка state='queue', planned:true, в конце ленты, в порядке плана.

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

// Состояние шага для ленты (макет A2): done — готово, run — идёт, wait — ждёт, queue — в очереди,
// withdrawn — снят с выкладки. Выводится из корзины бэка (bucketOf), конфликтов приоритетов нет.
export type ProdCardState = 'done' | 'run' | 'wait' | 'queue' | 'withdrawn';

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
  // Шаг в корзине «ревью»: ждёт ревью или виток ревью идёт (плашка различает по chain.outcome).
  awaitingReview: boolean;
  // Исполнитель идущей задачи остановлен (тревога stopped): состояние wait, не run.
  stopped: boolean;
  nodes: ProdNode[];
  // Состояние шага (иконка, тон карточки и плашка).
  state: ProdCardState;
  // Шаг плана без задачи (бэковый notCreated): пунктирная карточка «в плане, не заведён»
  // (GC-F3; верстка — GC-F8). false — реальный шаг цепочки.
  planned: boolean;
  // Чем занят идущий шаг: работа / доработка; null — шаг не идёт.
  runKind: 'work' | 'rework' | null;
  // С какого момента идёт текущая задача (claudeStartedAt, иначе createdAt); null — неизвестно.
  runningSince: string | null;
  // Когда шаг начал ждать ревью / когда принят — поля бэка; null, пока их нет в агрегате.
  waitingSince: string | null;
  acceptedAt: string | null;
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

// Корзина шага — зеркало FeatureStepAccounting на бэке (единственный источник правды, своей
// эвристики нет): withdrawn → accepted → work → review. Идущий виток ревью (outcome «running» при
// всех закрытых задачах цепочки) — корзина review, а не work: шаг ждёт ревью, как и в сводке.
type ProdBucket = 'withdrawn' | 'accepted' | 'review' | 'work' | 'notCreated';

function bucketOf(chain: FeatureStepChain, tasks: FeatureTask[], started: boolean): ProdBucket {
  if (chain.outcome === 'withdrawn') return 'withdrawn';
  if (chain.outcome === 'accepted') return 'accepted';
  if (!started) return 'notCreated';
  if (chain.outcome === 'pending') return 'review';
  // Неполный агрегат (задач цепочки не хватает) «все закрыты» доказать не может — остаёмся в work:
  // ложное «ждёт ревью» хуже, чем осторожное «ждёт»; полный агрегат даёт точный ответ.
  const allDone = chain.taskIds.length > 0 && chain.taskIds.length === tasks.length && tasks.every(t => t.status === 'done');
  if (chain.outcome === 'running' && allDone) return 'review';
  return 'work';
}

// Состояние шага — из корзины: accepted → done, withdrawn → withdrawn, review → wait,
// notCreated → queue; work → run, если задача идёт и её исполнитель не остановлен (остановленный —
// тревога stopped: wait), иначе wait: корзина work без идущей задачи — «ждёт», а слово плашки
// («закрыт без повторного ревью», «вернулся…») у бэка (chain.status). run.now состояние не меняет.
function cardState(
  chain: FeatureStepChain, tasks: FeatureTask[], started: boolean,
): { state: ProdCardState; runTask: FeatureTask | null; stopped: boolean; review: boolean } {
  const out = (state: ProdCardState, runTask: FeatureTask | null = null, stopped = false, review = false) =>
    ({ state, runTask, stopped, review });
  const bucket = bucketOf(chain, tasks, started);
  if (bucket === 'withdrawn') return out('withdrawn');
  if (bucket === 'accepted') return out('done');
  if (bucket === 'notCreated') return out('queue');
  if (bucket === 'review') return out('wait', null, false, true);
  const running = tasks.find(t => t.status === 'inProgress') ?? null;
  // Как на бэке (FeatureProjector): пометка актуальна, только если новее старта исполнителя.
  if (running?.executorStoppedAt
    && (!running.claudeStartedAt || Date.parse(running.executorStoppedAt) > Date.parse(running.claudeStartedAt))) {
    return out('wait', null, true);
  }
  if (running) return out('run', running);
  return out('wait');
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

// Ключ плана для сверки с цепочками — тот же, что на бэке (FeatureStepAccounting.KeyOf +
// StepKeyComparer): трим и регистронезависимо («Б1» и «б1» — один шаг, «Б1 » и «Б1» — один шаг).
function planKey(stepRef: string): string {
  return stepRef.trim().toLowerCase();
}

// Шаги плана без цепочки — синтетические карточки «в очереди» (зеркало бэкового notCreated:
// FeatureStepAccounting считает ключ плана, которого нет среди заведённых шагов). Пересечения
// с заведёнными нет: ключ совпадает с бэковым; дубль stepRef в плане — первое вхождение;
// пустой stepRef — не шаг. Карточка без старта: после всех цепочек, в порядке плана (тот же
// порядок, что у бэковых линий: сначала заведённые шаги, затем notCreated).
function plannedCards(run: FeatureRun, chainKeys: Set<string>, startIndex: number): ProdCard[] {
  const out: ProdCard[] = [];
  const seen = new Set<string>();
  for (const p of run.plan ?? []) {
    if (!p) continue;
    for (const ps of p.planSteps ?? []) {
      if (!ps || !ps.stepRef?.trim()) continue;
      const key = planKey(ps.stepRef);
      if (chainKeys.has(key) || seen.has(key)) continue;
      seen.add(key);
      out.push({
        stepRef: ps.stepRef.trim(),
        title: ps.title,
        personaId: null,
        startedAt: null,
        order: startIndex + out.length,
        chatId: null,
        cycles: 0,
        isCurrent: false,
        awaitingReview: false,
        stopped: false,
        // Как у не начатого шага: одна пунктирная «работа», без ссылок.
        nodes: [{ kind: 'work', label: 'работа', state: 'pend', chatId: null }],
        state: 'queue',
        planned: true,
        runKind: null,
        runningSince: null,
        waitingSince: null,
        acceptedAt: null,
        returnReason: null,
      });
    }
  }
  return out;
}

// Модель ленты «Производство»: карточки на шаг в порядке старта (без старта — в конце,
// стабильно), после них — шаги плана без задачи в порядке плана. Ни цепочек, ни плана —
// пустой список (рендер показывает существующее пустое состояние).
export function buildProdCards(
  run: FeatureRun,
  personaName?: (id: string) => string | null,
): ProdCard[] {
  const chains = run.stepChains ?? [];

  const taskMap = taskMapOf(run);

  // Порядок: по старту ascending; без старта — после всех, внутри групп — исходный порядок.
  const ordered = chains.map((chain, i) => ({ chain, start: chainStart(chain, taskMap), i }));
  ordered.sort((a, b) => {
    if (a.start && b.start) return a.start < b.start ? -1 : a.start > b.start ? 1 : a.i - b.i;
    if (a.start) return -1;
    if (b.start) return 1;
    return a.i - b.i;
  });

  const cards: ProdCard[] = ordered.map(({ chain }, idx) => {
    // «Текущий» — по run.now: шаг или одна из его задач сейчас InProgress. Как в Ганте
    // (buildGanttModel): у закрытой/смерженной фичи никакой шаг не подсвечен.
    const finished = run.closed === true || run.state === 'merged' || run.state === 'closed';
    const now = finished ? [] : (run.now ?? []);
    const isCurrent = now.some(n => n.stepRef === chain.stepRef || chain.taskIds.includes(n.taskId));
    const started = chainStart(chain, taskMap) !== null;
    const tasks = chain.taskIds.map(id => taskMap.get(id)).filter((t): t is FeatureTask => !!t);
    const { state, runTask, stopped, review: awaiting } = cardState(chain, tasks, started);
    const nodes = chainNodes(run, chain, taskMap, personaName);
    const runKind = state !== 'run' ? null
      : [...nodes].reverse().find(n => n.kind === 'rework' || n.kind === 'work')?.kind === 'rework' ? 'rework' : 'work';
    return {
      stepRef: chain.stepRef,
      title: chain.title,
      personaId: chain.personaId ?? null,
      startedAt: chainStart(chain, taskMap),
      order: idx,
      chatId: chain.chatId ?? null,
      cycles: chain.loops ?? 0,
      isCurrent,
      awaitingReview: awaiting,
      stopped,
      nodes,
      state,
      planned: false,
      runKind,
      runningSince: runTask ? (runTask.claudeStartedAt ?? runTask.createdAt ?? null) : null,
      waitingSince: chain.waitingSince ?? null,
      acceptedAt: chain.acceptedAt ?? null,
      returnReason: lastReturnReason(chain),
    };
  });

  // Шаги плана без цепочки — после всех карточек цепочек (GC-F3, правило (б)).
  const chainKeys = new Set(chains.map(c => planKey(c.stepRef)));
  return [...cards, ...plannedCards(run, chainKeys, cards.length)];
}

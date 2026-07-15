export const meta = {
  name: 'team-implement',
  description: 'Командная реализация «разбить и раздать»: декомпозиция задачи в под-задачи по владению файлами → раздача персонам-исполнителям → исполнение волнами (последовательно или параллельно в worktree) → merge → verify тестами/сборкой',
  whenToUse: 'Разбить крупную задачу на независимые куски и поручить их команде исполнителей. Передавай args: { task, executors, worktree, verify }. task — что реализовать; executors — массив типов сабагентов (handle персон-исполнителей); worktree=true — параллельно в изолированных worktree с последующим merge (иначе последовательная раздача в общем дереве); verify=false отключает финальную проверку.',
  phases: [
    { title: 'Декомпозиция' },
    { title: 'Исполнение' },
    { title: 'Merge' },
    { title: 'Verify' },
  ],
}

// ---- Вводные (терпимый парс) ----
const a = (() => {
  if (typeof args === 'string') { try { return JSON.parse(args) } catch { return {} } }
  return (args && typeof args === 'object') ? args : {}
})()
const task = (typeof a.task === 'string' && a.task.trim()) ? a.task.trim() : 'Задача не задана'
const useWorktree = a.worktree === true
const doVerify = a.verify !== false

// Персоны-исполнители (round-robin по под-задачам); без них — стандартный агент
const executors = Array.isArray(a.executors)
  ? a.executors.map(p => (typeof p === 'string' ? p.trim() : '')).filter(Boolean)
  : []
const execOpts = (j) => (executors.length ? { agentType: executors[j % executors.length] } : {})
const execTag = (j) => (executors.length ? ` @${executors[j % executors.length]}` : '')

// ---- Схемы ----
const SUBTASKS_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    plan: { type: 'string', description: 'Короткое описание, как задача разбита и почему' },
    subtasks: {
      type: 'array', description: 'Под-задачи. Делай их максимально НЕЗАВИСИМЫМИ — непересекающиеся файлы (file-ownership), минимум зависимостей.',
      items: {
        type: 'object', additionalProperties: false,
        properties: {
          id: { type: 'string', description: 'Короткий уникальный id (kebab-case)' },
          title: { type: 'string' },
          description: { type: 'string', description: 'Что именно сделать: файлы, ожидаемое поведение, критерий готовности' },
          files: { type: 'array', items: { type: 'string' }, description: 'Файлы/каталоги, которыми владеет эта под-задача (не пересекаются с другими)' },
          dependencies: { type: 'array', items: { type: 'string' }, description: 'id под-задач, которые должны быть готовы раньше (пусто — независима)' },
        },
        required: ['id', 'title', 'description'],
      },
    },
  },
  required: ['plan', 'subtasks'],
}

const EXEC_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    status: { type: 'string', enum: ['готово', 'частично', 'провал'] },
    summary: { type: 'string', description: 'Что сделано по факту' },
    filesTouched: { type: 'array', items: { type: 'string' } },
    notes: { type: 'string', description: 'Проблемы, отклонения, что не доделано' },
  },
  required: ['status', 'summary'],
}

const VERIFY_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    passed: { type: 'boolean', description: 'Прошли ли сборка/тесты после команды' },
    summary: { type: 'string' },
    fixesApplied: { type: 'array', items: { type: 'string' }, description: 'Правки, внесённые для починки (если чинил)' },
    remaining: { type: 'array', items: { type: 'string' }, description: 'Что осталось красным/незакрытым' },
  },
  required: ['passed', 'summary'],
}

// ---- Фаза 1: декомпозиция ----
phase('Декомпозиция')
const decomposition = await agent(`Ты — ПЛАНИРОВЩИК командной реализации. Разбей задачу на под-задачи для параллельной работы команды исполнителей.

ЗАДАЧА: ${task}

Изучи репозиторий по фактам (структура, затронутые модули). Затем разбей на под-задачи по принципу ВЛАДЕНИЯ ФАЙЛАМИ: каждая под-задача владеет своим непересекающимся набором файлов, чтобы исполнители не конфликтовали. Минимизируй зависимости между под-задачами; неизбежные — укажи явно в dependencies.
${useWorktree
  ? 'Исполнение будет ПАРАЛЛЕЛЬНЫМ в изолированных worktree — непересечение файлов критично.'
  : 'Исполнение будет ПОСЛЕДОВАТЕЛЬНЫМ в общем дереве в порядке зависимостей.'}
Если в окружении доступен инструмент tasks_create — создай эти под-задачи в системе задач (по одной, с описанием), чтобы пользователь видел прогресс; в любом случае верни структуру. Отвечай по-русски.`,
  { label: 'Декомпозиция задачи', phase: 'Декомпозиция', schema: SUBTASKS_SCHEMA })

const subtasks = (decomposition && Array.isArray(decomposition.subtasks)) ? decomposition.subtasks : []
if (subtasks.length === 0) {
  return { task, error: 'Декомпозиция не дала под-задач', decomposition }
}

// ---- Топологические волны по зависимостям (Kahn) ----
function buildWaves(items) {
  const ids = new Set(items.map(s => s.id))
  const done = new Set()
  const remaining = items.slice()
  const waves = []
  let guard = 0
  while (remaining.length && guard++ < items.length + 1) {
    // готовы те, чьи зависимости (из известных id) уже выполнены
    const ready = remaining.filter(s =>
      (s.dependencies || []).filter(d => ids.has(d)).every(d => done.has(d)))
    const batch = ready.length ? ready : remaining.slice() // цикл зависимостей → берём всё, что осталось
    waves.push(batch)
    for (const s of batch) { done.add(s.id); remaining.splice(remaining.indexOf(s), 1) }
  }
  if (remaining.length) waves.push(remaining) // страховка
  return waves
}
const waves = buildWaves(subtasks)

// ---- Фаза 2: исполнение волнами ----
phase('Исполнение')
const execPrompt = (s) => `Ты — ИСПОЛНИТЕЛЬ командной реализации. Реализуй ПОЛНОСТЬЮ свою под-задачу и ничего сверх неё — не трогай файлы других под-задач.

ОБЩАЯ ЗАДАЧА: ${task}

ТВОЯ ПОД-ЗАДАЧА: ${s.title}
${s.description}
${s.files && s.files.length ? 'Твои файлы (владей только ими): ' + s.files.join(', ') : ''}

Доведи до рабочего состояния: внеси правки, следуй стилю окружающего кода. Если в окружении есть tasks_update — отметь статус своей задачи (в работе → готово). Верни честный отчёт: если что-то не доделал — так и скажи. Отвечай по-русски.`

const execResults = []
let globalIndex = 0
for (const wave of waves) {
  if (useWorktree && wave.length > 1) {
    // параллельно, каждый исполнитель в своём worktree (изоляция параллельных мутаций)
    const batch = await parallel(wave.map((s) => {
      const j = globalIndex++
      return () => agent(execPrompt(s), { label: `Исполнение: ${s.title}${execTag(j)}`, phase: 'Исполнение', schema: EXEC_SCHEMA, isolation: 'worktree', ...execOpts(j) })
        .then(r => ({ subtask: s, result: r }))
    }))
    execResults.push(...batch.filter(Boolean))
  } else {
    // последовательно в общем дереве (безопасно от конфликтов)
    for (const s of wave) {
      const j = globalIndex++
      const r = await agent(execPrompt(s), { label: `Исполнение: ${s.title}${execTag(j)}`, phase: 'Исполнение', schema: EXEC_SCHEMA, ...execOpts(j) })
      execResults.push({ subtask: s, result: r })
    }
  }
}

const execDigest = execResults.map(e =>
  `- ${e.subtask.title}: ${e.result ? `${e.result.status} — ${e.result.summary}` : '(исполнитель не отчитался)'}`
).join('\n')

// ---- Фаза 3: merge (только для параллельного worktree-режима) ----
let merge = null
const parallelHappened = useWorktree && waves.some(w => w.length > 1)
if (parallelHappened) {
  phase('Merge')
  merge = await agent(`Ты — ИНТЕГРАТОР командной реализации. Исполнители работали параллельно в изолированных worktree. Сведи их изменения в рабочее дерево единым согласованным состоянием.

ОБЩАЯ ЗАДАЧА: ${task}

Что сделали исполнители:
${execDigest}

Проверь текущее состояние git (ветки/worktree/diff), собери изменения в рабочее дерево, разреши конфликты по смыслу общей задачи. Верни отчёт о том, что сведено и какие конфликты были. Отвечай по-русски.`,
    { label: 'Merge изменений', phase: 'Merge', schema: EXEC_SCHEMA })
}

// ---- Фаза 4: verify ----
let verify = null
if (doVerify) {
  phase('Verify')
  verify = await agent(`Ты — ВЕРИФИКАТОР командной реализации. Проверь, что суммарный результат работает.

ОБЩАЯ ЗАДАЧА: ${task}

Что сделала команда:
${execDigest}

Запусти релевантные проверки этого репозитория (например для фронта: cd frontend && npm run build; для бэка: cd backend && dotnet build / dotnet test). Если проверка красная — почини по месту (в пределах разумного) и перезапусти. Верни: прошло ли, что чинил, что осталось красным. Отвечай по-русски.`,
    { label: 'Проверка результата', phase: 'Verify', schema: VERIFY_SCHEMA })
}

return {
  task,
  worktree: useWorktree,
  plan: decomposition.plan,
  subtaskCount: subtasks.length,
  waves: waves.map(w => w.map(s => s.id)),
  execResults: execResults.map(e => ({ id: e.subtask.id, title: e.subtask.title, result: e.result })),
  merge,
  verify,
}

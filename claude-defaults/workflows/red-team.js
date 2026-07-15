export const meta = {
  name: 'red-team',
  description: 'Красная команда: N атакующих с разных углов (краевые случаи, безопасность, неверные допущения, нагрузка, режимы отказа) ищут, как сломать готовый план/решение/PR, каждый держит свою «теорию поломки» → синтез уязвимостей с рекомендациями',
  whenToUse: 'Стресс-проверка готового артефакта на прочность перед принятием. Передавай args: { target, angles, participants }. target — что атакуем (план/решение/дифф/PR); angles — массив углов из [edge-cases, security, wrong-assumptions, load-scale, failure-modes]; participants — необязательный массив типов сабагентов (handle персон) по порядку углов.',
  phases: [
    { title: 'Атака' },
    { title: 'Усиление' },
    { title: 'Синтез' },
  ],
}

// ---- Вводные (терпимый парс) ----
const a = (() => {
  if (typeof args === 'string') { try { return JSON.parse(args) } catch { return {} } }
  return (args && typeof args === 'object') ? args : {}
})()
const target = (typeof a.target === 'string' && a.target.trim()) ? a.target.trim() : 'предложенное решение/план в текущем контексте разговора'

// Углы атаки: каталог с фокусом. Роль задаётся ПРОМПТОМ (как в panel-of-experts);
// agentType навешивается только когда угол играет персона-участник (см. roleOpts).
const ANGLE_CATALOG = {
  'edge-cases': { title: 'Краевые случаи',
    focus: 'пустые/предельные/некорректные входы, границы диапазонов, гонки, порядок событий, пере/недополнение, юникод/локали' },
  'security': { title: 'Безопасность',
    focus: 'модель угроз, злоупотребления, обход авторизации, инъекции, утечки, эскалация прав, доверие к внешним данным' },
  'wrong-assumptions': { title: 'Неверные допущения',
    focus: 'скрытые предпосылки, «этого не случится», зависимости от порядка/окружения, что будет, если предположение ложно' },
  'load-scale': { title: 'Нагрузка и масштаб',
    focus: 'поведение на большом объёме/конкуренции, деградация, таймауты, узкие места, исчерпание ресурсов, каскадные отказы' },
  'failure-modes': { title: 'Режимы отказа',
    focus: 'частичные сбои, недоступность зависимостей, повторные попытки/идемпотентность, восстановление, потеря данных при обрыве' },
}

const requested = Array.isArray(a.angles) ? a.angles.map(x => String(x).trim()) : []
let angles = requested.filter(k => ANGLE_CATALOG[k])
if (angles.length === 0) angles = ['edge-cases', 'wrong-assumptions', 'failure-modes']

// Без участника угол играет стандартный агент, роль задаёт промпт.
const participants = Array.isArray(a.participants)
  ? a.participants.map(p => (typeof p === 'string' ? p.trim() : '')).slice(0, angles.length)
  : []
const roleOpts = (i) => (participants[i] ? { agentType: participants[i] } : {})
const angleTag = (i) => (participants[i] ? ` @${participants[i]}` : '')

// ---- Схемы ----
const ATTACK_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    overall: { type: 'string', description: 'Насколько решение устойчиво с этого угла — общий вывод атакующего' },
    vulnerabilities: {
      type: 'array', description: 'Найденные способы сломать (пустой массив — если пробить не удалось)',
      items: {
        type: 'object', additionalProperties: false,
        properties: {
          title: { type: 'string', description: 'Короткое имя уязвимости/слабости' },
          scenario: { type: 'string', description: 'Конкретный сценарий поломки: вход/состояние/действие → сломанный результат' },
          severity: { type: 'string', enum: ['критичная', 'серьёзная', 'умеренная', 'мелкая'] },
          fix: { type: 'string', description: 'Как закрыть/смягчить' },
        },
        required: ['title', 'scenario', 'severity'],
      },
    },
  },
  required: ['overall', 'vulnerabilities'],
}

const SYNTH_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    verdict: { type: 'string', description: 'Итог: насколько решение прочно, стоит ли принимать как есть' },
    topRisks: { type: 'array', items: { type: 'string' }, description: 'Главные риски по убыванию критичности' },
    mustFix: { type: 'array', items: { type: 'string' }, description: 'Что закрыть обязательно до принятия' },
    recommendation: { type: 'string', enum: ['принять', 'принять с доработками', 'переделать'] },
  },
  required: ['verdict', 'recommendation'],
}

const fmtVuln = (v) => `[${v.severity}] ${v.title} — ${v.scenario}${v.fix ? '\n    как закрыть: ' + v.fix : ''}`

// ---- Фаза 1: атака (все углы параллельно) ----
phase('Атака')
const attacks = await parallel(angles.map((angleKey, i) => () => {
  const A = ANGLE_CATALOG[angleKey]
  const prompt = `Ты — АТАКУЮЩИЙ в красной команде, твой угол — «${A.title}». Твоя цель не хвалить, а СЛОМАТЬ решение именно с этого угла: ${A.focus}.
Ты держишь свою «теорию поломки» и ищешь конкретные способы, как всё пойдёт не так.

ЧТО АТАКУЕМ: ${target}

Сначала пойми решение по фактам (прочитай затронутый код/план), затем предметно атакуй со своего угла: конкретные сценарии поломки, не общие рассуждения. Для каждого — как это воспроизвести и как закрыть.
Если пробить с этого угла честно не удалось — так и скажи (пустой список), не выдумывай. Отвечай по-русски.`
  return agent(prompt, { label: `Атака: ${A.title}${angleTag(i)}`, phase: 'Атака', schema: ATTACK_SCHEMA, ...roleOpts(i) })
    .then(r => ({ angleKey, title: A.title, result: r }))
}))

const rawAttacks = attacks.filter(Boolean).filter(x => x.result)

// ---- Фаза 2: усиление (каждый видит находки соседей, дополняет) ----
phase('Усиление')
const attackDigest = rawAttacks.map(x =>
  `### Угол «${x.title}» — ${x.result.overall}\n` +
  ((x.result.vulnerabilities || []).map(v => '  - ' + fmtVuln(v)).join('\n') || '  (пробить не удалось)')
).join('\n\n')

// усиливаем только если атакующих больше одного и есть что показать соседям
const reinforced = rawAttacks.length > 1
  ? await parallel(rawAttacks.map((x, i) => () =>
      agent(`Ты — АТАКУЮЩИЙ красной команды, угол «${x.title}». Ты уже атаковал; теперь видишь находки коллег по другим углам. Усиль свою атаку: добавь новые сценарии поломки на стыке углов или разверни то, что коллеги задели вскользь. НЕ повторяй уже названное. Если добавить нечего — верни пустой список.

ЧТО АТАКУЕМ: ${target}

Находки всей красной команды:
${attackDigest}

Верни ТОЛЬКО новые уязвимости со своего угла (или пустой список). Отвечай по-русски.`,
        { label: `Усиление: ${x.title}`, phase: 'Усиление', schema: ATTACK_SCHEMA, ...roleOpts(i) })
        .then(r => ({ angleKey: x.angleKey, title: x.title, result: r })))
    )
  : []

// ---- Сбор уязвимостей ----
const SEV_ORDER = { 'критичная': 0, 'серьёзная': 1, 'умеренная': 2, 'мелкая': 3 }
const all = []
for (const x of rawAttacks) for (const v of (x.result.vulnerabilities || [])) all.push({ ...v, angle: x.title })
for (const x of reinforced.filter(Boolean)) if (x.result) for (const v of (x.result.vulnerabilities || [])) all.push({ ...v, angle: x.title })
all.sort((p, q) => (SEV_ORDER[p.severity] ?? 9) - (SEV_ORDER[q.severity] ?? 9))

// ---- Фаза 3: синтез ----
phase('Синтез')
const vulnBlock = all.length
  ? all.map((v, i) => `${i + 1}. [${v.angle}] ${fmtVuln(v)}`).join('\n')
  : '(красной команде не удалось пробить решение)'

const synthesis = await agent(`Ты — капитан красной команды. Атакующие по углам (${angles.map(k => ANGLE_CATALOG[k].title).join(', ')}) пытались сломать решение. Сведи итог.

ЧТО АТАКОВАЛИ: ${target}

Все найденные уязвимости (по убыванию критичности):
${vulnBlock}

Дай итог: насколько решение прочно, главные риски, что обязательно закрыть до принятия, и рекомендацию. Конкретно и по делу. Отвечай по-русски.`,
  { label: 'Синтез красной команды', phase: 'Синтез', schema: SYNTH_SCHEMA })

return {
  target,
  angles: angles.map(k => ANGLE_CATALOG[k].title),
  totalVulnerabilities: all.length,
  vulnerabilities: all,
  synthesis,
}

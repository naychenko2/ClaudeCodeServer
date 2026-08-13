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

// ---- Обёртка над agent({schema}) с авто-ретраем ----
// Движок Workflow сам нуджит агента ОДИН раз и при неудаче отдаёт null; мы добавляем ещё
// одну попытку с усиленным требованием финального StructuredOutput, чтобы не терять находки
// молча в `filter(Boolean)`. Трактуем как сбой и идём в ретрай и возврат null/undefined, и
// исключение первой попытки (parallel логирует «failed:» именно при throw — см. реальный
// кейс wf_8b7fbf4d-433). Если и ретрай пустой/упавший — пишем потерю в лог прогона с
// раздельной формулировкой причины и возвращаем null, не роняя стадию.
async function structuredAgent(prompt, opts) {
  const label = opts.label || '(без label)'

  // Первая попытка: и null/undefined, и исключение — повод идти в ретрай
  let firstFailedReason = null
  let first = null
  try {
    first = await agent(prompt, opts)
    if (first !== null && first !== undefined) return first
    firstFailedReason = 'не вызвал StructuredOutput'
  } catch (err) {
    firstFailedReason = `упал с ошибкой: ${err && err.message ? err.message : String(err)}`
  }

  const retryPrompt = prompt +
    `\n\n⚠️ КРИТИЧНО: предыдущий ход ${firstFailedReason}. ` +
    'Сейчас ОБЯЗАТЕЛЬНО заверши работу явным вызовом StructuredOutput, вернув объект, ' +
    'строго соответствующий заявленной схеме. Не пиши итоговый текст в ответ — ' +
    'верни структуру через StructuredOutput.'
  const retryOpts = { ...opts, label: opts.label ? `${opts.label} · повтор` : 'повтор' }

  // Вторая попытка: исключение НЕ роняет стадию — логируем потерю и возвращаем null
  let second = null
  let secondFailedReason = null
  try {
    second = await agent(retryPrompt, retryOpts)
    if (second !== null && second !== undefined) return second
    secondFailedReason = 'не вызвал StructuredOutput'
  } catch (err) {
    secondFailedReason = `упал с ошибкой: ${err && err.message ? err.message : String(err)}`
  }

  log(`⚠️ structuredAgent: «${label}» — агент дважды не дал результат (1: ${firstFailedReason}; 2: ${secondFailedReason}), находки утрачены`)
  return null
}

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

// Подсказка агенту про обязательные поля схемы: перечисляет их поимённо и задаёт
// пустое значение (например `vulnerabilities: []`), чтобы модель не опускала поле
// вместо валидного «нет находок». Держим рядом со схемами — правишь `required`,
// правится и подсказка.
function requiredFieldsReminder(schema) {
  const fields = (schema.required || []).map(name => {
    const p = schema.properties && schema.properties[name]
    if (p && p.type === 'array') return `\`${name}: []\``
    if (p && p.type === 'string') return `\`${name}: ""\``
    if (p && p.type === 'boolean') return `\`${name}: false\``
    if (p && (p.type === 'number' || p.type === 'integer')) return `\`${name}: 0\``
    return `\`${name}\``
  })
  return `\n\n⚠️ ОБЯЗАТЕЛЬНЫЕ ПОЛЯ ОТВЕТА: ${fields.join(', ')}. Все перечисленные поля обязательны и должны присутствовать в ответе. Если значения нет — верни пустое значение (как показано в скобках), а не опускай поле. Пропуск обязательного поля = невалидный ответ, ход упадёт.`
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
Если пробить с этого угла честно не удалось — так и скажи (пустой список), не выдумывай. Отвечай по-русски.${requiredFieldsReminder(ATTACK_SCHEMA)}`
  return structuredAgent(prompt, { label: `Атака: ${A.title}${angleTag(i)}`, phase: 'Атака', schema: ATTACK_SCHEMA, ...roleOpts(i) })
    .then(r => ({ angleKey, title: A.title, result: r }))
}))

const rawAttacks = attacks.filter(Boolean).filter(x => x.result)

// ---- Потери фазы «Атака» ----
// Пришли — углы, чьи атакующие дали результат; выпали — те, чьи атакующие
// дважды не вернули StructuredOutput. Если углы выпали, покрытие молча сузилось,
// и это надо объявить в синтезе и в возврате.
const arrivedAngles = new Set(rawAttacks.map(x => x.angleKey))
const lostAttackAngles = angles.filter(k => !arrivedAngles.has(k))

// ---- Фаза 2: усиление (каждый видит находки соседей, дополняет) ----
phase('Усиление')
const attackDigest = rawAttacks.map(x =>
  `### Угол «${x.title}» — ${x.result.overall}\n` +
  ((x.result.vulnerabilities || []).map(v => '  - ' + fmtVuln(v)).join('\n') || '  (пробить не удалось)')
).join('\n\n')

// усиливаем только если атакующих больше одного и есть что показать соседям
const reinforced = rawAttacks.length > 1
  ? await parallel(rawAttacks.map((x, i) => () =>
      structuredAgent(`Ты — АТАКУЮЩИЙ красной команды, угол «${x.title}». Ты уже атаковал; теперь видишь находки коллег по другим углам. Усиль свою атаку: добавь новые сценарии поломки на стыке углов или разверни то, что коллеги задели вскользь. НЕ повторяй уже названное. Если добавить нечего — верни пустой список.

ЧТО АТАКУЕМ: ${target}

Находки всей красной команды:
${attackDigest}

Верни ТОЛЬКО новые уязвимости со своего угла (или пустой список). Отвечай по-русски.${requiredFieldsReminder(ATTACK_SCHEMA)}`,
        { label: `Усиление: ${x.title}`, phase: 'Усиление', schema: ATTACK_SCHEMA, ...roleOpts(i) })
        .then(r => ({ angleKey: x.angleKey, title: x.title, result: r })))
    )
  : []

// ---- Потери фазы «Усиление» ----
const reinforcedOk = reinforced.filter(Boolean).filter(x => x.result)
const lostReinforced = rawAttacks.filter(x => !reinforcedOk.find(y => y.angleKey === x.angleKey))

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

// Потери: выпавшие углы в «Атаке» и в «Усилении» — капитан обязан оговорить
// неполноту в verdict: неполный охват ≠ полный охват.
const lostStages = [
  ...lostAttackAngles.map(k => ({ phase: 'Атака', stage: `угол «${ANGLE_CATALOG[k].title}»`, reason: 'агент не вернул результат ни в первой, ни во второй попытке' })),
  ...lostReinforced.map(x => ({ phase: 'Усиление', stage: `угол «${x.title}»`, reason: 'агент не вернул результат усиления' })),
]
const lostBlock = lostStages.length
  ? lostStages.map(s => `  - [${s.phase}] ${s.stage} — ${s.reason}`).join('\n')
  : '(потерь нет)'

const synthesis = await structuredAgent(`Ты — капитан красной команды. Атакующие по углам (${angles.map(k => ANGLE_CATALOG[k].title).join(', ')}) пытались сломать решение. Сведи итог.

ЧТО АТАКОВАЛИ: ${target}

Все найденные уязвимости (по убыванию критичности):
${vulnBlock}

⚠️ НЕПОЛНОТА ПОКРЫТИЯ — обязательно учти в verdict:
${lostBlock}
${lostStages.length ? 'Покрытие механики сузилось. Если выпали критичные для этого решения углы — понизь уверенность вердикта и явно укажи, какие риски остались непроверенными.' : ''}

Дай итог: насколько решение прочно, главные риски, что обязательно закрыть до принятия, и рекомендацию. Конкретно и по делу. Отвечай по-русски.${requiredFieldsReminder(SYNTH_SCHEMA)}`,
  { label: 'Синтез красной команды', phase: 'Синтез', schema: SYNTH_SCHEMA })

return {
  target,
  angles: angles.map(k => ANGLE_CATALOG[k].title),
  totalVulnerabilities: all.length,
  vulnerabilities: all,
  synthesis,
  lostStages,
}

export const meta = {
  name: 'panel-of-experts',
  description: 'Многоагентная дискуссия: Генератор → Критик → Адвокат → Модератор (2–3 раунда, синтез)',
  whenToUse: 'Глубокий разбор сложной развилки с разных сторон: генерация идей, жёсткая критика, защита, синтез взвешенного решения. Передавай args: { topic, brief, rounds, participants }. participants — необязательный массив типов сабагентов (например handle персон-консультантов) для ролей по порядку: [Генератор, Критик, Адвокат, Модератор]; роли без участника играет стандартный агент.',
  phases: [
    { title: 'Раунд 1' },
    { title: 'Раунд 2' },
    { title: 'Раунд 3' },
    { title: 'Финальный синтез' },
  ],
}

// ---- Вводные ----
// Модель нередко передаёт args СТРОКОЙ с JSON вместо объекта — тогда args.topic/participants
// молча теряются (панель шла с «Тема дискуссии не задана» и без персон). Парсим терпимо.
const a = (() => {
  if (typeof args === 'string') { try { return JSON.parse(args) } catch { return {} } }
  return (args && typeof args === 'object') ? args : {}
})()
const topic = a.topic || 'Тема дискуссии не задана'
const brief = a.brief || '(дополнительный контекст не передан)'
const maxRounds = Math.max(1, Math.min(3, a.rounds || 3))

// Участники-персоны (опционально): a.participants — типы сабагентов для ролей
// [Генератор, Критик, Адвокат, Модератор] по порядку. Персона играет роль СВОИМ
// характером (промпт роли дополняет её собственный системный промпт сабагента).
const participants = Array.isArray(a.participants)
  ? a.participants.map(p => (typeof p === 'string' ? p.trim() : '')).slice(0, 4)
  : []
const roleOpts = (i) => (participants[i] ? { agentType: participants[i] } : {})
const roleTag = (i) => (participants[i] ? ` @${participants[i]}` : '')

// ---- Обёртка над agent({schema}) с авто-ретраем ----
// Движок Workflow сам нуджит агента ОДИН раз и при неудаче отдаёт null; мы добавляем ещё
// одну попытку с усиленным требованием финального StructuredOutput, чтобы не терять реплики
// молча. Трактуем как сбой и идём в ретрай и возврат null/undefined, и исключение первой
// попытки (parallel логирует «failed:» именно при throw). Если и ретрай пустой/упавший —
// пишем потерю в лог прогона с раздельной формулировкой причины и возвращаем null, не роняя
// стадию.
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

  log(`⚠️ structuredAgent: «${label}» — агент дважды не дал результат (1: ${firstFailedReason}; 2: ${secondFailedReason}), реплика утрачена`)
  return null
}

// ---- Схемы структурированного вывода ----
const PROPOSER_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    framing: { type: 'string', description: 'Как Генератор формулирует суть развилки' },
    proposals: {
      type: 'array', description: '2–4 варианта решения',
      items: {
        type: 'object', additionalProperties: false,
        properties: {
          title: { type: 'string' },
          idea: { type: 'string' },
          rationale: { type: 'string' },
        },
        required: ['title', 'idea', 'rationale'],
      },
    },
  },
  required: ['framing', 'proposals'],
}

const CRITIC_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    overall: { type: 'string', description: 'Общий вердикт скептика' },
    critiques: {
      type: 'array',
      items: {
        type: 'object', additionalProperties: false,
        properties: {
          target: { type: 'string', description: 'Какую идею/тезис критикует' },
          weakness: { type: 'string' },
          severity: { type: 'string', enum: ['критичная', 'серьёзная', 'умеренная'] },
        },
        required: ['target', 'weakness', 'severity'],
      },
    },
  },
  required: ['overall', 'critiques'],
}

const SUPPORTER_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    strengthened: { type: 'string', description: 'Усиленная версия идей с учётом критики' },
    defenses: {
      type: 'array',
      items: {
        type: 'object', additionalProperties: false,
        properties: {
          critique: { type: 'string', description: 'На какую критику отвечает' },
          rebuttal: { type: 'string' },
          mitigation: { type: 'string', description: 'Как обойти/смягчить препятствие' },
        },
        required: ['critique', 'rebuttal'],
      },
    },
  },
  required: ['strengthened', 'defenses'],
}

const JUDGE_INTERIM_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    assessment: { type: 'string' },
    surviving: { type: 'array', items: { type: 'string' }, description: 'Идеи, выдержавшие критику' },
    rejected: { type: 'array', items: { type: 'string' }, description: 'Отброшенные варианты' },
    contestedPoints: { type: 'array', items: { type: 'string' }, description: 'Спорные вопросы для следующего раунда; пустой массив = консенсус достигнут' },
  },
  required: ['assessment', 'surviving', 'contestedPoints'],
}

const JUDGE_FINAL_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    decision: { type: 'string', description: 'Итоговое решение по каждой развилке' },
    rationale: { type: 'string' },
    tradeoffs: { type: 'array', items: { type: 'string' } },
    recommendation: { type: 'array', items: { type: 'string' }, description: 'Конкретные рекомендации/шаги' },
    residualRisks: { type: 'array', items: { type: 'string' } },
  },
  required: ['decision', 'rationale', 'recommendation', 'residualRisks'],
}

// ---- Общий контекст (транскрипт дискуссии) ----
const transcript = []
function render() {
  if (!transcript.length) return '(дискуссия ещё не начата)'
  return transcript.map(e => `### [Раунд ${e.round}] ${e.role}\n${e.content}`).join('\n\n')
}
function fmtProposer(p) {
  return `Постановка: ${p.framing}\nИдеи:\n` +
    p.proposals.map((x, i) => `  ${i + 1}. ${x.title} — ${x.idea} (почему: ${x.rationale})`).join('\n')
}
function fmtCritic(c) {
  return `Общий вердикт: ${c.overall}\nКритика:\n` +
    c.critiques.map((x, i) => `  ${i + 1}. [${x.severity}] ${x.target}: ${x.weakness}`).join('\n')
}
function fmtSupporter(s) {
  return `Усиленная версия: ${s.strengthened}\nЗащита:\n` +
    s.defenses.map((x, i) => `  ${i + 1}. На «${x.critique}»: ${x.rebuttal}${x.mitigation ? ' | обход: ' + x.mitigation : ''}`).join('\n')
}
function fmtJudgeInterim(j) {
  return `Оценка: ${j.assessment}\nВыжившие идеи: ${(j.surviving || []).join('; ')}\nОтброшено: ${(j.rejected || []).join('; ')}\nСпорные вопросы: ${(j.contestedPoints || []).join('; ')}`
}

// ---- Дискуссия ----
let openQuestions = ''
let round = 0
// Потери ролей: «капитан» обязан оговорить неполноту в финальном синтезе.
const lostStages = []
while (round < maxRounds) {
  round++
  const ph = `Раунд ${round}`
  phase(ph)

  // 1. Генератор
  const proposerPrompt = round === 1
    ? `Ты — ГЕНЕРАТОР (Идеолог) в панели экспертов. Твоя задача — выдвигать смелые гипотезы и нестандартные подходы, без оглядки на ограничения; ты задаёшь тон дискуссии и даёшь остальным «пищу для размышлений».

ТЕМА ДИСКУССИИ: ${topic}

КОНТЕКСТ И ВВОДНЫЕ:
${brief}

Предложи 2–4 содержательных варианта решения. Для каждого — суть и обоснование. Мысли как сильный архитектор/стратег, не бойся радикальных идей. Отвечай по-русски.`
    : `Ты — ГЕНЕРАТОР в панели экспертов, идёт раунд ${round}. Модератор по итогам прошлого раунда оставил спорные/открытые вопросы:
${openQuestions}

Вся дискуссия до этого момента:
${render()}

Доработай идеи или предложи новые ИМЕННО по открытым вопросам — с учётом высказанной критики и защиты. Отвечай по-русски.`
  const prop = await structuredAgent(proposerPrompt, { label: `Генератор${roleTag(0)} · р${round}`, phase: ph, schema: PROPOSER_SCHEMA, ...roleOpts(0) })
  if (prop) transcript.push({ round, role: 'Генератор', content: fmtProposer(prop) })
  else lostStages.push({ phase: `Раунд ${round}`, stage: 'Генератор', reason: 'агент не вернул реплику' })

  // 2. Критик
  const crit = await structuredAgent(`Ты — КРИТИК (Скептик) в панели экспертов. Твоя единственная задача — разрушать идеи Генератора: искать уязвимости, риски, логические нестыковки и слабые места. Ты НЕ предлагаешь своих решений. Будь жёстким, конкретным и предметным.

ТЕМА: ${topic}

Вся дискуссия:
${render()}

Разбери последние предложения Генератора и покажи, почему они могут не сработать. Отвечай по-русски.`, { label: `Критик${roleTag(1)} · р${round}`, phase: ph, schema: CRITIC_SCHEMA, ...roleOpts(1) })
  if (crit) transcript.push({ round, role: 'Критик', content: fmtCritic(crit) })
  else lostStages.push({ phase: `Раунд ${round}`, stage: 'Критик', reason: 'агент не вернул реплику' })

  // 3. Адвокат
  const supp = await structuredAgent(`Ты — АДВОКАТ (Оптимист) в панели экспертов. Твоя задача — защищать идеи Генератора от Критика: фокусироваться на сильных сторонах, искать аргументы «за», развивать потенциал идей и придумывать, как обойти препятствия, на которые указал Критик.

ТЕМА: ${topic}

Вся дискуссия:
${render()}

Ответь на критику по пунктам и усиль идеи. Отвечай по-русски.`, { label: `Адвокат${roleTag(2)} · р${round}`, phase: ph, schema: SUPPORTER_SCHEMA, ...roleOpts(2) })
  if (supp) transcript.push({ round, role: 'Адвокат', content: fmtSupporter(supp) })
  else lostStages.push({ phase: `Раунд ${round}`, stage: 'Адвокат', reason: 'агент не вернул реплику' })

  // 4. Модератор (промежуточно)
  const judge = await structuredAgent(`Ты — МОДЕРАТОР (Синтезатор) в панели экспертов, раунд ${round} из ${maxRounds}. Ты слушаешь спор Генератора, Критика и Адвоката и беспристрастно взвешиваешь аргументы.

ТЕМА: ${topic}

Вся дискуссия:
${render()}

Дай ПРОМЕЖУТОЧНЫЙ синтез: какие идеи выдержали критику, что отбрасываем, какие спорные вопросы остаются нерешёнными. Если консенсус достигнут и спорных вопросов не осталось — верни ПУСТОЙ массив contestedPoints. Отвечай по-русски.`, { label: `Модератор${roleTag(3)} · р${round}`, phase: ph, schema: JUDGE_INTERIM_SCHEMA, ...roleOpts(3) })
  if (judge) {
    transcript.push({ round, role: 'Модератор (промежуточно)', content: fmtJudgeInterim(judge) })
    openQuestions = (judge.contestedPoints || []).map((q, i) => `${i + 1}. ${q}`).join('\n')
    if (!judge.contestedPoints || judge.contestedPoints.length === 0) break // консенсус → ранняя остановка
  } else {
    lostStages.push({ phase: `Раунд ${round}`, stage: 'Модератор (промежуточно)', reason: 'агент не вернул синтез раунда' })
  }
}

// ---- Финальный синтез ----
phase('Финальный синтез')
const lostBlock = lostStages.length
  ? lostStages.map(s => `  - [${s.phase}] ${s.stage} — ${s.reason}`).join('\n')
  : '(потерь нет)'

const final = await structuredAgent(`Ты — МОДЕРАТОР (Синтезатор) в панели экспертов. Дискуссия завершена. Проанализируй весь спор и выдай ИТОГОВОЕ, максимально проработанное и жизнеспособное решение.

ТЕМА: ${topic}

КОНТЕКСТ И ВВОДНЫЕ:
${brief}

Полная дискуссия:
${render()}

⚠️ НЕПОЛНОТА ПОКРЫТИЯ — обязательно учти в residualRisks:
${lostBlock}
${lostStages.length ? 'Дискуссия велась с дырами: часть ролей или раундов не дала реплик. Если выпали критичные для темы роли (например, Критик без реплики — нет давления на Генератора) — повысь residualRisks и явно укажи, какие аспекты остались неоспоренными.' : ''}

Взвесь плюсы (от Адвоката) и минусы (от Критика), отбрось нерабочее и синтезируй финальную позицию: решение по каждой развилке, обоснование, ключевые компромиссы, конкретные рекомендации/шаги и остаточные риски. Будь конкретным и практичным. Отвечай по-русски.`, { label: `Финальный синтез${roleTag(3)}`, phase: 'Финальный синтез', schema: JUDGE_FINAL_SCHEMA, ...roleOpts(3) })

if (!final) lostStages.push({ phase: 'Финальный синтез', stage: 'Модератор (финал)', reason: 'агент не вернул итоговое решение' })

return { topic, roundsRun: round, final, transcript, lostStages }

export const meta = {
  name: 'review-consilium',
  description: 'Ревью-консилиум: параллельное ревью с N независимых линз (корректность, безопасность, тесты, архитектура, производительность) + adversarial verify каждой находки + синтез по severity',
  whenToUse: 'Разобрать дифф/PR/файлы командой независимых ревьюеров, каждый со своей осью. Передавай args: { target, lenses, participants, verify }. target — что ревьюим (по умолчанию текущий git diff); lenses — массив осей из [correctness, security, tests, architecture, performance]; participants — необязательный массив типов сабагентов (handle персон) по порядку осей; verify=false отключает фазу опровержения.',
  phases: [
    { title: 'Ревью по осям' },
    { title: 'Проверка находок' },
    { title: 'Синтез' },
  ],
}

// ---- Вводные (терпимый парс: модель нередко шлёт args строкой с JSON) ----
const a = (() => {
  if (typeof args === 'string') { try { return JSON.parse(args) } catch { return {} } }
  return (args && typeof args === 'object') ? args : {}
})()
const target = (typeof a.target === 'string' && a.target.trim()) ? a.target.trim() : 'текущие незакоммиченные изменения (git diff / git status)'
const doVerify = a.verify !== false

// Оси ревью: каталог с фокусом. Роль задаётся ПРОМПТОМ (как в panel-of-experts);
// agentType навешивается только когда ось играет персона-участник (см. roleOpts).
const LENS_CATALOG = {
  correctness: { title: 'Корректность',
    focus: 'логические дефекты, краевые случаи, ошибки состояния/конкурентности, нарушенные инварианты, регрессии' },
  security: { title: 'Безопасность',
    focus: 'инъекции, авторизация/доступ, утечки секретов, небезопасная десериализация, path traversal, OWASP-класс' },
  tests: { title: 'Тесты',
    focus: 'покрытие изменений, незакрытые ветки/краевые случаи, хрупкие и ложнозелёные тесты, отсутствие негативных сценариев' },
  architecture: { title: 'Архитектура',
    focus: 'нарушения слоёв/границ, дублирование, преждевременные абстракции, связность/связанность, соответствие принятым паттернам репозитория' },
  performance: { title: 'Производительность',
    focus: 'N+1, лишние аллокации/копии, блокирующий IO на горячем пути, неоптимальная асимптотика, утечки ресурсов' },
}

// Выбранные оси (валидируем по каталогу; дефолт — базовая тройка)
const requested = Array.isArray(a.lenses) ? a.lenses.map(x => String(x).trim()) : []
let lenses = requested.filter(k => LENS_CATALOG[k])
if (lenses.length === 0) lenses = ['correctness', 'security', 'tests']

// Персоны на оси (опционально): participants[i] играет ось lenses[i] своим характером.
// Без участника ось играет стандартный агент, роль задаёт промпт.
const participants = Array.isArray(a.participants)
  ? a.participants.map(p => (typeof p === 'string' ? p.trim() : '')).slice(0, lenses.length)
  : []
const roleOpts = (i) => (participants[i] ? { agentType: participants[i] } : {})
const lensTag = (i) => (participants[i] ? ` @${participants[i]}` : '')

// ---- Схемы структурированного вывода ----
const FINDINGS_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    summary: { type: 'string', description: 'Общее впечатление ревьюера по этой оси' },
    findings: {
      type: 'array', description: 'Найденные проблемы (пустой массив — если чисто)',
      items: {
        type: 'object', additionalProperties: false,
        properties: {
          file: { type: 'string', description: 'Путь к файлу (repo-relative)' },
          line: { type: 'number', description: '1-based строка, к которой относится находка; 0 если неприменимо' },
          severity: { type: 'string', enum: ['критичная', 'серьёзная', 'умеренная', 'мелкая'] },
          summary: { type: 'string', description: 'Суть дефекта одним предложением' },
          scenario: { type: 'string', description: 'Конкретный сценарий сбоя: вход/состояние → неверный выход/крах' },
        },
        required: ['file', 'severity', 'summary', 'scenario'],
      },
    },
  },
  required: ['summary', 'findings'],
}

const VERDICT_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    verdict: { type: 'string', enum: ['подтверждено', 'опровергнуто', 'сомнительно'] },
    reason: { type: 'string', description: 'Почему находка реальна или почему опровергнута' },
  },
  required: ['verdict', 'reason'],
}

const SYNTH_SCHEMA = {
  type: 'object', additionalProperties: false,
  properties: {
    verdict: { type: 'string', description: 'Итоговый вердикт по изменениям: можно ли мержить и с какими оговорками' },
    blocking: { type: 'array', items: { type: 'string' }, description: 'Блокирующие проблемы (критичные/серьёзные), которые надо закрыть до мержа' },
    nonBlocking: { type: 'array', items: { type: 'string' }, description: 'Некритичные замечания на потом' },
    recommendation: { type: 'string', enum: ['мержить', 'мержить с правками', 'доработать'] },
  },
  required: ['verdict', 'recommendation'],
}

const fmtFinding = (f) => `[${f.severity}] ${f.file}${f.line ? ':' + f.line : ''} — ${f.summary}\n    сценарий: ${f.scenario}`

// ---- Фаза 1+2: ревью по осям, каждая ось верифицируется как только готова ----
const perLens = await pipeline(
  lenses,
  // stage 1 — ревьюер оси
  (lensKey, _orig, i) => {
    const L = LENS_CATALOG[lensKey]
    const prompt = `Ты — независимый РЕВЬЮЕР в консилиуме, отвечаешь ТОЛЬКО за ось «${L.title}».
Смотри узко в свою ось, не подменяй другие: ${L.focus}.

ЧТО РЕВЬЮИМ: ${target}

Сначала собери, что именно изменилось (git diff / прочитай затронутые файлы), затем разбери это по своей оси.
Возвращай конкретные, предметные находки с точными файлами/строками и правдоподобным сценарием сбоя.
Если по твоей оси всё чисто — верни пустой массив находок и скажи об этом в summary. Не выдумывай проблемы ради галочки. Отвечай по-русски.`
    return agent(prompt, { label: `Ревью: ${L.title}${lensTag(i)}`, phase: 'Ревью по осям', schema: FINDINGS_SCHEMA, ...roleOpts(i) })
      .then(r => ({ lensKey, title: L.title, result: r }))
  },
  // stage 2 — adversarial verify находок этой оси (each по отдельности, скептик пытается опровергнуть)
  async (lensOut) => {
    if (!lensOut || !lensOut.result) return lensOut
    const found = lensOut.result.findings || []
    if (!doVerify || found.length === 0) {
      return { ...lensOut, verified: found.map(f => ({ ...f, verdict: 'подтверждено' })) }
    }
    const checked = await parallel(found.map(f => () =>
      agent(`Ты — придирчивый ПРОВЕРЯЮЩИЙ. Другой ревьюер заявил проблему в коде — твоя задача её ОПРОВЕРГНУТЬ, если она несостоятельна.

ОСЬ: ${lensOut.title}
ЗАЯВЛЕННАЯ ПРОБЛЕМА: ${fmtFinding(f)}

ЧТО РЕВЬЮИМ: ${target}

Проверь по фактам кода: реально ли воспроизводится заявленный сценарий сбоя? Не защищён ли этот случай где-то ещё? Не выдумка ли это.
В сомнении не отбрасывай (verdict=сомнительно). Отвечай по-русски.`,
        { label: `Проверка: ${f.file}`, phase: 'Проверка находок', schema: VERDICT_SCHEMA })
        .then(v => ({ ...f, verdict: v ? v.verdict : 'сомнительно', verifyReason: v ? v.reason : '' }))
    ))
    // отбрасываем только явно опровергнутое
    return { ...lensOut, verified: checked.filter(Boolean).filter(f => f.verdict !== 'опровергнуто') }
  },
)

// ---- Сбор подтверждённых находок ----
const SEV_ORDER = { 'критичная': 0, 'серьёзная': 1, 'умеренная': 2, 'мелкая': 3 }
const all = []
const lensSummaries = []
for (const lo of perLens.filter(Boolean)) {
  lensSummaries.push(`- **${lo.title}**: ${lo.result ? lo.result.summary : '(ось не отработала)'}`)
  for (const f of (lo.verified || [])) all.push({ ...f, lens: lo.title })
}
all.sort((x, y) => (SEV_ORDER[x.severity] ?? 9) - (SEV_ORDER[y.severity] ?? 9))

// ---- Фаза 3: синтез ----
phase('Синтез')
const findingsBlock = all.length
  ? all.map((f, i) => `${i + 1}. [${f.lens}] ${fmtFinding(f)}`).join('\n')
  : '(подтверждённых проблем не найдено)'

const synthesis = await agent(`Ты — ведущий консилиума. Ревьюеры по осям (${lenses.map(k => LENS_CATALOG[k].title).join(', ')}) разобрали изменения, находки прошли проверку на опровержение. Сведи итог.

ЧТО РЕВЬЮИЛИ: ${target}

Резюме ревьюеров по осям:
${lensSummaries.join('\n')}

Подтверждённые находки (по убыванию критичности):
${findingsBlock}

Дай итоговый вердикт: что блокирует мерж (критичные/серьёзные), что можно на потом, и рекомендацию. Будь конкретным и практичным, без воды. Отвечай по-русски.`,
  { label: 'Синтез консилиума', phase: 'Синтез', schema: SYNTH_SCHEMA })

return {
  target,
  lenses: lenses.map(k => LENS_CATALOG[k].title),
  verified: doVerify,
  totalFindings: all.length,
  findings: all,
  synthesis,
}

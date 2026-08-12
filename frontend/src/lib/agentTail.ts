// Системный хвост результата сабагента (Task/Agent): CLI дописывает к финальному
// ответу служебные строки вида
//   agentId: a011da168d23b9e32 (use SendMessage with to: '…', summary: '…' to continue this agent)
//   <usage>subagent_tokens: 30161
//   tool_uses: 1
//   duration_ms: 31510</usage>
// В ленте это выглядит мусором — вырезаем из текста и отдаём метрики отдельно,
// чтобы карточки рендерили их аккуратной строкой «токены · действия · время».

export interface AgentResultTail {
  agentId?: string;
  tokens?: number;
  toolUses?: number;
  durationMs?: number;
}

// Блок <usage>…</usage> в самом конце текста
const USAGE_RE = /\n?\s*<usage>([\s\S]*?)<\/usage>\s*$/;
// Строка «agentId: <id> (use SendMessage …)» в самом конце текста (скобка опциональна)
const AGENT_ID_RE = /(?:^|\n)\s*agentId:\s*([\w-]+)(?:\s*\([^)]*\))?\s*$/;

export function splitAgentResultTail(result: string): { body: string; tail: AgentResultTail | null } {
  // Обе регулярки ниже сканируют текст целиком, а результаты инструментов бывают
  // в десятки килобайт — на ленте это выходило в сотни миллисекунд при открытии чата.
  // Хвост есть у считанных элементов, поэтому сначала дешёвая проверка подстрокой:
  // нет ни одного маркера — разбирать нечего.
  if (!result.includes('<usage>') && !result.includes('agentId:')) return { body: result, tail: null };

  let body = result;
  const tail: AgentResultTail = {};
  let found = false;

  const usage = body.match(USAGE_RE);
  if (usage && usage.index !== undefined) {
    found = true;
    body = body.slice(0, usage.index);
    for (const line of usage[1].split('\n')) {
      const kv = line.match(/^\s*(\w+):\s*(\d+)\s*$/);
      if (!kv) continue;
      const value = Number(kv[2]);
      if (kv[1] === 'subagent_tokens') tail.tokens = value;
      else if (kv[1] === 'tool_uses') tail.toolUses = value;
      else if (kv[1] === 'duration_ms') tail.durationMs = value;
    }
  }

  const agentId = body.match(AGENT_ID_RE);
  if (agentId && agentId.index !== undefined) {
    found = true;
    tail.agentId = agentId[1];
    body = body.slice(0, agentId.index);
  }

  return found ? { body: body.trimEnd(), tail } : { body: result, tail: null };
}

// Квитанция ФОНОВОГО запуска сабагента (run_in_background): tool_result приходит сразу,
// но это служебная метаинформация CLI («Async agent launched successfully… agentId…
// output_file…»), а не ответ — показывать её пользователю нельзя. Ответ агента
// доезжает в ленту его транскриптом (agent_text) по мере работы.
export function isAsyncLaunchAck(result: string | null | undefined): boolean {
  return /^Async agent launched successfully/i.test((result ?? '').trimStart());
}

// Текст тела карточки вместо квитанции фонового запуска: пока агент жив — «работает
// в фоне», после прерывания (bgAborted) — честная пометка про обрыв выдачи. Категоричного
// «задача не завершена» не пишем: мы знаем лишь, что поток не дошёл до финального блока,
// а координатор мог восстановить результат другим каналом (resume, чтение файла) — это
// тот же дефект, что bgEmptyAnswerNote ниже. В точке рендера (ToolUseView) признака
// активности нет, поэтому говорим только про выдачу, без утверждений о судьбе задачи.
export function asyncLaunchAckNote(bgAborted: boolean | undefined): string {
  return bgAborted === true
    ? 'Выдача прервана — ответа нет'
    : 'Агент работает в фоне — его ход виден в списке действий.';
}

// Подпись тела карточки консультанта, когда ответного текста в потоке нет.
// Разводим обрыв выдачи (bgAborted — поток не дошёл до финального блока) и штатное
// завершение без текста: последнее бывает, когда результат получен координатором другим
// каналом (resume, чтение файла плана), а в нашу ленту финальная реплика не попала.
// Категоричного «ответа не будет» не пишем никогда — оно противоречит секции «Активность»
// (агент работал) и возможному восстановлению результата координатором.
// undefined → карточка покажет дефолт «Ответ передан без текста».
export function bgEmptyAnswerNote(opts: {
  settledNoText: boolean;    // фоновый агент завершился (bgDone), а ответного текста в потоке нет
  bgAborted: boolean | undefined;
  hasToolActivity: boolean;  // в «Активности» есть вызовы инструментов — агент проявлял активность
}): string | undefined {
  if (!opts.settledNoText) return undefined;
  if (opts.bgAborted === true) {
    return opts.hasToolActivity
      ? 'Выдача прервана — детали в Активности'
      : 'Выдача прервана — ответа нет';
  }
  // Завершился штатно без текста — результат мог дойти до координатора вне карточки;
  // «прерван» тут ложь, оставляем дефолт.
  return undefined;
}

// Любая квитанция фонового запуска (Agent run_in_background / Workflow / resume агента):
// по такому result судить о завершённости НЕЛЬЗЯ — он приходит мгновенно при старте.
// Достоверный признак завершения — bgDone (событие bg_agent_done) либо workflowDone.
export function isBgLaunchResult(result: string | null | undefined): boolean {
  if (!result) return false;
  return isAsyncLaunchAck(result)
    || result.includes('Transcript dir:')
    || result.includes('resumed from transcript in the background');
}

// «30161» → «30,2k», «133903» → «134k» — как fmtTok в плашке result
export function formatTailTokens(n: number): string {
  return n >= 1000 ? (n / 1000).toFixed(n >= 10000 ? 0 : 1).replace('.', ',') + 'k' : String(n);
}

// «31510» → «32с», «772726» → «12м 53с»
export function formatTailDuration(ms: number): string {
  const totalSec = Math.round(ms / 1000);
  if (totalSec < 60) return `${totalSec}с`;
  const min = Math.floor(totalSec / 60);
  const sec = totalSec % 60;
  return sec > 0 ? `${min}м ${sec}с` : `${min}м`;
}

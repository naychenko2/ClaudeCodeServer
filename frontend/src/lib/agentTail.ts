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

// Спайк ADR-016: драйвер сценариев со стороны «сервера». Ведёт себя как ClaudeSession:
// держит relay-client обычным дочерним процессом, пишет stream-json в stdin, отвечает на
// control_request can_use_tool, шлёт interrupt, убивает ход по TurnId.
//
//   node driver.mjs <сценарий> [аргументы]   — сценарии см. SCENARIOS внизу
import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import readline from 'node:readline';

const HERE = path.dirname(new URL(import.meta.url).pathname);
const GATEWAY = 'http://127.0.0.1:18701';
const WORK = '/tmp/ccs-spike';
const [projectId, sessionId] = fs.readFileSync(path.join(WORK, 'ids.txt'), 'utf8').trim().split('\n');
const OUT = path.join(WORK, 'results');
fs.mkdirSync(OUT, { recursive: true });

// Секреты сервера — только чтобы искать их на «устройстве»; в вывод идут одни булевы флаги
function serverSecrets() {
  const cred = JSON.parse(fs.readFileSync(process.env.GW_OAUTH_FILE, 'utf8')).claudeAiOauth;
  return {
    oauthAccess: cred.accessToken,
    oauthRefresh: cred.refreshToken,
    deepseekKey: JSON.parse(fs.readFileSync(process.env.GW_PROVIDERS_FILE, 'utf8')).LlmProviders.deepseek.ApiKey,
    minimaxKey: JSON.parse(fs.readFileSync(process.env.GW_PROVIDERS_FILE, 'utf8')).LlmProviders.minimax.ApiKey,
    backendJwt: fs.readFileSync(process.env.GW_BACKEND_JWT_FILE, 'utf8').trim(),
    backendJwtSecret: fs.readFileSync(path.join(WORK, 'devdata', 'jwt-secret.txt'), 'utf8').trim(),
  };
}

function scanProc(pid, extra) {
  const secrets = { ...serverSecrets(), ...extra };
  const res = {};
  for (const f of ['environ', 'cmdline']) {
    let text = '';
    try { text = fs.readFileSync(`/proc/${pid}/${f}`, 'latin1'); } catch (e) { res[f] = 'unreadable: ' + e.code; continue; }
    res[f] = Object.fromEntries(Object.entries(secrets).map(([k, v]) => [k, v && text.includes(v)]));
    if (f === 'environ') res.envKeys = text.split('\0').filter(Boolean).map(s => s.split('=')[0]);
  }
  return res;
}

async function mintTurn(provider) {
  const r = await fetch(GATEWAY + '/admin/turn', { method: 'POST', body: JSON.stringify({ provider, sessionId }) });
  return (await r.json()).token;
}

function mcpConfig() {
  return {
    mcpServers: {
      tasks: { type: 'http', url: `sidecar:/mcp/tasks/${sessionId}`, alwaysLoad: true },
      memory: { type: 'http', url: `sidecar:/mcp/memory/-/${projectId}`, alwaysLoad: true },
    },
  };
}

const userMsg = text => JSON.stringify({ type: 'user', message: { role: 'user', content: text } }) + '\n';

// Один процесс CLI на «устройстве». prompts — очередь сообщений: следующее уходит после result.
async function runTurn({ name, provider = 'subscription', model = 'sonnet', prompts, resume, mcp, onDelta }) {
  const turnId = crypto.randomBytes(6).toString('hex');
  const turnToken = await mintTurn(provider);
  const args = ['--print', '--verbose', '--output-format', 'stream-json', '--input-format', 'stream-json',
    '--include-partial-messages', '--permission-prompt-tool', 'stdio', '--model', model];
  if (resume) args.push('--resume', resume);
  const specFile = path.join(WORK, `spec-${turnId}.json`);
  fs.writeFileSync(specFile, JSON.stringify({ turnId, args, turnToken, mcpConfig: mcp ? mcpConfig() : undefined }), { mode: 0o600 });
  const infoFile = path.join(WORK, `info-${turnId}.json`);
  const t0 = Date.now();
  const proc = spawn('node', [path.join(HERE, 'relay-client.mjs'), 'spawn', specFile],
    { env: { ...process.env, RELAY_INFO_FILE: infoFile }, stdio: ['pipe', 'pipe', 'pipe'] });
  const log = fs.createWriteStream(path.join(OUT, `${name}.stream.jsonl`));
  let stderr = '';
  proc.stderr.on('data', d => { stderr += d; });

  const r = { name, turnId, provider, model, deltas: [], results: [], toolUses: [], toolResults: [], controlRequests: 0, init: null, procScan: null };
  const queue = [...prompts];
  proc.stdin.write(userMsg(queue.shift()));
  const api = {
    interrupt() { proc.stdin.write(JSON.stringify({ type: 'control_request', request_id: 'int_' + turnId, request: { subtype: 'interrupt' } }) + '\n'); r.interruptSentMs = Date.now() - t0; },
    kill() { r.killSentMs = Date.now() - t0; r.killReply = execFileSync('node', [path.join(HERE, 'relay-client.mjs'), 'kill', turnId]).toString().trim(); },
    stopped: false,
  };

  for await (const line of readline.createInterface({ input: proc.stdout })) {
    log.write(line + '\n');
    let ev; try { ev = JSON.parse(line); } catch { continue; }
    if (ev.type === 'system' && ev.subtype === 'init') {
      r.init = { sessionId: ev.session_id, model: ev.model, mcp_servers: ev.mcp_servers, mcpTools: ev.tools?.filter(t => t.startsWith('mcp__')) };
      // Env и argv живого CLI — пока ход идёт
      if (!r.procScan && fs.existsSync(infoFile)) {
        r.pid = JSON.parse(fs.readFileSync(infoFile, 'utf8')).pid;
        r.procScan = scanProc(r.pid, { turnToken });
      }
    } else if (ev.type === 'stream_event' && ev.event?.type === 'content_block_delta') {
      r.deltas.push(Date.now() - t0);
      onDelta?.(r.deltas.length, api);
    } else if (ev.type === 'assistant') {
      for (const c of ev.message?.content ?? []) if (c.type === 'tool_use') r.toolUses.push(c.name);
    } else if (ev.type === 'user') {
      for (const c of ev.message?.content ?? []) if (c.type === 'tool_result') r.toolResults.push({ isError: !!c.is_error, text: JSON.stringify(c.content).slice(0, 300) });
    } else if (ev.type === 'control_request' && ev.request?.subtype === 'can_use_tool') {
      r.controlRequests++;
      proc.stdin.write(JSON.stringify({ type: 'control_response', response: { subtype: 'success', request_id: ev.request_id, response: { behavior: 'allow', updatedInput: ev.request.input } } }) + '\n');
    } else if (ev.type === 'result') {
      r.results.push({ atMs: Date.now() - t0, subtype: ev.subtype, is_error: ev.is_error, result: (ev.result ?? '').slice(0, 300), session_id: ev.session_id });
      if (queue.length) proc.stdin.write(userMsg(queue.shift()));
      else proc.stdin.end();
    }
  }
  r.exitCode = await new Promise(res => proc.exitCode !== null ? res(proc.exitCode) : proc.on('exit', c => res(c)));
  r.totalMs = Date.now() - t0;
  r.stderrTail = stderr.slice(-600);
  if (r.pid) r.pidAliveAfter = fs.existsSync(`/proc/${r.pid}`);
  r.deltaCount = r.deltas.length;
  r.firstDeltaMs = r.deltas[0]; r.lastDeltaMs = r.deltas.at(-1);
  // Квантили времени прихода дельт: у живого стрима они размазаны по ходу, у буферизованного — слиплись в конце
  r.deltaQuantilesMs = [0.1, 0.25, 0.5, 0.75, 0.9].map(q => r.deltas[Math.floor(q * (r.deltas.length - 1))]);
  delete r.deltas;
  fs.writeFileSync(path.join(OUT, `${name}.json`), JSON.stringify(r, null, 1));
  console.log(JSON.stringify(r, null, 1));
  return r;
}

const LONG = 'Напиши числа от 1 до 300 словами по-русски, каждое с новой строки, без пояснений.';
const SCENARIOS = {
  sub: () => runTurn({ name: 'c1-subscription', prompts: ['Ответь ровно одним словом: ананас'] }),
  stream: () => runTurn({ name: 'c1-stream', prompts: ['Не используй инструменты. Прямо в ответе перечисли числа от 1 до 80 словами по-русски, каждое с новой строки.'] }),
  resume: sid => runTurn({ name: 'c4-resume', resume: sid, prompts: ['Какое слово ты ответил мне в прошлом сообщении? Ответь одним словом.'] }),
  deepseek: () => runTurn({ name: 'c2-deepseek', provider: 'deepseek', model: 'deepseek-v4-flash', prompts: ['Ответь ровно одним словом: ананас'] }),
  minimax: () => runTurn({ name: 'c2-minimax', provider: 'minimax', model: 'MiniMax-M2.7', prompts: ['Ответь ровно одним словом: ананас'] }),
  mcp: () => runTurn({
    name: 'c3-mcp', mcp: true,
    prompts: ['Вызови инструмент mcp__tasks__tasks_list, затем mcp__memory__memory_list. Ответь одной строкой: сколько задач и сколько записей памяти вернули инструменты.'],
  }),
  mcpBeacon: () => runTurn({
    name: 'c3-mcp-beacon', mcp: true,
    prompts: ['Вызови mcp__tasks__tasks_list с параметром scope="all" и перечисли заголовки задач дословно.'],
  }),
  // Прерывание по stdin, затем второе сообщение в ТОМ ЖЕ процессе — процесс обязан пережить interrupt
  interrupt: () => runTurn({
    name: 'c4-interrupt', prompts: [LONG, 'Ответь ровно одним словом: готово'],
    onDelta: (n, api) => { if (n === 15 && !api.stopped) { api.stopped = true; api.interrupt(); } },
  }),
  kill: () => runTurn({
    name: 'c4-kill', prompts: [LONG],
    onDelta: (n, api) => { if (n === 15 && !api.stopped) { api.stopped = true; api.kill(); } },
  }),
};

await SCENARIOS[process.argv[2]](...process.argv.slice(3));

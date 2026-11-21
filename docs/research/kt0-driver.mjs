#!/usr/bin/env node
// КТ0 (детский режим, волна 4): драйвер живого CLI для протокола песочницы.
// Дословно пишет stdout CLI в лог (--log), отвечает на control_request/can_use_tool
// согласно политике: allow — как разрешивший пользователь, deny — как отказ.
// Завершается на result-сообщении или по таймауту (SIGKILL потомка).
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import readline from 'node:readline';

const argv = process.argv.slice(2);
const dash = argv.indexOf('--');
const cli = argv.slice(dash + 1);
const opt = (name, dflt = null) => { const i = argv.indexOf(name); return i >= 0 ? argv[i + 1] : dflt; };

const logPath = opt('--log');
const prompt = opt('--prompt');
const policy = opt('--policy', 'allow'); // allow | deny
const denyTools = (opt('--deny-tools', '')).split(',').filter(Boolean);
const timeoutMs = Number(opt('--timeout', '120000'));
const cwd = opt('--cwd', process.cwd());

const logStream = fs.createWriteStream(logPath);
const write = (s) => logStream.write(s);

let finished = false;
const child = spawn(cli[0], cli.slice(1), { cwd, stdio: ['pipe', 'pipe', 'pipe'] });
child.stderr.on('data', (d) => write('[stderr] ' + d));

function finish(code) {
    if (finished) return;
    finished = true;
    clearTimeout(timer);
    try { child.kill('SIGKILL'); } catch { }
    write(`[driver] exit=${code}\n`);
    logStream.close();
    process.exit(code);
}
const timer = setTimeout(() => { write('[driver] TIMEOUT\n'); finish(2); }, timeoutMs);

const rl = readline.createInterface({ input: child.stdout });
rl.on('line', (line) => {
    write(line + '\n');
    let msg; try { msg = JSON.parse(line); } catch { return; }
    if (msg.type === 'control_request' && msg.request?.subtype === 'can_use_tool') {
        const tool = msg.request.tool_name;
        const deny = policy === 'deny' || denyTools.includes(tool);
        const response = deny
            ? { behavior: 'deny', message: 'КТ0: отказ по политике протокола' }
            : { behavior: 'allow' };
        child.stdin.write(JSON.stringify({
            type: 'control_response',
            response: { subtype: 'success', request_id: msg.request_id, response },
        }) + '\n');
    }
    if (msg.type === 'result') setTimeout(() => finish(0), 500);
});

child.on('exit', (code) => setTimeout(() => finish(code ?? 0), 200));

child.stdin.write(JSON.stringify({ type: 'user', message: { role: 'user', content: prompt } }) + '\n');

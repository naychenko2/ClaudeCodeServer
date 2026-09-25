// Спайк ADR-016, критерий 5: после ходов на «устройстве» не должно быть ни одного секрета.
// Ищет точные значения серверных секретов (и выданных токенов хода) во всех файлах каталога
// устройства и в env/argv процесса агента. Печатает только булевы флаги и пути — не значения.
import fs from 'node:fs';
import path from 'node:path';

const WORK = '/tmp/ccs-spike';
const DEVICE = path.join(WORK, 'device');
const cred = JSON.parse(fs.readFileSync(process.env.GW_OAUTH_FILE, 'utf8')).claudeAiOauth;
const prov = JSON.parse(fs.readFileSync(process.env.GW_PROVIDERS_FILE, 'utf8')).LlmProviders;
const secrets = {
  oauthAccess: cred.accessToken,
  oauthRefresh: cred.refreshToken,
  deepseekKey: prov.deepseek.ApiKey,
  minimaxKey: prov.minimax.ApiKey,
  backendJwt: fs.readFileSync(process.env.GW_BACKEND_JWT_FILE, 'utf8').trim(),
  backendJwtSecret: fs.readFileSync(path.join(WORK, 'devdata', 'jwt-secret.txt'), 'utf8').trim(),
};
// Токены хода — capability с TTL; на диске устройства им тоже не место
for (const f of fs.readdirSync(WORK).filter(f => f.startsWith('spec-')))
  secrets['turnToken:' + f] = JSON.parse(fs.readFileSync(path.join(WORK, f), 'utf8')).turnToken;

const files = [];
(function walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p);
    else if (e.isFile()) files.push(p);
  }
})(DEVICE);

const hits = [];
let bytes = 0;
for (const f of files) {
  const text = fs.readFileSync(f, 'latin1');
  bytes += text.length;
  for (const [k, v] of Object.entries(secrets)) if (v && text.includes(v)) hits.push({ file: f, secret: k });
}

// Агент устройства — долгоживущий процесс «клиента»
const agentPid = fs.readFileSync(path.join(WORK, 'agent.pid'), 'utf8').trim();
const procHits = [];
for (const f of ['environ', 'cmdline']) {
  const text = fs.readFileSync(`/proc/${agentPid}/${f}`, 'latin1');
  for (const [k, v] of Object.entries(secrets)) if (v && text.includes(v)) procHits.push({ proc: f, secret: k });
}
const agentEnvKeys = fs.readFileSync(`/proc/${agentPid}/environ`, 'latin1').split('\0').filter(Boolean).map(s => s.split('=')[0]);

console.log(JSON.stringify({
  scannedFiles: files.length, scannedBytes: bytes, secretsChecked: Object.keys(secrets).length,
  fileHits: hits, agentProcHits: procHits, agentEnvKeys,
  profileFiles: files.filter(f => f.includes('/home/')).map(f => path.relative(DEVICE, f)).slice(0, 60),
}, null, 1));

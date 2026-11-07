// QA-приёмка «Общие проекты», круг 3, добавка: приватность по симлинкам в корне проекта
// (ссылка прямо на .cc-attachments, на private-каталог, на корень ЛИЧНОГО проекта участника)
// и право участника менять глобальный лимит подмен.
//
// Стенд: feat/shared-projects, порт 5557 (юнит ccs-qa-shared). Учётки admin/qa_b.

import { test, expect } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readdirSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { mkdirSync as fsMkdir } from 'node:fs';

const BASE = process.env.PLAYWRIGHT_BASE_URL || 'http://127.0.0.1:5557';
const PROJECT_ID = '433434e7-12d2-4541-90b5-832f95e7c3ee';
const SHOTS = '/home/an/Sources/ClaudeCodeServer-shared-projects/.cc-attachments/qa-shared-accept';

const RUN = process.env.QA_ACCEPT_RUN || Date.now().toString(36).slice(-6);

const report: string[] = [];
function note(s: string) {
  report.push(s);
  writeFileSync(`${SHOTS}/report-extra.txt`, report.join('\n'));
  console.log(s);
}

const tokens: Record<string, string> = {};
const ids: Record<string, string> = {};

async function login(user: string): Promise<string> {
  if (tokens[user]) return tokens[user];
  for (let i = 0; i < 10; i++) {
    const r = await fetch(`${BASE}/api/auth/login`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: user, password: '12345' }),
    });
    if (r.status === 200) { tokens[user] = (await r.json()).token; return tokens[user]; }
    if (r.status !== 429) throw new Error(`login ${user}: ${r.status}`);
    await new Promise(r => setTimeout(r, 12000));
  }
  throw new Error(`login ${user}: лимит входа`);
}

async function api(token: string, method: string, path: string, body?: unknown) {
  const r = await fetch(`${BASE}${path}`, {
    method, headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const txt = await r.text();
  let parsed: any = txt;
  try { parsed = JSON.parse(txt); } catch { /* текст */ }
  return { status: r.status, body: parsed };
}

test.describe.configure({ mode: 'serial' });
test.beforeAll(() => { mkdirSync(SHOTS, { recursive: true }); });
test.setTimeout(120_000);

test('доп: приватность по симлинкам в корне проекта и право на лимит подмен', async () => {
  // Реальный id общего проекта берём у живого стенда (в проекте он общий на всех прогонов).
  const a = await login('admin');
  const b = await login('qa_b');
  ids.admin = (await api(a, 'GET', '/api/auth/me')).body.userId;
  ids.b = (await api(b, 'GET', '/api/auth/me')).body.userId;
  const projects = (await api(a, 'GET', '/api/projects')).body as any[];
  const shared = projects.find(p => p.rootPath && existsSync(`${p.rootPath}/.cc-attachments`));
  expect(shared, 'общий проект с .cc-attachments найден').toBeTruthy();
  const pid = shared.id as string;
  const root = shared.rootPath as string;
  note(`  общий проект: ${pid} (${root})`);

  // Личный проект участника qa_b — в него кладём его личный private-файл.
  const projectsB = (await api(b, 'GET', '/api/projects')).body as any[];
  const personal = projectsB.find(p => p.id !== pid);
  note(`  личный проект qa_b: ${personal ? `${personal.id} (${personal.rootPath})` : 'нет'}`);

  const links: string[] = [];
  const makeLink = (name: string, target: string) => {
    const p = `${root}/${name}`;
    rmSync(p, { force: true });
    symlinkSync(target, p);
    links.push(p);
  };

  try {
    // Ссылка прямо на контейнер .cc-attachments общего проекта.
    makeLink(`QA-ACC-ATT-${RUN}`, `${root}/.cc-attachments`);
    const search1 = (await api(b, 'GET', `/api/projects/${pid}/files/search?q=${encodeURIComponent('qa-acc-secret')}`)).body as any[];
    const foreign1 = search1.filter(x => x.path.includes(`private/${ids.admin}/`));
    note(`  ссылка на .cc-attachments: чужих private-имён ${foreign1.length}, всего ${search1.length}`);
    expect(foreign1, 'через ссылку прямо на .cc-attachments чужие private не находятся').toEqual([]);

    const list1 = await api(b, 'GET', `/api/projects/${pid}/files?path=${encodeURIComponent(`QA-ACC-ATT-${RUN}/private`)}`);
    const listNames = (list1.body as any[]).map(x => x.name);
    note(`  список private через ссылку на .cc-attachments: ${list1.status}, каталоги ${JSON.stringify(listNames)}`);
    expect(listNames, 'в private-контейнере по ссылке нет каталога чужого автора').not.toContain(ids.admin);
    // Каталог чужого автора внутри — 404.
    const listAdmin = await api(b, 'GET', `/api/projects/${pid}/files?path=${encodeURIComponent(`QA-ACC-ATT-${RUN}/private/${ids.admin}`)}`);
    note(`  каталог чужого автора внутри ссылки: ${listAdmin.status}`);
    expect(listAdmin.status, 'каталог чужого автора private по ссылке закрыт').toBe(404);

    // Ссылка прямо на private-каталог чужого автора.
    makeLink(`QA-ACC-PRIV-${RUN}`, `${root}/.cc-attachments/private/${ids.admin}`);
    const list2 = await api(b, 'GET', `/api/projects/${pid}/files?path=${encodeURIComponent(`QA-ACC-PRIV-${RUN}`)}`);
    note(`  список по ссылке прямо на private-каталог админа: ${list2.status}`);
    expect(list2.status, 'прямая ссылка на чужой private-каталог закрыта').toBe(404);

    const read2 = await api(b, 'GET', `/api/projects/${pid}/files/content?path=${encodeURIComponent(`QA-ACC-PRIV-${RUN}/qa-acc-secret-2cqmcu.txt`)}`);
    note(`  чтение по прямой ссылке на private-каталог: ${read2.status}`);
    expect(read2.status, 'чтение из чужого private-каталога по прямой ссылке закрыто').toBe(404);

    // Ссылка на корень ЛИЧНОГО проекта участника: его личное не должно утекать к другому.
    if (personal) {
      const pRoot = personal.rootPath as string;
      // Кладём личный файл участника в его личный проект и ссылаемся на корень из общего.
      const privDir = `${pRoot}/.cc-attachments/private/${ids.b}`;
      rmSync(privDir, { recursive: true, force: true });
      fsMkdir(privDir, { recursive: true });
      writeFileSync(`${privDir}/qa-acc-personal-${RUN}.txt`, 'личное qa_b');
      makeLink(`QA-ACC-PERS-${RUN}`, pRoot);

      // Участник видит своё личное (он автор).
      const own = (await api(b, 'GET', `/api/projects/${pid}/files/search?q=${encodeURIComponent(`qa-acc-personal-${RUN}`)}`)).body as any[];
      note(`  поиск своего личного через ссылку на свой корень: ${own.length}`);
      // Владелец проекта (админ) — чужой: не должен видеть.
      const foreign = (await api(a, 'GET', `/api/projects/${pid}/files/search?q=${encodeURIComponent(`qa-acc-personal-${RUN}`)}`)).body as any[];
      const leak = foreign.filter(x => x.path.includes(`private/${ids.b}/`));
      note(`  поиск чужого личного (админ) через ссылку на корень личного проекта: ${foreign.length}, из них чужих private ${leak.length}`);
      expect(leak, 'личный файл участника через ссылку на его корень не утекает к владельцу проекта').toEqual([]);

      const read3 = await api(a, 'GET', `/api/projects/${pid}/files/content?path=${encodeURIComponent(`QA-ACC-PERS-${RUN}/.cc-attachments/private/${ids.b}/qa-acc-personal-${RUN}.txt`)}`);
      note(`  чтение личного файла участника владельцем через ссылку на корень: ${read3.status}`);
      expect(read3.status, 'чтение личного файла участника по ссылке на корень закрыто').toBe(404);

      rmSync(privDir, { recursive: true, force: true });
    }

    // Право участника менять глобальный лимит подмен — только у admin.
    const putMember = await api(b, 'PUT', '/api/specialties/settings/fallback/global', { maxSubstitutions: 1 });
    note(`  участник меняет глобальный лимит подмен: ${putMember.status} ${JSON.stringify(putMember.body).slice(0, 160)}`);
    expect([401, 403], 'участнику глобальный лимит подмен менять нельзя').toContain(putMember.status);
  } finally {
    for (const p of links) rmSync(p, { force: true });
    const left = readdirSync(root).filter(n => n.startsWith('QA-ACC-'));
    note(`  остатки симлинков: ${JSON.stringify(left)}`);
    expect(left, 'симлинки добавки убраны').toEqual([]);
  }
});

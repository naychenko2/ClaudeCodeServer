import { test, expect, type APIRequestContext } from '@playwright/test';
import { existsSync, mkdirSync, readFileSync, writeFileSync, rmSync, readdirSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import os from 'node:os';
import path from 'node:path';
import { api, login, seed, type Api } from './helpers';

// Повторная проверка дефекта 0b4c2164 (правка/перенос страницы вики выбивали её из
// индекса Dify) после доработки 028426b49: имя документа «{заголовок} · {8 симв. хеша}».
// Стенд — дев-стенд из этого дерева с Dify:Namespace (рецепт docs/operations/qa-stand.md).
// Сценарии только через API стенда + прямые обращения к Dify + снапшоты промпта живых ходов.
// Засев — с префиксом qa-wref-; страницы вики пишутся в data-qa/wiki/<adminId>/pages напрямую.

const PREFIX = 'qa-wref-';
const base = typeof __dirname !== 'undefined' ? __dirname : process.cwd();
const ROOT = path.resolve(base, '../../..');
const DATA_DIR = path.join(ROOT, 'data-qa');

// Конфиг стенда (gitignored) — адрес Dify, ключ и namespace читаются оттуда же,
// что и у стенда: секрет не попадает в spec и в git.
const difyCfg = JSON.parse(
    readFileSync(path.join(ROOT, 'backend/ClaudeHomeServer/appsettings.Local.json'), 'utf8')
        .replace(/^\s*\/\/.*$/gm, ''),
) as { Dify: { ApiUrl: string; ApiKey: string; Namespace: string } };
const DIFY = difyCfg.Dify;

const stamp = Date.now();
const pageName = (n: string) => `${PREFIX}${n}-${stamp}`; // заголовок и имя файла страницы
const P1 = pageName('Альфа');
const P2 = pageName('Бета');
const P3 = pageName('Гамма');
const TEXT1_OLD = `Текст альфа версия первая, маркер альфа-один ${stamp}.`;
const TEXT1_NEW = `Текст альфа версия вторая, маркер альфа-два ${stamp}.`;
const TEXT2 = `Текст бета, маркер бета-контур ${stamp}.`;
const TEXT3 = `Текст гамма, маркер гамма-контур ${stamp}.`;

let request: APIRequestContext;
let dify: APIRequestContext;
let token: string;
let client: Api;
let seeded: ReturnType<typeof seed>;
let adminId: string;
let projectIdP: string;
let projectIdQ: string;
let chatP: string;
let chatQ: string;
let checkStartedAt: number; // unix-секунды: горизонт «боевые датасеты не тронуты»
let wikiDs: string;
let firstAlfaName = ''; // имя документа до правки — для п.3 («имя освобождается»)
let firstAlfaId = '';

function pageFile(projectId: string, title: string): string {
    return path.join(DATA_DIR, 'wiki', adminId, 'pages', 'Проекты', projectId, `${title}.md`);
}

function writePage(projectId: string, title: string, text: string): void {
    const f = pageFile(projectId, title);
    mkdirSync(path.dirname(f), { recursive: true });
    writeFileSync(f, `---\ntype: topic\n---\n# ${title}\n\n${text}\n`);
}

async function reindex(): Promise<number> {
    const r = await client.post<{ changed: number }>('/api/notes/reindex');
    return r.changed;
}

async function difyJson<T>(urlPath: string): Promise<T> {
    const r = await dify.get(urlPath, { headers: { Authorization: `Bearer ${DIFY.ApiKey}` } });
    expect(r.ok(), `Dify GET ${urlPath} → ${r.status()} ${r.ok() ? '' : await r.text()}`).toBeTruthy();
    const text = await r.text();
    try { return JSON.parse(text) as T; }
    catch { throw new Error(`Dify ответил не JSON: url=${r.url()} status=${r.status()} тело=${text.slice(0, 200)}`); }
}

type DifyDoc = { id: string; name: string };

async function wikiDocs(): Promise<DifyDoc[]> {
    const all: DifyDoc[] = [];
    for (let page = 1; ; page++) {
        const r = await difyJson<{ data: DifyDoc[]; has_more: boolean }>(
            `datasets/${wikiDs}/documents?page=${page}&limit=100`);
        all.push(...r.data);
        if (!r.has_more) break;
    }
    return all;
}

async function docMeta(docId: string): Promise<Record<string, string>> {
    // ?metadata=all отдаёт сам объект документа (без обёртки data) — берём и обе формы.
    const resp = await difyJson<Record<string, any>>(`datasets/${wikiDs}/documents/${docId}?metadata=all`);
    const doc = resp && typeof resp.data === 'object' && resp.data !== null ? resp.data : resp;
    console.log(`[meta] ответ documents/${docId}?metadata=all:`, JSON.stringify(doc).slice(0, 1200));
    const raw = (doc.doc_metadata ?? doc.metadata ?? null) as
        Record<string, string> | Array<{ name: string; value: string }> | null;
    if (!raw) return {};
    // Dify отдаёт либо словарь {имя: значение}, либо список [{name, value}] — нормализуем к словарю.
    if (Array.isArray(raw)) return Object.fromEntries(raw.map(f => [f.name, f.value]));
    return raw;
}

// Реестр notes-knowledge.json: userId → { WikiDatasetId, WikiDocs: { <id страницы>: {DocId, Hash} } }.
// Имя свойства — как в C#-модели (PascalCase); читаем терпимо к обоим вариантам.
type RegistryDoc = { DocId?: string; docId?: string; Hash?: string; hash?: string };
function registryWikiDocs(): Array<{ key: string; docId: string }> {
    const f = path.join(DATA_DIR, 'notes-knowledge.json');
    if (!existsSync(f)) return [];
    const store = JSON.parse(readFileSync(f, 'utf8')) as Record<string, { WikiDocs?: Record<string, RegistryDoc>; wikiDocs?: Record<string, RegistryDoc> }>;
    const entry = store[adminId];
    const docs = entry?.WikiDocs ?? entry?.wikiDocs ?? {};
    return Object.entries(docs).map(([key, v]) => ({ key, docId: v.DocId ?? v.docId ?? '' }));
}

function decodePageId(id: string): string {
    const b64 = id.replace('-', '+').replace('_', '/');
    const pad = b64.length % 4 === 2 ? '==' : b64.length % 4 === 3 ? '=' : '';
    try { return Buffer.from(b64 + pad, 'base64').toString('utf8'); } catch { return ''; }
}

// Снапшот промпта хода: data-qa/prompt-snapshots/{sessionId}/{id}.json.gz.
// Имя нового снапшота больше имени любого прежнего — «последний файл» сам по себе
// не признак нового хода: в чате с уже погоревшим контролем он будет прежним.
function lastSnapshotFile(sessionId: string): string | null {
    const dir = path.join(DATA_DIR, 'prompt-snapshots', sessionId);
    if (!existsSync(dir)) return null;
    const files = readdirSync(dir).filter(f => f.endsWith('.json.gz')).sort();
    return files.length ? files[files.length - 1] : null;
}

function readSnapshot(sessionId: string, file: string): string {
    return gunzipSync(readFileSync(path.join(DATA_DIR, 'prompt-snapshots', sessionId, file))).toString('utf8');
}

// Блок auto-recall в снапшоте: снапшот — JSON со списком секций промпта, кириллица
// в нём \uXXXX-экранирована, поэтому искать текст по сырому маркеру нельзя —
// берём секцию key='recall-notes' из разобранного JSON.
function recallBlock(snapshotText: string): string | null {
    try {
        const snap = JSON.parse(snapshotText) as { sections?: Array<{ key?: string; text?: string }> };
        const sec = (snap.sections ?? []).find(s => s.key === 'recall-notes');
        return sec?.text ?? null;
    } catch {
        const start = snapshotText.indexOf('Возможно релевантные заметки');
        if (start < 0) return null;
        const end = snapshotText.indexOf('Полный текст нужной заметки', start);
        return snapshotText.slice(start, end < 0 ? undefined : end);
    }
}

// Dify индексирует документ асинхронно (indexing_status: indexing → completed).
// Поиск чата (recall) видит только готовые сегменты — ждём завершения, иначе
// «recall пуст» означает «ещё не проиндексировано», а не «отфильтровано разделом».
async function waitDifyIndexed(docId: string, timeoutMs = 90_000): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
        const d = await difyJson<Record<string, any>>(`datasets/${wikiDs}/documents/${docId}`);
        if (d.indexing_status === 'completed') return;
        if (Date.now() > deadline) throw new Error(`документ ${docId} не проиндексирован за ${timeoutMs} мс (status=${d.indexing_status})`);
        await new Promise(r => setTimeout(r, 2000));
    }
}

// Прямая запись файла мимо API: граф заметок — кэш с TTL 2 с (NotesService.CacheTtl),
// свой путь записи (через Хранителя) инвалидирует его сам. Ждём за горизонт TTL,
// иначе reindex соберёт модель до того, как увидит новую версию страницы.
async function pauseGraph(): Promise<void> {
    await new Promise(r => setTimeout(r, 3000));
}

async function startTurn(sessionId: string, text: string): Promise<void> {
    // wait=none: дождёмся не хода, а снапшота промпта (он пишется на сборке промпта).
    const r = await request.post(`/api/sessions/${sessionId}/messages`, {
        headers: { Authorization: `Bearer ${token}` },
        data: { text, wait: 'none' },
    });
    expect(r.ok(), `POST messages в ${sessionId} → ${r.status()} ${r.ok() ? '' : await r.text()}`).toBeTruthy();
}

// Ждём снапшота НОВОГО хода: файл с именем строже prevFile (тот, что был до startTurn).
async function waitNewSnapshot(sessionId: string, tag: string, prevFile: string | null, timeoutMs = 90_000): Promise<string> {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        const f = lastSnapshotFile(sessionId);
        if (f && f !== prevFile) {
            const s = readSnapshot(sessionId, f);
            // Копия — в .cc-attachments (вне git): диагностика разбора блока, не артефакт проверки.
            mkdirSync(path.join(ROOT, '.cc-attachments', 'wref-snap'), { recursive: true });
            writeFileSync(path.join(ROOT, '.cc-attachments', 'wref-snap', `${tag}-${sessionId}.txt`), s);
            console.log(`[snap] ${tag}: файл=${f}, длина=${s.length}, секция recall-notes=${s.includes('recall-notes')}`);
            return s;
        }
        await new Promise(r => setTimeout(r, 2000));
    }
    throw new Error(`новый снапшот промпта чата ${sessionId} не появился за ${timeoutMs} мс`);
}

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ playwright, baseURL }) => {
    request = await playwright.request.newContext({ baseURL });
    // baseURL с косым концом: иначе WHATWG резолвит 'datasets/…' от '/v1' как '/datasets' (минус /v1).
    dify = await playwright.request.newContext({ baseURL: DIFY.ApiUrl.replace(/\/?$/, '/') });
    token = await login(request); // admin/12345 — Auth:DevPassword на дев-стенде
    client = api(request, token);
    // adminId — из sub токена (/auth/me отдаёт другое имя поля, а sub — точный id владельца)
    adminId = JSON.parse(Buffer.from(token.split('.')[1], 'base64').toString('utf8')).sub as string;

    // Вики-горизонтали включаются здесь же: флаг + «заведённость» (гейт IsInitialized).
    await client.put(`/api/feature-flags/llm-wiki`, { enabled: true });
    const init = await client.post<{ initialized: boolean }>('/api/wiki/initialize', {});
    expect(init.initialized, 'вики заведена').toBeTruthy();

    seeded = seed(client);
    const projectP = await seeded.project(`${PREFIX}p-${stamp}`, `${os.tmpdir()}/${PREFIX}${stamp}-p`);
    const projectQ = await seeded.project(`${PREFIX}q-${stamp}`, `${os.tmpdir()}/${PREFIX}${stamp}-q`);
    projectIdP = projectP.id;
    projectIdQ = projectQ.id;
    chatP = (await seeded.session(projectIdP, `${PREFIX}chat-p-${stamp}`)).id;
    chatQ = (await seeded.session(projectIdQ, `${PREFIX}chat-q-${stamp}`)).id;

    // Засев страниц: по одной в раздел P (Альфа — объект правок), Бета → переносится в Q,
    // Гамма — контроль раздела Q (не трогается).
    writePage(projectIdP, P1, TEXT1_OLD);
    writePage(projectIdP, P2, TEXT2);
    writePage(projectIdQ, P3, TEXT3);
    await pauseGraph();

    checkStartedAt = Math.floor(Date.now() / 1000);
    const changed = await reindex();
    console.log(`[засев] первый reindex changed=${changed}`);

    // Датасет вики — «{username}:wiki» с префиксом namespace стенда.
    const datasets = await difyJson<{ data: Array<{ id: string; name: string }> }>('datasets?page=1&limit=100');
    const w = datasets.data.find(d => d.name === `${DIFY.Namespace}:admin:wiki`);
    expect(w, `датасет ${DIFY.Namespace}:admin:wiki создан`).toBeTruthy();
    wikiDs = w!.id;
});

test.afterAll(async () => {
    // Уборка за собой: страницы сняты, индекс почистян явным reindex (ветка «stale» его делает).
    try {
        const pages: Array<[string, string]> = [[projectIdP, P1], [projectIdP, P2], [projectIdQ, P2], [projectIdQ, P3]];
        for (const [pid, title] of pages) {
            rmSync(pageFile(pid, title), { force: true });
        }
        await pauseGraph();
        if (wikiDs) await reindex();
    } catch { /* best-effort */ }
    await seeded?.cleanup();
    await request?.dispose();
    await dify?.dispose();
});

test('засев: в :wiki ровно по документу на страницу, wiki_section = раздел', async () => {
    const docs = await wikiDocs();
    console.log('[засев] GET /documents →', JSON.stringify(docs.map(d => ({ id: d.id, name: d.name }))));
    // Проекции (_schema, _index) в индекс не попадают: документ ровно 3.
    expect(docs.length, 'в датасете ровно засеянные страницы').toBe(3);
    const seededPages: Array<[string, string]> = [[P1, projectIdP], [P2, projectIdP], [P3, projectIdQ]];
    for (const [title, projectId] of seededPages) {
        const mine = docs.filter(d => d.name.startsWith(`${title} · `));
        expect(mine.length, `ровно один документ «${title} · …»`).toBe(1);
        // Хеш имени — Convert.ToHexString(SHA256): верхний регистр, префикс 8 символов.
        expect(mine[0].name, `имя вида «${title} · {8 hex}»`).toMatch(new RegExp(`^${title} · [0-9A-Fa-f]{8}$`));
        const meta = await docMeta(mine[0].id);
        console.log(`[засев] ${mine[0].name} metadata=`, JSON.stringify(meta));
        expect(meta.wiki_section, `wiki_section = projects/${projectId}`).toBe(`projects/${projectId}`);
    }
    const alfa = docs.find(d => d.name.startsWith(`${P1} · `))!;
    firstAlfaName = alfa.name;
    firstAlfaId = alfa.id;
});

test('п.1 правка текста: ровно один документ, реестр на нём; повторный reindex changed=0', async () => {
    writePage(projectIdP, P1, TEXT1_NEW);
    await pauseGraph();
    const changed = await reindex();
    console.log(`[п.1] reindex после правки changed=${changed}`);

    const docs = await wikiDocs();
    console.log('[п.1] GET /documents →', JSON.stringify(docs.map(d => ({ id: d.id, name: d.name }))));
    const mine = docs.filter(d => d.name.startsWith(`${P1} · `));
    expect(mine.length, 'после правки ровно один документ страницы').toBe(1);
    expect(mine[0].id, 'документ — новый (имя версии изменилось)').not.toBe(firstAlfaId);
    expect(mine[0].name).not.toBe(firstAlfaName);
    expect(docs.some(d => d.id === firstAlfaId), 'старый документ удалён из датасета').toBeFalsy();

    const meta = await docMeta(mine[0].id);
    console.log(`[п.1] ${mine[0].name} metadata=`, JSON.stringify(meta));
    expect(meta.wiki_section).toBe(`projects/${projectIdP}`);

    // Реестр указывает на живой документ: запись Альфы → DocId нового, старого нет.
    const reg = registryWikiDocs();
    const regAlfa = reg.find(r => decodePageId(r.key).includes(P1));
    expect(regAlfa, 'запись Альфы в реестре есть').toBeTruthy();
    expect(regAlfa!.docId, 'реестр указывает на новый документ').toBe(mine[0].id);
    expect(reg.some(r => r.docId === firstAlfaId), 'реестр не указывает на удалённый документ').toBeFalsy();
    expect(reg.map(r => r.docId).sort(), 'все записи реестра — на документы датасета')
        .toEqual(docs.map(d => d.id).sort());

    // Повторный reindex: ничего не меняется, документ на месте (провал был именно здесь).
    const changed2 = await reindex();
    expect(changed2, 'повторный reindex changed=0').toBe(0);
    const docs2 = await wikiDocs();
    expect(docs2.filter(d => d.name.startsWith(`${P1} · `)).length, 'документ на месте').toBe(1);
    expect(docs2.some(d => d.id === mine[0].id), 'тот же документ').toBeTruthy();
});

test('п.3 возврат текста: имя освобождено, ровно один документ, реестр на живом', async () => {
    // Возврат к исходному тексту → хеш (и имя) совпадает с первым документом п.1-засева.
    // Проверяем, что Dify освободил имя после удаления документа.
    writePage(projectIdP, P1, TEXT1_OLD);
    await pauseGraph();
    const changed = await reindex();
    console.log(`[п.3] reindex после возврата текста changed=${changed}`);

    const docs = await wikiDocs();
    console.log('[п.3] GET /documents →', JSON.stringify(docs.map(d => ({ id: d.id, name: d.name }))));
    const mine = docs.filter(d => d.name.startsWith(`${P1} · `));
    expect(mine.length, 'ровно один документ страницы').toBe(1);
    expect(mine[0].name, 'имя совпало с первым (имя освобождено, документ живой)').toBe(firstAlfaName);
    expect(mine[0].id, 'это новый документ, не удалённый').not.toBe(firstAlfaId);

    const reg = registryWikiDocs();
    const regAlfa = reg.find(r => decodePageId(r.key).includes(P1));
    expect(regAlfa!.docId, 'реестр указывает на живой документ').toBe(mine[0].id);
    expect(docs.some(d => d.id === regAlfa!.docId), 'DocId реестра существует в датасете').toBeTruthy();
});

test('п.2 перенос в другой раздел: один документ с новым разделом; recall старого/нового', async () => {
    test.setTimeout(360_000); // контроль + живые ходы в двух чатах + ожидание индексации Dify
    const oldBeta = (await wikiDocs()).find(d => d.name.startsWith(`${P2} · `))!;
    await waitDifyIndexed(oldBeta.id);

    // Прогрев и диагностика семантического поиска: timeout auto-recall в ходе — 2,5 с,
    // первый холодный запрос к Dify может в него не уложиться. Заодно видно, отдаёт ли
    // поиск страницу в принципе (без разделов — это следующий срез).
    const t0 = Date.now();
    const warm = await client.get<{ available: boolean; results: Array<{ title: string; score: number }> }>(
        `/api/notes/semantic?q=${encodeURIComponent('бета-контур')}`);
    console.log(`[п.2] прогрев /notes/semantic за ${Date.now() - t0} мс →`,
        JSON.stringify(warm.results.map(r => ({ title: r.title, score: r.score }))));

    // Контроль «до»: в чате раздела P страница в recall есть — иначе «нет» после переноса
    // была бы ложным negative (не сфильтровано, а ещё не проиндексировано).
    const prevP0 = lastSnapshotFile(chatP);
    await startTurn(chatP, 'Кратко: что известно из базы знаний про «бета-контур»?');
    const blockBefore = recallBlock(await waitNewSnapshot(chatP, 'before-P', prevP0));
    console.log('[п.2] recall-блок чата P до переноса:', blockBefore || '(блока нет)');
    expect(blockBefore && blockBefore.includes(P2), 'до переноса страница в recall своего раздела').toBeTruthy();

    // Перенос файла в раздел Q без правки текста.
    const from = pageFile(projectIdP, P2);
    const to = pageFile(projectIdQ, P2);
    mkdirSync(path.dirname(to), { recursive: true });
    writeFileSync(to, readFileSync(from));
    rmSync(from);
    await pauseGraph();
    const changed = await reindex();
    console.log(`[п.2] reindex после переноса changed=${changed}`);

    const docs = await wikiDocs();
    console.log('[п.2] GET /documents →', JSON.stringify(docs.map(d => ({ id: d.id, name: d.name }))));
    const mine = docs.filter(d => d.name.startsWith(`${P2} · `));
    expect(mine.length, 'после переноса ровно один документ страницы').toBe(1);
    expect(mine[0].id, 'документ новый (раздел входит в хеш)').not.toBe(oldBeta.id);
    const meta = await docMeta(mine[0].id);
    console.log(`[п.2] ${mine[0].name} metadata=`, JSON.stringify(meta));
    expect(meta.wiki_section, 'wiki_section = новый раздел').toBe(`projects/${projectIdQ}`);
    await waitDifyIndexed(mine[0].id);

    // Recall: в чате старого раздела страницы нет, в чате нового — есть.
    const prevP1 = lastSnapshotFile(chatP);
    await startTurn(chatP, 'Кратко: есть ли в базе знаний что-то про «бета-контур»? Если нет — так и скажи.');
    const snapP = recallBlock(await waitNewSnapshot(chatP, 'after-P', prevP1)) ?? '';
    console.log('[п.2] recall-блок чата P после переноса:', snapP || '(блока нет)');
    expect(snapP.includes(P2), 'в recall чата старого раздела страницы нет').toBeFalsy();

    const prevQ0 = lastSnapshotFile(chatQ);
    await startTurn(chatQ, 'Кратко: что известно из базы знаний про «бета-контур»?');
    const blockQ = recallBlock(await waitNewSnapshot(chatQ, 'after-Q', prevQ0));
    console.log('[п.2] recall-блок чата Q:', blockQ || '(блока нет)');
    expect(blockQ, 'в чате нового раздела блок recall есть').toBeTruthy();
    expect(blockQ!.includes(P2), 'в recall чата нового раздела страница есть').toBeTruthy();
});

test('боевые датасеты (без префикса namespace) за проверку не тронуты', async () => {
    const all: Array<{ id: string; name: string; updated_at: number }> = [];
    for (let page = 1; ; page++) {
        const r = await difyJson<{ data: Array<{ id: string; name: string; updated_at: number }>; has_more: boolean }>(
            `datasets?page=${page}&limit=100`);
        all.push(...r.data);
        if (!r.has_more) break;
    }
    const own = all.filter(d => d.name.startsWith(`${DIFY.Namespace}:`));
    const foreign = all.filter(d => !d.name.startsWith(`${DIFY.Namespace}:`));
    const maxForeign = Math.max(...foreign.map(d => d.updated_at));
    console.log(`[боёвые] всего датасетов ${all.length}; наших (${DIFY.Namespace}:) ${own.length}:`,
        JSON.stringify(own.map(d => ({ id: d.id, name: d.name }))));
    console.log(`[боёвые] без префикса: ${foreign.length}, максимальный updated_at = ${new Date(maxForeign * 1000).toISOString()}` +
        ` (горизонт проверки ${new Date(checkStartedAt * 1000).toISOString()})`);
    expect(own.map(d => d.name).sort(), 'наш namespace: только :notes и :wiki админа')
        .toEqual([`${DIFY.Namespace}:admin:notes`, `${DIFY.Namespace}:admin:wiki`]);
    expect(maxForeign, 'ни один боевой датасет не обновлялся во время проверки').toBeLessThan(checkStartedAt);
});

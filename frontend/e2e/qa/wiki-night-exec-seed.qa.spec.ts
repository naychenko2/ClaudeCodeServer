// QA · фича «Ночь вики на нескольких локальных моделях» (эпик e5c5f97b, задача 673155e0).
// Спек 1/4: посев — владелец (не админ), по паре проектов (два раздела вики) на каждую ночь,
// вики владельца, настройки ночи. Пометки очереди ставит спек каждой ночи (метка живёт в памяти).
// Ночь гоняет ВЛАДЕЛЕЦ: его вики и его блок «Настройки» проверяются.
//
// Чаты лежат историей файлом до первого обращения к аккумулятору (приём QA С2): стенд после
// посева перезапускается руками, иначе живой аккумулятор сессии файла не увидит.

import { test, expect, type APIRequestContext } from '@playwright/test';
import { api, login } from './helpers';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { ATT, DATA, type Seed, type SeedGroup } from './wiki-night-exec-helpers';

const stamp = Date.now();
const OWNER = `qa-nx-owner-${stamp}`;
const OWNER_PASS = 'qa-owner-pass-1234';

test.setTimeout(300_000);

// История чата: человеческие реплики и ответы агента. Тема — воображаемый проект, чтобы
// ночному разбору было о чём говорить; у чатов разных разделов текст разный.
function history(project: string, topic: string, extra: number): Array<{ kind: string; text: string; timestamp: number }> {
  const messages: Array<[string, string]> = [
    ['user_message', `Начинаем разбор проекта «${project}». Главное решение: ${topic} — оставляем, потому что оно снимает ручную рутину.`],
    ['text', `Принято. Зафиксировал решение по «${project}»: ${topic}. Что ещё стоит запомнить по этому проекту?`],
    ['user_message', 'Ещё два правила. Первое: сборки идут только в контейнере, на хосте ничего не собираем. Второе: перед мержем обязателен зелёный прогон тестов, иначе ветка не вливается.'],
    ['text', `Записал: сборка — только в контейнере; мерж только после зелёных тестов. Это правила процесса для «${project}».`],
    ['user_message', `Про данные: состояние храним в data/${project.toLowerCase()}.json, секреты — только в локальном файле настроек, в репозиторий не кладём.`],
    ['text', `Понял. Хранилище состояния — data/${project.toLowerCase()}.json; секреты — только в машинном файле, в git не попадают.`],
    ['user_message', 'И ещё: если модель недоступна, ночь не должна молча пропадать — нужен запасной исполнитель и честный итог в журнале.'],
    ['text', `Согласен: отказ модели фиксируется в итоге ночи, а работа продолжается на живом исполнителе. Это требование к надёжности «${project}».`],
    ['user_message', 'Открытый вопрос: как показывать владельцу, что ночь шла на двух моделях? Ответ: только числа по каждой, без текста знаний.'],
    ['text', 'Решение: в блоке настроек — строка исполнителей и разбивка последней ночи по движкам, без текста знаний и причин.'],
    ['user_message', 'Последнее: интерфейс на мобиле не должен давать горизонтальную прокрутку. Длинный список моделей переносится.'],
    ['text', 'Понял: на телефоне список переносится, горизонтального переполнения нет.'],
  ];
  for (let i = 0; i < extra; i++) {
    messages.push(['user_message', `Дополнение ${i + 1} по «${project}»: ${topic}, уточнение — ${i + 1}-й пункт остаётся в силе, менять не будем.`]);
    messages.push(['text', `Отметил дополнение ${i + 1} по «${project}».`]);
  }
  return messages.map(([kind, text], i) => ({ kind, text, timestamp: 1762000000000 + i * 60_000 }));
}

let request: APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
});

test.afterAll(async () => {
  await request?.dispose();
});

test('посев: владелец, восемь разделов, вики, настройки ночи', async () => {
  expect(fs.existsSync(DATA), `каталог данных стенда: ${DATA}`).toBeTruthy();
  const admin = await login(request);
  const adminApi = api(request, admin);

  const owner = await adminApi.post<{ id: string; username: string }>('/api/users', {
    username: OWNER, password: OWNER_PASS, role: 'user',
  });

  const loginRes = await request.post('/api/auth/login', { data: { username: OWNER, password: OWNER_PASS } });
  expect(loginRes.ok(), `вход владельца ${OWNER}`).toBeTruthy();
  const ownerToken = (await loginRes.json()).token as string;
  const ownerApi = api(request, ownerToken);
  const ownerId = JSON.parse(Buffer.from(ownerToken.split('.')[1], 'base64url').toString()).sub as string;

  // llm-wiki владельцу (тёмный запуск: без флага вкладки «Вики» и ночи у него нет).
  await ownerApi.put('/api/feature-flags/llm-wiki', { enabled: true });

  const root = `${os.tmpdir()}/qa-nx-${stamp}`;
  fs.mkdirSync(root, { recursive: true });

  // Группа = одна ночь: два проекта, то есть два раздела вики. Параллельность ночи живёт
  // только между разделами (сродство с разделом), поэтому разделов в каждой группе два.
  const plan: Array<{ group: string; projects: Array<{ name: string; topic: string; chats: number }> }> = [
    { group: 'n1', projects: [{ name: 'qa-nx-alpha', topic: 'единый журнал решений', chats: 3 }, { name: 'qa-nx-beta', topic: 'двухступенчатый отбор чатов', chats: 3 }] },
    { group: 'n2a', projects: [{ name: 'qa-nx-gamma', topic: 'проба готовности движка', chats: 3 }, { name: 'qa-nx-delta', topic: 'отказ по занятости слота', chats: 3 }] },
    { group: 'n2b', projects: [{ name: 'qa-nx-epsilon', topic: 'выбывание мёртвого исполнителя', chats: 3 }, { name: 'qa-nx-zeta', topic: 'повтор чата на живом движке', chats: 3 }] },
    { group: 'n3', projects: [{ name: 'qa-nx-eta', topic: 'оба движка погашены', chats: 2 }, { name: 'qa-nx-theta', topic: 'итог error без закрытия даты', chats: 2 }] },
    { group: 'n4', projects: [{ name: 'qa-nx-iota', topic: 'пустой список исполнителей', chats: 2 }, { name: 'qa-nx-kappa', topic: 'прежний маршрут места', chats: 2 }] },
  ];

  const groups: SeedGroup[] = [];
  for (const g of plan) {
    const projects: SeedGroup['projects'] = [];
    for (const d of g.projects) {
      const p = await ownerApi.post<{ id: string; name: string }>('/api/projects', {
        name: `${d.name}-${stamp}`, rootPath: `${root}/${d.name}`, createDirectory: true,
      });
      const chats: Array<{ id: string; name: string }> = [];
      for (let i = 0; i < d.chats; i++) {
        const s = await ownerApi.post<{ id: string }>(`/api/projects/${p.id}/sessions`, {
          name: `qa-nx-chat-${d.name}-${i}-${stamp}`,
        });
        const dir = path.join(DATA, 'sessions', s.id);
        fs.mkdirSync(dir, { recursive: true });
        fs.writeFileSync(path.join(dir, 'history.json'),
          JSON.stringify(history(d.name, d.topic, 2), null, 2), 'utf8');
        chats.push({ id: s.id, name: `qa-nx-chat-${d.name}-${i}` });
      }
      projects.push({ id: p.id, name: p.name, chats });
    }
    groups.push({ group: g.group, projects });
  }

  // Вики владельца и настройки ночи: лимиты выше умолчаний — в очереди бывает больше десяти чатов.
  await ownerApi.post('/api/wiki/initialize').catch(() => undefined);
  const settings = await ownerApi.put<{ nightly: { enabled: boolean } }>('/api/wiki/settings', {
    nightly: {
      enabled: true,
      maxPackagesPerSection: 20,
      maxPackagesPerNight: 20,
      maxChatsPerSection: 10,
      inputTokenBudget: 5_000_000,
    },
  });
  expect(settings.nightly.enabled, 'ночь включена').toBe(true);

  const seed: Seed = { stamp: String(stamp), ownerId, owner: OWNER, password: OWNER_PASS, userId: owner.id, groups };
  fs.mkdirSync(ATT, { recursive: true });
  fs.writeFileSync(path.join(ATT, 'seed.json'), JSON.stringify(seed, null, 2), 'utf8');

  // История ложится файлом и станет видна серверу только после перезапуска стенда (живой
  // аккумулятор сессии файла не читает) — поэтому здесь проверяется сам файл, а не GET.
  const first = groups[0].projects[0].chats[0];
  const histFile = path.join(DATA, 'sessions', first.id, 'history.json');
  expect(fs.existsSync(histFile), 'файл истории первого чата посеян').toBeTruthy();
  expect((JSON.parse(fs.readFileSync(histFile, 'utf8')) as unknown[]).length, 'реплик в посеве').toBeGreaterThan(10);
});

import { test, expect } from '@playwright/test';
import { login, api } from './helpers';

// Подготовка контура: слоты моделей администратора на локальную модель — и проверка,
// что после настройки LocalLlm живой ход и подбор маршрута идут.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const FEAT = 'qa-e2e-wf-feature';

test('П2: слоты моделей + живой ход + подбор', async ({ playwright, baseURL }) => {
  test.setTimeout(420_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  const c = api(req, token);

  const tiers = await c.put<any>('/api/me/model-tiers', { strong: 'qwen3.8-27b', medium: 'qwen3.8-27b', weak: 'qwen3.8-27b' });
  console.log('П2 слоты:', JSON.stringify(tiers));

  const tasks = await c.get<any[]>(`/api/projects/${PID}/tasks`);
  const epic = tasks.find(t => t.title === FEAT && t.role === 'feature');
  if (epic) {
    const rec = await c.post<any>(`/api/tasks/${epic.id}/workflow/recommend`);
    console.log('П2 recommend:', JSON.stringify(rec.proposal)?.slice(0, 600));
  }

  const t = await c.post<{ id: string }>('/api/tasks', { title: 'qa-e2e-wf-turn-probe2', assignee: 'claude', description: 'Ответь ровно одно слово: готово' });
  const ex = await req.post(`/api/tasks/${t.id}/execute`, { headers: { Authorization: `Bearer ${token}` } });
  console.log('П2 execute:', ex.status());
  let done = false;
  for (let i = 0; i < 50; i++) {
    await new Promise(r => setTimeout(r, 4000));
    const cur = await c.get<any>(`/api/tasks/${t.id}`);
    if (cur?.status === 'done') { done = true; console.log('П2 ход завершился за', (i + 1) * 4, 'с'); break; }
    if (i % 5 === 0) console.log('П2 статус:', cur?.status, 'after', (i + 1) * 4, 's', 'stopReason:', cur?.executorStopReason);
  }
  console.log('П2 живой ход:', done ? 'ЕСТЬ' : 'НЕТ');
  await c.del(`/api/tasks/${t.id}`);
  await req.dispose();
  expect(done, 'живой ход после настройки контура').toBeTruthy();
});

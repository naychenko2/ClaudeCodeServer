import { test, expect } from '@playwright/test';
import { login, api } from './helpers';

// Диагностика контура перед частью A: почему подбор не ответил и идёт ли живой ход.

const PID = 'ce3e224a-d0f8-4828-9519-f51d4c3dbf6a';
const FEAT = 'qa-e2e-wf-feature';

test('П1: причина отказа подбора и живой ход исполнителя', async ({ playwright, baseURL }) => {
  test.setTimeout(300_000);
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  const c = api(req, token);

  const tasks = await c.get<any[]>(`/api/projects/${PID}/tasks`);
  const epic = tasks.find(t => t.title === FEAT && t.role === 'feature');
  if (epic) {
    const wf = await c.get<any>(`/api/tasks/${epic.id}/workflow`);
    console.log('П1 предложение эпика:', JSON.stringify(wf.proposal), 'confirmed:', JSON.stringify(wf.confirmed)?.slice(0, 200));
  } else { console.log('П1: эпика нет'); }

  // Живой ход: обычная задача на Claude-исполнителе
  const t = await c.post<{ id: string }>('/api/tasks', { title: 'qa-e2e-wf-turn-probe', assignee: 'claude', description: 'Ответь одним словом: готово.' });
  const ex = await req.post(`/api/tasks/${t.id}/execute`, { headers: { Authorization: `Bearer ${token}` } });
  console.log('П1 execute:', ex.status(), (await ex.text()).slice(0, 200));
  let done = false;
  for (let i = 0; i < 40; i++) {
    await new Promise(r => setTimeout(r, 5000));
    const cur = await c.get<any>(`/api/tasks/${t.id}`);
    const st = cur?.status ?? cur?.task?.status;
    if (st === 'done') { done = true; console.log('П1 ход завершился за', (i + 1) * 5, 'с; result:', String(cur?.resultMarkdown ?? '').slice(0, 200)); break; }
    if (i % 4 === 0) console.log('П1 статус:', st, 'after', (i + 1) * 5, 's');
  }
  console.log('П1 живой ход:', done ? 'ЕСТЬ' : 'НЕТ');
  await c.del(`/api/tasks/${t.id}`);
  await req.dispose();
  expect(true).toBeTruthy();
});

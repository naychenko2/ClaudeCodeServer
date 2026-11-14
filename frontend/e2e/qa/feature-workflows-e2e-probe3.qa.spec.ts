import { test, expect } from '@playwright/test';
import { login, api } from './helpers';

// Почему страница фичы не показывает предложение модели?

const EPIC = 'f571aace-a780-4c3e-abca-05741154c8ad';

test('П3: что отдаёт страница фичы про маршрут', async ({ playwright, baseURL }) => {
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  const c = api(req, token);

  const wf = await c.get<any>(`/api/tasks/${EPIC}/workflow`);
  console.log('П3 /workflow:', JSON.stringify(wf.proposal)?.slice(0, 300));

  const page = await c.get<any>(`/api/tasks/features/${EPIC}`);
  const epic = page?.epic ?? page?.page?.epic ?? page?.run?.epic;
  console.log('П3 keys страницы:', Object.keys(page ?? {}).slice(0, 10));
  console.log('П3 epic.workflowProposal:', JSON.stringify(epic?.workflowProposal)?.slice(0, 200));
  console.log('П3 epic.routeRequired:', epic?.routeRequired, 'epic.workflow:', JSON.stringify(epic?.workflow)?.slice(0, 100));
  await req.dispose();
  expect(true).toBeTruthy();
});

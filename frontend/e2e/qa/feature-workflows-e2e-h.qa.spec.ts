import { test, expect } from '@playwright/test';
import { login, api, type Api } from './helpers';

// Часть H: наследование настроек специальности персоной.
// Проверяем: при создании ничего не копируется; правка специальности действует сразу;
// своё значение персоны не теряется.

let client: Api;
let token: string;
let request: import('@playwright/test').APIRequestContext;

test.beforeAll(async ({ playwright, baseURL }) => {
  request = await playwright.request.newContext({ baseURL });
  token = await login(request);
  client = api(request, token);
});
test.afterAll(async () => { await request?.dispose(); });

const own = (p: any) => ({
  tierStrong: p?.tierStrong, tierMedium: p?.tierMedium, tierWeak: p?.tierWeak,
  access: p?.access, tools: p?.tools, disallowedTools: p?.disallowedTools,
  promptSections: Object.keys(p?.promptSections ?? {}),
});

test('С10: наследование специальности', async () => {
  test.setTimeout(200_000);
  const personas = await client.get<any[]>('/api/personas');
  let p = personas.find(x => x.name === 'qa-e2e-наследник');
  if (!p) {
    p = await client.post<any>('/api/personas', { name: 'qa-e2e-наследник', specialty: 'analyst' });
  }
  const id = p.id ?? p.persona?.id;
  let cur = await client.get<any>(`/api/personas/${id}`);
  console.log('С10 создана, свои значения:', JSON.stringify(own(cur)));
  expect.soft(own(cur).tierStrong ?? null, 'при создании модельные слоты не скопированы').toBeFalsy();

  const specBefore = await client.get<any>('/api/specialties');
  const analyst = specBefore.find((s: any) => s.key === 'analyst');
  console.log('С10 шаблон аналитика до:', JSON.stringify(analyst?.template));

  // Правка специальности: свои права (custom + запретить WebSearch)
  const put = await client.put<any>('/api/specialties/settings/global', {
    specialties: { analyst: { access: 'custom', tools: null, disallowedTools: ['WebSearch'] } },
  });
  console.log('С10 PUT specialties/settings/global:', JSON.stringify(put)?.slice(0, 200));
  const specAfter = await client.get<any>('/api/specialties');
  console.log('С10 шаблон аналитика после:', JSON.stringify(specAfter.find((s: any) => s.key === 'analyst')?.template));
  cur = await client.get<any>(`/api/personas/${id}`);
  console.log('С10 персона после правки специальности (свои значения):', JSON.stringify(own(cur)));
  expect.soft(own(cur).disallowedTools ?? [], 'унаследованное не записалось своим значением персоны').toBeFalsy();

  // Своё значение персоны: запретить свой инструмент
  const upd = await client.put<any>(`/api/personas/${id}`, { access: 'custom', disallowedTools: ['WebFetch'] });
  console.log('С10 своё значение поставлено:', JSON.stringify(own(upd ?? await client.get<any>(`/api/personas/${id}`))));
  // Ещё раз правим специальность — своё должно остаться
  await client.put<any>('/api/specialties/settings/global', {
    specialties: { analyst: { access: 'custom', tools: null, disallowedTools: ['WebSearch', 'Read'] } },
  });
  cur = await client.get<any>(`/api/personas/${id}`);
  console.log('С10 после второй правки специальности:', JSON.stringify(own(cur)));
  expect.soft((own(cur).disallowedTools ?? []).includes('WebFetch'), 'персональная кастомизация не потерялась').toBeTruthy();

  await client.del(`/api/personas/${id}`);
});

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
  const NAME = 'qa-e2e-наследник';
  // Уборка остатков прошлого прогона: иначе персона приходит с чужим состоянием
  for (const old of (await client.get<any[]>('/api/personas')).filter(x => x.name === NAME)) await client.del(`/api/personas/${old.id}`);

  const specBefore = await client.get<any>('/api/specialties');
  const tplBefore = specBefore.find((s: any) => s.key === 'analyst')?.template;
  const setTpl = (disallowedTools: string[]) => client.put<any>('/api/specialties/settings/global', {
    specialties: { analyst: { access: 'custom', tools: null, disallowedTools } },
  });
  let id: string | undefined;
  try {
    await setTpl(['WebSearch']);
    // Наследование включается флагом inheritsAccess (его шлёт форма); без него — старая «копия при создании»
    const p = await client.post<any>('/api/personas', { name: NAME, specialty: 'analyst', inheritsAccess: true });
    id = p.id ?? p.persona?.id;
    let cur = await client.get<any>(`/api/personas/${id}`);
    console.log('С10 создана, свои значения:', JSON.stringify(own(cur)));
    expect(cur.inheritsAccess, 'персона создана наследующей права').toBe(true);
    expect(own(cur).tierStrong ?? null, 'при создании модельные слоты не скопированы').toBeNull();
    expect(own(cur).disallowedTools, 'права показаны по шаблону специальности').toEqual(['WebSearch']);

    // Правка специальности действует сразу — без правки персоны
    await setTpl(['WebSearch', 'Read']);
    cur = await client.get<any>(`/api/personas/${id}`);
    console.log('С10 после правки специальности:', JSON.stringify(own(cur)));
    expect(own(cur).disallowedTools, 'правка специальности дошла до наследующей персоны').toEqual(['WebSearch', 'Read']);

    // «Своё»: персона фиксирует собственное значение
    const upd = await client.put<any>(`/api/personas/${id}`, { inheritsAccess: false, access: 'custom', disallowedTools: ['WebFetch'] });
    console.log('С10 своё значение поставлено:', JSON.stringify(own(upd ?? await client.get<any>(`/api/personas/${id}`))));
    await setTpl(['WebSearch']);
    cur = await client.get<any>(`/api/personas/${id}`);
    console.log('С10 после второй правки специальности:', JSON.stringify(own(cur)));
    expect(cur.inheritsAccess, 'после «Своё» наследование снято').toBe(false);
    expect(own(cur).disallowedTools, 'своё значение персоны не потерялось').toEqual(['WebFetch']);
  } finally {
    if (id) await client.del(`/api/personas/${id}`);
    if (tplBefore) await setTpl(tplBefore.disallowedTools ?? []);
  }
});

import { test, expect } from '@playwright/test';
import { login, api } from './helpers';

// Проба контура перед сквозным прогоном «Процессы фичи» (задача a06c5c09):
// логин, проект «AI Home», встроенные шаблоны, специальности, персоны.

test('С0: контур стенда', async ({ playwright, baseURL }) => {
  const req = await playwright.request.newContext({ baseURL });
  const token = await login(req);
  const c = api(req, token);

  const projects = await c.get<Array<{ id: string; name: string; rootPath?: string }>>('/api/projects');
  console.log('ПРОЕКТЫ:', JSON.stringify(projects.map(p => ({ id: p.id, name: p.name, rootPath: p.rootPath }))));

  const tpls = await c.get<any[]>('/api/workflow-templates');
  console.log('ШАБЛОНЫ:', JSON.stringify(tpls.map(e => ({
    scope: e.scope, id: e.template.id, name: e.template.name, builtIn: e.template.builtInKey,
    stages: e.stageCount, version: e.template.version,
    optional: e.template.stages.filter((s: any) => s.presence === 'optional').map((s: any) => s.id ?? s.role),
  })), null, 1));

  const spec = await c.get<any[]>('/api/specialties');
  console.log('СПЕЦИАЛЬНОСТИ:', JSON.stringify(spec.map(s => ({ id: s.id, name: s.name }))));

  const personas = await c.get<any[]>('/api/personas');
  console.log('ПЕРсоны:', JSON.stringify(personas.map(p => ({ id: p.id, name: p.name, specialtyId: p.specialtyId, inherits: p.inheritsSpecialtySettings }))));

  await req.dispose();
  expect(projects.length, 'хотя бы один проект').toBeGreaterThan(0);
});

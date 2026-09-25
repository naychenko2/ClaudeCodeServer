import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  DEVICE_AGENT_SHARED, DEVICE_AGENT_UNSUPPORTED, RELAY_ROUTES, type ProjectRoute,
} from '../deviceAgentRoutes';
import { routeSupports } from '../projectCapabilities';

// Сторож задачи 5.2 (ADR-016 §5): с другого устройства у локального проекта НЕТ ни одного контрола
// записи, и источник «что скрыть» один — RELAY_ROUTES. Держится двумя частями:
// 1) в ретрансляторе нет записи, и routeSupports('relay', …) отвечает «нет» на любой не-GET;
// 2) каждая операция записи в панелях файлов и изменений стоит под гейтом can('<её маршрут>')
//    из useProjectRoutes — без своих проверок «локальный ли проект» и без рассыпанных if.
// Новая операция записи в панели без гейта по её маршруту — красный тест.

const here = dirname(fileURLToPath(import.meta.url));
const src = (rel: string) => readFileSync(resolve(here, '..', '..', rel), 'utf-8');

const ALL_ROUTES: ProjectRoute[] = [...DEVICE_AGENT_SHARED, ...DEVICE_AGENT_UNSUPPORTED, ...RELAY_ROUTES];

// Вызов записи → маршрут, по которому его гейтит панель. Карта растёт вместе с панелями:
// сторож ищет вызовы по этим именам, поэтому новая запись должна попасть сюда
const WRITE_CALLS: Record<string, ProjectRoute> = {
  'api.files.saveContent(': 'PUT files/content',
  'api.files.createFile(': 'POST files/create',
  'api.files.mkdir(': 'POST files/mkdir',
  'api.files.upload(': 'POST files/upload',
  'api.files.rename(': 'POST files/rename',
  'api.files.delete(': 'DELETE files',
  'api.files.revert(': 'POST files/revert',
  'api.files.toMarkdown(': 'POST files/document/to-markdown',
  'api.files.officeForceSave(': 'POST files/office-force-save',
  'api.files.officeDiscard(': 'POST files/office-discard',
  'api.git.stageHunk(': 'POST git/stage-hunk',
  'api.git.unstageHunk(': 'POST git/unstage-hunk',
  'gitStage(': 'POST git/stage',
  'gitUnstage(': 'POST git/unstage',
  'gitStageAll(': 'POST git/stage-all',
  'gitCommit(': 'POST git/commit',
  'gitDiscard(': 'POST git/discard',
  'gitDiscardAll(': 'POST git/discard-all',
  'gitCheckout(': 'POST git/checkout',
  'gitCreateBranch(': 'POST git/branches',
  'gitStashPush(': 'POST git/stash',
  'gitStashPop(': 'POST git/stash/{index:int}/pop',
  'gitStashDrop(': 'DELETE git/stash/{index:int}',
  'gitFetch(': 'POST git/fetch',
  'gitPull(': 'POST git/pull',
  'gitPush(': 'POST git/push',
  'gitSync(': 'POST git/sync',
  'gitRestoreFile(': 'POST git/commits/{sha}/restore-file',
  'gitRevertCommit(': 'POST git/commits/{sha}/revert',
  'gitSaveNow(': 'POST git/save-now',
  'gitSetAutoCommit(': 'PUT git/auto-commit',
  'gitInit(': 'POST git/init',
};

// Панели, которые открываются с другого устройства через ретранслятор
const RELAY_PANELS = ['components/FileExplorer.tsx', 'components/FileViewer.tsx', 'components/GitChangesRail.tsx'];

// Вызовы без префикса-объекта ищем как отдельное имя: gitStage( не должен совпасть внутри gitStageAll(
function callsIn(text: string, call: string): boolean {
  if (call.startsWith('api.')) return text.includes(call);
  return new RegExp(`(?<![\\w.])${call.replace('(', '\\(')}`).test(text);
}

describe('ретранслятор — только чтение (5.2)', () => {
  it('все маршруты ретранслятора — GET', () => {
    for (const r of RELAY_ROUTES) expect(r.startsWith('GET '), r).toBe(true);
  });

  it('с другого устройства нет ни одного маршрута записи', () => {
    const writes = ALL_ROUTES.filter(r => !r.startsWith('GET '));
    expect(writes.length).toBeGreaterThan(0);
    for (const r of writes) expect(routeSupports('relay', r), r).toBe(false);
  });

  it('на машине проекта запись агента остаётся, на сервере — вся', () => {
    expect(routeSupports('agent', 'PUT files/content')).toBe(true);
    expect(routeSupports('agent', 'POST git/commit')).toBe(true);
    expect(routeSupports('server', 'POST git/push')).toBe(true);
  });

  it('карта записей сторожа покрывает только маршруты записи', () => {
    for (const [call, route] of Object.entries(WRITE_CALLS)) {
      expect(ALL_ROUTES, `${call} → ${route}: маршрута нет в списках deviceAgentRoutes.ts`).toContain(route);
      expect(route.startsWith('GET '), `${call} → ${route}: это не запись`).toBe(false);
    }
  });

  for (const panel of RELAY_PANELS) {
    it(`${panel}: каждая операция записи стоит под гейтом can() по своему маршруту`, () => {
      const text = src(panel);
      // Единственный источник решения — хук useProjectRoutes; свой обход маршрутизации запрещён
      expect(text, 'панель берёт гейты из useProjectRoutes').toMatch(/useProjectRoutes\(project\)/);
      expect(text).not.toMatch(/projectSupportsRoute|routeSupports|agentSupports|relaySupports|projectRouteOf/);
      const missing = Object.entries(WRITE_CALLS)
        .filter(([call]) => callsIn(text, call))
        .filter(([, route]) => !text.includes(`can('${route}')`))
        .map(([call, route]) => `${call} → can('${route}')`);
      expect(missing, 'операции записи без гейта по их маршруту').toEqual([]);
    });
  }

  it('сторож не вакуумный: в панелях есть что проверять', () => {
    const found = RELAY_PANELS.flatMap(p => Object.keys(WRITE_CALLS).filter(c => callsIn(src(p), c)));
    expect(found.length).toBeGreaterThanOrEqual(15);
  });
});

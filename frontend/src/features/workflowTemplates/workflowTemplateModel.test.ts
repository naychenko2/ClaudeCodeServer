import { describe, it, expect, vi, beforeEach } from 'vitest';

// Тесты модели редактора шаблонов (feature-workflows, C1a). Случаи — зеркало
// backend/ClaudeHomeServer.Tasks.Tests/WorkflowRulesTests.cs: фронт не должен противоречить
// серверу ни в клампе, ни в белых списках, ни в несъёмных ролях.
// Часть api (разбор 409) мокает настоящий lib/offline — та же схема, что
// features/files/__tests__/apiFilesConflict.test.ts; живёт в этом файле, потому что
// список файлов C1a — четыре, тест домена стоит рядом с его моделью.

const { request } = vi.hoisted(() => ({ request: vi.fn() }));
// request() мокаем: тест задаёт поведение сервера. Остальное — настоящее.
vi.mock('../../lib/offline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../lib/offline')>();
  return { ...orig, request };
});

import {
  NON_SKIPPABLE,
  SINGLE_ROLES,
  STAGE_ROLES,
  TITLE_LIMIT,
  isNonSkippable,
  setPresence,
  stageErrors,
  titleRequired,
} from './workflowTemplateModel';
import { workflowTemplatesApi } from './api';
import type { WorkflowStage, WorkflowTemplate } from './types';

beforeEach(() => {
  request.mockReset();
});

const stage = (over: Partial<WorkflowStage> = {}): WorkflowStage => ({
  role: 'step',
  presence: 'on',
  ...over,
});

const tpl = (stages: WorkflowStage[], over: Partial<WorkflowTemplate> = {}): WorkflowTemplate => ({
  id: 't1',
  name: 'Тестовый шаблон',
  version: 1,
  stages,
  ...over,
});

describe('порог молчания (И3): вне [5..240] — ошибка, а не молчаливый кламп', () => {
  const err = (n: number | null | undefined) =>
    stageErrors(tpl([{ role: 'plan', presence: 'on', stallMinutes: n }])).filter(e => e.includes('От 5 до 240 минут'));

  it('null/undefined и границы 5, 60, 240 — без ошибки', () => {
    for (const n of [null, undefined, 5, 60, 240]) expect(err(n)).toEqual([]);
  });

  it('0, 1..4, 241, отрицательное, NaN, Infinity — «От 5 до 240 минут» с меткой этапа', () => {
    for (const n of [0, 1, 4, 241, 999, -5, NaN, Infinity]) {
      expect(err(n)).toEqual(['этап «plan»: От 5 до 240 минут']);
    }
  });
});

describe('setPresence — несъёмная роль не снимается', () => {
  // Мутация «снять проверку isNonSkippable в setPresence» красит ровно этот случай (c2).
  it.each([
    ['step', 'off'],
    ['step', 'optional'],
    ['merge', 'off'],
    ['merge', 'optional'],
    ['final', 'off'],
    ['final', 'optional'],
  ] as const)('несъёмная %s: Presence «%s» → шаблон без изменений', (role, presence) => {
    const t = tpl([stage({ role, presence: 'on' })]);
    const r = setPresence(t, role, presence);
    expect(r).toBe(t); // тот же объект: отказ, а не «тихая правка»
    expect(r.stages[0].presence).toBe('on');
  });

  it('несъёмную роль можно выставить в on (явного запрета нет)', () => {
    const t = tpl([stage({ role: 'merge', presence: 'on' })]);
    const r = setPresence(t, 'merge', 'on');
    expect(r.stages[0].presence).toBe('on');
  });

  it('съёмный этап: optional→off и on→optional применяются; исходный шаблон не тронут (несущность)', () => {
    const t = tpl([
      stage({ role: 'design', presence: 'on' }),
      stage({ role: 'qa', presence: 'optional' }),
    ]);
    const offQa = setPresence(t, 'qa', 'off');
    expect(offQa.stages[1].presence).toBe('off');
    const optDesign = setPresence(t, 'design', 'optional');
    expect(optDesign.stages[0].presence).toBe('optional');
    // оригинал — без изменений: копия, а не мутация (состояние редактора в React)
    expect(t.stages[0].presence).toBe('on');
    expect(t.stages[1].presence).toBe('optional');
  });

  it('роли нет в шаблоне — без изменений: ставить некуда', () => {
    const t = tpl([stage({ role: 'plan' })]);
    expect(setPresence(t, 'qa', 'off')).toBe(t);
  });

  it('белый список несъёмных — step/merge/final (временная final, вопрос 1 Андрею)', () => {
    expect([...NON_SKIPPABLE]).toEqual(['step', 'merge', 'final']);
    expect(isNonSkippable('final')).toBe(true);
    expect(isNonSkippable('qa')).toBe(false);
    // роли этапов — закрытый набор TaskRoles без feature/rework/consult (разрез §0.3)
    expect([...STAGE_ROLES]).toEqual(['cut', 'design', 'plan', 'step', 'review', 'qa', 'final', 'merge']);
    // одиночные роли — тот же список, что WorkflowRules.SingleRoles на бэке (разрез 2 §1.6)
    expect([...SINGLE_ROLES]).toEqual(['cut', 'final', 'merge']);
  });
});

describe('stageErrors — подсказки до отправки (зеркало WorkflowRules.Validate)', () => {
  it('пустой список и валидный шаблон — без ошибок', () => {
    expect(stageErrors(tpl([]))).toEqual([]);
    expect(stageErrors(tpl([
      stage({ role: 'cut', presence: 'optional' }),
      stage({ role: 'step', presence: 'on', prompt: 'текст', stallMinutes: 30, stallPolicy: 'nudge-alert' }),
      stage({ role: 'review', presence: 'optional', specialty: 'reviewer' }),
      stage({ role: 'merge', presence: 'on' }),
    ]))).toEqual([]);
  });

  it('неизвестная роль и дубль одиночной роли — каждая названа; метка неизвестной — по номеру', () => {
    const e = stageErrors(tpl([
      stage({ role: 'deploy' }),
      stage({ role: 'merge' }),
      stage({ role: 'merge' }),
    ]));
    expect(e.some(x => x.includes('этап #1') && x.includes('неизвестная роль этапа «deploy»'))).toBe(true);
    expect(e.some(x => x.includes('этап «merge»') && x.includes('роль «merge» одиночная'))).toBe(true);
  });

  // Зеркало бэка (WorkflowRulesTests.ПовторяемаяРольСРазнымиId_Допустима): повтор не-одиночной
  // роли легален (дублем на бэке считается id, не роль) — редактор не подсвечивает валидный
  // шаблон красным до отправки (дефект: был отказ на паре qa).
  it.each(['design', 'plan', 'step', 'review', 'qa'])('повтор не-одиночной роли %s — не ошибка', (role) => {
    expect(stageErrors(tpl([stage({ role }), stage({ role })]))).toEqual([]);
  });

  // Зеркало бэка (WorkflowRulesTests.ОдиночнаяРольДваждыСРазнымиId_Ошибка): cut/final/merge —
  // по одному на маршрут, повтор назван явно («одиночная»).
  it.each(['cut', 'final', 'merge'])('дубль одиночной роли %s — отказ с текстом про одиночность', (role) => {
    const e = stageErrors(tpl([stage({ role }), stage({ role })]));
    expect(e).toHaveLength(1);
    expect(e[0]).toContain('одиночная');
    expect(e[0]).toContain(role);
  });

  it('несъёмная роль в off/optional — отказ (тот же, что у сервера)', () => {
    const e = stageErrors(tpl([stage({ role: 'final', presence: 'off' })]));
    expect(e.some(x => x.includes('этап «final»') && x.includes('роль несъёмная'))).toBe(true);
  });

  it('неизвестное Presence — отказ со списком допустимых', () => {
    const e = stageErrors(tpl([stage({ role: 'qa', presence: 'maybe' })]));
    expect(e.some(x => x.includes('неизвестное Presence «maybe»'))).toBe(true);
  });

  it('Specialty и PersonaId взаимоисключающи; specialty=none с personaId — нет конфликта', () => {
    const both = stageErrors(tpl([stage({ role: 'step', specialty: 'tester', personaId: 'p1' })]));
    expect(both.some(x => x.includes('взаимоисключающи'))).toBe(true);
    const nonePlus = stageErrors(tpl([stage({ role: 'step', specialty: 'none', personaId: 'p1' })]));
    expect(nonePlus).toEqual([]);
  });

  it('промпт длиннее 4000 — отказ; ровно 4000 — нет', () => {
    expect(stageErrors(tpl([stage({ role: 'step', prompt: 'а'.repeat(4001) })]))
      .some(x => x.includes('длиннее 4000'))).toBe(true);
    expect(stageErrors(tpl([stage({ role: 'step', prompt: 'а'.repeat(4000) })]))).toEqual([]);
  });

  it('ячейка матрицы tier:* — отказ (зеркало ModelCellRules); id модели и preset:* — валидны', () => {
    const e = stageErrors(tpl([stage({ role: 'step', tierStrong: 'tier:strong' })]));
    expect(e.some(x => x.includes('в ячейке уровня не может быть tier:*'))).toBe(true);
    expect(stageErrors(tpl([stage({ role: 'step', tierMedium: 'model-1', tierWeak: 'preset:p1' })]))).toEqual([]);
  });

  // Зеркало бэка (WorkflowRules.cs:153): длина имени после обрезки, лимит TitleLimit=40.
  it('имя этапа длиннее 40 — отказ; ровно 40 и крающие пробелы внутри 40 — нет', () => {
    expect(TITLE_LIMIT).toBe(40);
    expect(stageErrors(tpl([stage({ role: 'step', title: 'а'.repeat(41) })]))
      .some(x => x.includes('имя этапа длиннее 40'))).toBe(true);
    expect(stageErrors(tpl([stage({ role: 'step', title: 'а'.repeat(40) })]))).toEqual([]);
    expect(stageErrors(tpl([stage({ role: 'step', title: `  ${'а'.repeat(40)}  ` })]))).toEqual([]);
    expect(stageErrors(tpl([stage({ role: 'step', title: null })]))).toEqual([]);
  });

  it('неизвестная политика простоя — отказ; пустая/известная — нет', () => {
    expect(stageErrors(tpl([stage({ role: 'step', stallPolicy: 'shout' })]))
      .some(x => x.includes('неизвестная политика простоя'))).toBe(true);
    expect(stageErrors(tpl([stage({ role: 'step', stallPolicy: 'stop-alert' })]))).toEqual([]);
    expect(stageErrors(tpl([stage({ role: 'step', stallPolicy: null })]))).toEqual([]);
  });

  it('контракт «все ошибки, не первая»: две ошибки в одном этапе — обе в списке', () => {
    const e = stageErrors(tpl([stage({ role: 'merge', presence: 'off', stallPolicy: 'shout' })]));
    expect(e.filter(x => x.includes('этап «merge»')).length).toBe(2);
  });

  it('null-шаблон, не-массив stages, null-этап — ошибка в списке, а не исключение', () => {
    expect(stageErrors(null).length).toBe(1);
    expect(stageErrors(undefined).length).toBe(1);
    const broken = { id: 't', name: '', version: 0, stages: null as unknown as WorkflowStage[] };
    expect(stageErrors(broken).some(x => x.includes('список этапов не задан'))).toBe(true);
    expect(stageErrors(tpl([null as unknown as WorkflowStage]))
      .some(x => x.includes('этап #1: пустая запись'))).toBe(true);
  });
});

// Подсказка поля «Имя этапа»: тот же критерий группы, что ValidateTitleGroups на бэке
// (WorkflowRules.cs:191-221) — роль+линия, только известные роли, группа от двух этапов.
describe('titleRequired — имя обязательно этапу группы (Role, Lane)', () => {
  const qaPair = (): WorkflowStage[] => [
    { role: 'qa', presence: 'optional', id: 'qa-plan', after: ['design'] },
    { role: 'qa', presence: 'optional', id: 'qa', after: ['review', 'qa-plan'] },
  ];

  it('два этапа одной роли (qa-plan и qa): имя требуется каждому', () => {
    expect(titleRequired(qaPair(), 0)).toBe(true);
    expect(titleRequired(qaPair(), 1)).toBe(true);
  });

  it('одиночный этап своей роли — имя не требуется', () => {
    expect(titleRequired([stage({ role: 'qa', id: 'qa' })], 0)).toBe(false);
  });

  it('этапы step разных линий — не группа: имена не требуются; одной линии — требуются', () => {
    const diffLanes = [
      stage({ role: 'step', id: 'step-back', lane: 'back' }),
      stage({ role: 'step', id: 'step-front', lane: 'front' }),
    ];
    expect(titleRequired(diffLanes, 0)).toBe(false);
    expect(titleRequired(diffLanes, 1)).toBe(false);
    const sameLane = [
      stage({ role: 'step', id: 'step-a', lane: 'back' }),
      stage({ role: 'step', id: 'step-b', lane: 'back' }),
    ];
    expect(titleRequired(sameLane, 0)).toBe(true);
    expect(titleRequired(sameLane, 1)).toBe(true);
  });

  it('роль вне белого списка группы не даёт; индекса нет — false', () => {
    const withUnknown = [stage({ role: 'deploy' }), stage({ role: 'deploy' })];
    expect(titleRequired(withUnknown, 0)).toBe(false);
    expect(titleRequired([stage({ role: 'qa' })], 5)).toBe(false);
  });

  it('роль и линия триммятся перед сравнением (тот же Normalize, что на бэке)', () => {
    const padded = [
      stage({ role: ' qa ', id: 'qa-a' }),
      stage({ role: 'qa', id: 'qa-b' }),
    ];
    expect(titleRequired(padded, 0)).toBe(true);
  });
});

describe('api — отказ различим по коду (Контракт конкурентности C1a)', () => {
  type ReqOpts = { method?: string; body?: string; signal?: AbortSignal };

  it('409 на saveTemplate (правка устаревшей версии) — ошибка request() как есть, status 409', async () => {
    request.mockRejectedValue(Object.assign(new Error('Версия шаблона устарела.'), {
      status: 409,
      body: { error: 'Версия шаблона устарела.' },
    }));

    const err = await workflowTemplatesApi
      .saveTemplate(tpl([stage({ role: 'step' })], { version: 3 }))
      .catch(e => e);
    expect(err.status).toBe(409);
    expect((err as Error).message).toBe('Версия шаблона устарела.');
  });

  it('правка (version>0) — PUT по id с expectedVersion; создание (version 0) — POST', async () => {
    request.mockResolvedValue(tpl([stage({ role: 'step' })], { version: 4 }));

    await workflowTemplatesApi.saveTemplate(tpl([stage({ role: 'step' })], { version: 3 }));
    const [urlPut, optsPut] = request.mock.calls[0] as [string, ReqOpts];
    expect(urlPut).toBe('/workflow-templates/t1');
    expect(optsPut.method).toBe('PUT');
    expect(JSON.parse(optsPut.body!).expectedVersion).toBe(3);

    request.mockResolvedValue(tpl([stage({ role: 'step' })], { version: 1 }));
    await workflowTemplatesApi.saveTemplate(tpl([stage({ role: 'step' })], { version: 0 }));
    const [urlPost, optsPost] = request.mock.calls[1] as [string, ReqOpts];
    expect(urlPost).toBe('/workflow-templates');
    expect(optsPost.method).toBe('POST');
    // expectedVersion: 0 — «существующего id быть не должно»: он даст 409, а не тихую перезапись
    expect(JSON.parse(optsPost.body!).expectedVersion).toBe(0);
  });

  it('область — наличием projectId: list/delete несут его параметром, reset — нет', async () => {
    request.mockResolvedValue([]);
    await workflowTemplatesApi.listTemplates('p1');
    expect(request.mock.calls[0][0]).toBe('/workflow-templates?projectId=p1');
    await workflowTemplatesApi.listTemplates();
    expect(request.mock.calls[1][0]).toBe('/workflow-templates');

    request.mockResolvedValue(undefined);
    await workflowTemplatesApi.deleteTemplate('t1', 'p1');
    expect(request.mock.calls[2][0]).toBe('/workflow-templates/t1?projectId=p1');
    expect((request.mock.calls[2][1] as ReqOpts).method).toBe('DELETE');

    request.mockResolvedValue(tpl([]));
    await workflowTemplatesApi.resetBuiltIn('builtin-full-cycle');
    expect(request.mock.calls[3][0]).toBe('/workflow-templates/builtin-full-cycle/reset');
    expect((request.mock.calls[3][1] as ReqOpts).method).toBe('POST');
  });

  it('обёртки B4b: confirm — POST с телом человека; recommend — POST с сигналом отмены', async () => {
    request.mockResolvedValue(undefined);
    await workflowTemplatesApi.confirmWorkflow('e1', {
      templateId: 't1',
      scope: 'project',
      skipped: ['design'],
    });
    const [urlC, optsC] = request.mock.calls[0] as [string, ReqOpts];
    expect(urlC).toBe('/tasks/e1/workflow/confirm');
    expect(optsC.method).toBe('POST');
    expect(JSON.parse(optsC.body!)).toEqual({ templateId: 't1', scope: 'project', skipped: ['design'] });

    const ctrl = new AbortController();
    // Ответ — в реальной форме ручки: обёртка { proposal } (TaskWorkflowController.Recommend).
    request.mockResolvedValue({ proposal: { templateId: 't1', scope: 'global', skipped: [] } });
    await workflowTemplatesApi.recommendWorkflow('e1', ctrl.signal);
    const [urlR, optsR] = request.mock.calls[1] as [string, ReqOpts];
    expect(urlR).toBe('/tasks/e1/workflow/recommend');
    expect(optsR.method).toBe('POST');
    expect(optsR.signal).toBe(ctrl.signal);
  });

  it('recommend разворачивает обёртку на границе: {proposal: P} → P, {proposal: null} → null', async () => {
    // Дефект приёмки QA (сценарий 4б): фронт клал обёртку в состояние как предложение —
    // блок показывал «Предложение модели» без шаблона. Разворот — только здесь, в api.
    const proposed = { templateId: 't1', scope: 'global' as const, skipped: ['design'], rationale: 'по описанию' };
    request.mockResolvedValue({ proposal: proposed });
    expect(await workflowTemplatesApi.recommendWorkflow('e1')).toBe(proposed);
    request.mockResolvedValue({ proposal: null });
    expect(await workflowTemplatesApi.recommendWorkflow('e1')).toBeNull();
  });

  it('recommend: state=none никогда не становится предложением, настоящее проходит', async () => {
    request.mockResolvedValue({ proposal: { state: 'none', reason: 'invalid' } });
    expect(await workflowTemplatesApi.recommendWorkflow('e1')).toBeNull();
    const proposed = { state: 'proposed' as const, templateId: 'x', scope: 'global' as const, skipped: [] };
    request.mockResolvedValue({ proposal: proposed });
    expect(await workflowTemplatesApi.recommendWorkflow('e1')).toBe(proposed);
  });

  it('confirm разворачивает обёртку { confirmed }: записанный маршрут наружу, без тела — null (Ф-2.15)', async () => {
    // Тот же приём, что у recommend: вызывающий (блок подтверждения) должен видеть версию,
    // которую записал сервер, сразу после подтверждения, а не ждать штатный task_changed.
    const written = { templateId: 't1', scope: 'project', skipped: ['design'], confirmedVersion: 3, confirmedByUser: true };
    request.mockResolvedValue({ confirmed: written });
    expect(await workflowTemplatesApi.confirmWorkflow('e1', { templateId: 't1', scope: 'project', skipped: ['design'] })).toBe(written);
    request.mockResolvedValue(undefined);
    expect(await workflowTemplatesApi.confirmWorkflow('e1', { templateId: 't1', scope: 'project', skipped: [] })).toBeNull();
    request.mockResolvedValue({ confirmed: null });
    expect(await workflowTemplatesApi.confirmWorkflow('e1', { templateId: 't1', scope: 'project', skipped: [] })).toBeNull();
  });
});

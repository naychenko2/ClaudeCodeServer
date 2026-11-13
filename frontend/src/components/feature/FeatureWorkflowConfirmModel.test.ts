// Тесты модели блока подтверждения маршрута (C2): confirmBody по id и только по съёмным
// (дубль роли 'qa' с id 'qa-plan' — снятие по id, не по роли; порядок skipped — порядок
// маршрута, вход намеренно обратный); «шаблон изменился» — только при смене набора id
// (правка промпта/версии тот же состав — не перемена; этапы 'off' вне сравнения);
// начальное снятие
// из предложения — только съёмное в показанном шаблоне; выбор стартового шаблона по id+scope;
// гонка recommend: устаревший ответ после смены featureId не применяется; M4: состояние
// блока маршрута (none/proposal/confirmed/легаси-null) и строка о подтверждённом маршруте
// (имя шаблона · версия · снятые этапы по title шаблона · кто выбрал). Окружение node:
// модель чистая — ни DOM, ни запросов.

import { describe, it, expect } from 'vitest';
import {
  applyRecommend, confirmBodyOf, confirmedLineText, initialRemovedIds, isRemovableStage,
  pickInitialTemplate, routeBlockState, templateChanged, templateUpdateText, type ConfirmedRoute,
} from './FeatureWorkflowConfirmModel';
import type { WorkflowStage, WorkflowTemplate, WorkflowTemplateEntry } from '../../features/workflowTemplates/types';

const st = (over: Partial<WorkflowStage> & { role: string }): WorkflowStage => ({ presence: 'on', ...over });

const tpl = (stages: WorkflowStage[], over: Partial<WorkflowTemplate> = {}): WorkflowTemplate => ({
  id: 'full-cycle', name: 'Полный цикл', version: 3, stages, ...over,
});

// Маршрут, подтверждённый по шаблону ниже: снят optional 'qa', остальные этапы в маршруте.
const CONFIRMED: ConfirmedRoute = {
  templateId: 'full-cycle',
  scope: 'global',
  stages: [
    { id: 'cut', role: 'cut', after: [] },
    { id: 'plan', role: 'plan', after: ['cut'] },
    { id: 'step', role: 'step', lane: 'back', after: ['plan'] },
    { id: 'review', role: 'review', after: ['step'] },
    { id: 'final', role: 'final', after: ['review'] },
    { id: 'merge', role: 'merge', after: ['final'] },
  ],
  skipped: ['qa'],
  confirmedVersion: 3,
};

const baseTemplate = (over: Partial<WorkflowTemplate> = {}): WorkflowTemplate => tpl([
  st({ role: 'cut', presence: 'optional' }),
  st({ role: 'plan' }),
  st({ role: 'step', lane: 'back' }),
  st({ role: 'review', presence: 'optional' }),
  st({ role: 'qa', presence: 'optional' }),
  st({ role: 'final' }),
  st({ role: 'merge' }),
], over);

describe('isRemovableStage / снятие этапов', () => {
  it('съёмный — только optional и несъёмная роль вне списка', () => {
    expect(isRemovableStage(st({ role: 'qa', presence: 'optional' }))).toBe(true);
    expect(isRemovableStage(st({ role: 'qa' }))).toBe(false);
    expect(isRemovableStage(st({ role: 'step', presence: 'optional' }))).toBe(false);
  });

  it('confirmBody: несъёмные этапы в skipped не попадают, порядок — как в маршруте', () => {
    // Входной порядок — ОБРАТНЫЙ порядку маршрута (qa раньше review): иначе тест
    // не отличает «порядок маршрута» от «порядка вызывающего» и вырождается.
    const body = confirmBodyOf(baseTemplate(), 'global', ['qa', 'review', 'merge', 'final', 'step', 'нет-такого']);
    expect(body).toEqual({
      templateId: 'full-cycle',
      scope: 'global',
      // 'step'/'final'/'merge' несъёмные, 'нет-такого' нет в шаблоне; 'cut' не снят человеком.
      skipped: ['review', 'qa'],
    });
  });

  it('дубль роли qa: снятие qa-plan даёт skipped=[qa-plan], а не [qa]', () => {
    const t = baseTemplate({
      stages: [
        st({ role: 'plan' }),
        st({ role: 'step' }),
        st({ id: 'qa-plan', role: 'qa', presence: 'optional' }),
        st({ role: 'qa', presence: 'optional' }), // id после нормализации — 'qa'
        st({ role: 'final' }),
      ],
    });
    expect(confirmBodyOf(t, 'global', ['qa-plan']).skipped).toEqual(['qa-plan']);
    expect(confirmBodyOf(t, 'global', ['qa']).skipped).toEqual(['qa']);
  });

  it('начальное снятие: только съёмные id предложения в ПОКАЗАННОМ шаблоне', () => {
    const proposal = { templateId: 'other', scope: 'global' as const, skipped: ['qa', 'step', 'чужой'] };
    // В показанном шаблоне 'qa' — optional (снимается), 'step' — нет, 'чужого' нет вовсе.
    expect(initialRemovedIds(baseTemplate(), proposal)).toEqual(['qa']);
    // Ничего не предложено — пустое снятие.
    expect(initialRemovedIds(baseTemplate(), null)).toEqual([]);
  });
});

describe('templateChanged / строка «шаблон изменился»', () => {
  it('та же версия и тот же состав — не перемена; правка промпта при том же составе — тоже нет', () => {
    expect(templateChanged(CONFIRMED, baseTemplate())).toBe(false);
    const promptOnly = baseTemplate({
      version: 4,
      stages: baseTemplate().stages.map(s => (s.role === 'step' ? { ...s, prompt: 'новые правила' } : s)),
    });
    expect(templateChanged(CONFIRMED, promptOnly)).toBe(false);
  });

  it('новый этап в составе — перемена; удалённый этап — перемена', () => {
    const withExtra = baseTemplate({
      stages: [...baseTemplate().stages, st({ id: 'audit', role: 'review', presence: 'optional' })],
    });
    expect(templateChanged(CONFIRMED, withExtra)).toBe(true);
    expect(templateChanged(CONFIRMED, baseTemplate({ stages: baseTemplate().stages.filter(s => s.role !== 'review') }))).toBe(true);
  });

  it('сравнение по id: переименованный id этапа — перемена, даже при том же наборе ролей', () => {
    const renamed = baseTemplate({
      stages: baseTemplate().stages.map(s => (s.role === 'review' ? { ...s, id: 'review-2' } : s)),
    });
    expect(templateChanged(CONFIRMED, renamed)).toBe(true);
  });

  it("этап 'off' вне сравнения: живой шаблон с выключенным этапом — не перемена", () => {
    // 'audit' с presence='off' не был в маршруте и не записан в skipped — считать его
    // в состав нельзя: при живом шаблоне с выключенным этапом это была бы ложная «перемена».
    const withOff = baseTemplate({
      stages: [...baseTemplate().stages, st({ id: 'audit', role: 'review', presence: 'off' })],
    });
    expect(templateChanged(CONFIRMED, withOff)).toBe(false);
    // Тот же этап, включённый ('on'), вошёл бы в состав — это перемена.
    const onAgain = baseTemplate({
      stages: [...baseTemplate().stages, st({ id: 'audit', role: 'review' })],
    });
    expect(templateChanged(CONFIRMED, onAgain)).toBe(true);
  });

  it('снятый при подтверждении optional-этап живёт в шаблоне optional — состава это не меняет', () => {
    // CONFIRMED снял 'qa'; шаблон как был ('qa' optional) → тот же состав маршрута.
    expect(templateChanged(CONFIRMED, baseTemplate())).toBe(false);
    // 'qa' стал несъёмным (on): при прежнем снятии он вошёл бы в маршрут — состав другой.
    const qaForced = baseTemplate({
      stages: baseTemplate().stages.map(s => (s.role === 'qa' ? { ...s, presence: 'on' } : s)),
    });
    expect(templateChanged(CONFIRMED, qaForced)).toBe(true);
  });

  it('строка — только при новой версии И смены состава; vN → vM по фактическим числам', () => {
    const changedV4 = baseTemplate({
      version: 4,
      stages: [...baseTemplate().stages, st({ id: 'audit', role: 'review', presence: 'optional' })],
    });
    expect(templateUpdateText(CONFIRMED, changedV4)).toBe('Шаблон изменился: v3 → v4, обновить маршрут?');
    // Версия новая, состав тот же — строки нет.
    expect(templateUpdateText(CONFIRMED, baseTemplate({ version: 4 }))).toBeNull();
    // Версия не продвинута (confirmedVersion >= version шаблона) — строки нет, даже
    // когда состав другой: сравнивать «vN → vM» не с чем.
    expect(templateUpdateText({ ...CONFIRMED, confirmedVersion: 4 }, changedV4)).toBeNull();
    // Та же версия — строки нет.
    expect(templateUpdateText(CONFIRMED, baseTemplate())).toBeNull();
    // Нечего сравнивать.
    expect(templateUpdateText(null, baseTemplate())).toBeNull();
    expect(templateUpdateText(CONFIRMED, null)).toBeNull();
  });
});

describe('pickInitialTemplate', () => {
  const globalEntry: WorkflowTemplateEntry = { scope: 'global', projectId: null, template: baseTemplate() };
  const projectEntry: WorkflowTemplateEntry = {
    scope: 'project', projectId: 'p1',
    template: baseTemplate({ id: 'full-cycle', name: 'Полный цикл (проект)' }),
  };
  const entries = [globalEntry, projectEntry];

  it('предложение — по id И scope: тот же id в двух областях не спутать', () => {
    const hit = pickInitialTemplate(entries, { templateId: 'full-cycle', scope: 'project', skipped: [] }, null);
    expect(hit?.scope).toBe('project');
  });

  it('предложения нет — берётся шаблон подтверждённого маршрута', () => {
    const hit = pickInitialTemplate(entries, null, CONFIRMED);
    expect(hit?.scope).toBe('global');
  });

  it('ни предложения, ни подтверждения — null: шаблон выбирает человек', () => {
    expect(pickInitialTemplate(entries, null, null)).toBeNull();
  });

  it('предложенного шаблона нет в каталоге — фолбэк на подтверждённый; нет и его — null', () => {
    const gone = { templateId: 'removed', scope: 'global' as const, skipped: [] };
    expect(pickInitialTemplate(entries, gone, CONFIRMED)?.template.id).toBe('full-cycle');
    expect(pickInitialTemplate(entries, gone, null)).toBeNull();
  });
});

describe('гонка «Подобрать маршрут»', () => {
  const proposal = { templateId: 'full-cycle', scope: 'global' as const, skipped: ['qa'], rationale: 'по описанию' };

  it('ответ своей фичи применяется', () => {
    expect(applyRecommend('f1', 'f1', proposal)).toBe(proposal);
  });

  it('устаревший ответ recommend после смены featureId не применяется', () => {
    expect(applyRecommend('f1', 'f2', proposal)).toBeNull();
  });
});

describe('routeBlockState / состояние блока маршрута (M4)', () => {
  const proposal = { templateId: 'full-cycle', scope: 'global' as const, skipped: [] };

  it('режим маршрута без предложения — none', () => {
    expect(routeBlockState(true, null, null)).toBe('none');
    expect(routeBlockState(true, undefined, undefined)).toBe('none');
  });

  it('есть предложение — proposal', () => {
    expect(routeBlockState(true, proposal, null)).toBe('proposal');
    // routeRequired false при живом предложении — предложение важнее.
    expect(routeBlockState(false, proposal, null)).toBe('proposal');
  });

  it('подтверждённый маршрут — confirmed при любых остальных аргументах', () => {
    expect(routeBlockState(true, null, CONFIRMED)).toBe('confirmed');
    expect(routeBlockState(false, proposal, CONFIRMED)).toBe('confirmed');
    expect(routeBlockState(undefined, undefined, CONFIRMED)).toBe('confirmed');
  });

  it('не режим маршрута (легаси: routeRequired нет/false) — null, блока нет', () => {
    expect(routeBlockState(undefined, null, null)).toBeNull();
    expect(routeBlockState(null, null, null)).toBeNull();
    expect(routeBlockState(false, null, null)).toBeNull();
  });
});

describe('confirmedLineText / строка о подтверждённом маршруте (M4)', () => {
  // Тот же шаблон, но у снятого optional-'qa' есть title — по нему строка называет этап.
  const titledTemplate = baseTemplate({
    stages: baseTemplate().stages.map(s => (s.role === 'qa' ? { ...s, title: 'Проверка' } : s)),
  });

  it('autoConfirmed со снятым этапом: имя · vN · снято: <title> · выбрано моделью', () => {
    expect(confirmedLineText({ ...CONFIRMED, autoConfirmed: true }, titledTemplate))
      .toBe('Полный цикл · v3 · снято: Проверка · выбрано моделью — можно сменить');
  });

  it('autoConfirmed не задан, снятых нет: подтверждён вами', () => {
    const noSkipped = { ...CONFIRMED, skipped: [] };
    expect(confirmedLineText(noSkipped, baseTemplate()))
      .toBe('Полный цикл · v3 · подтверждён вами');
    // autoConfirmed=false — то же, что не задан.
    expect(confirmedLineText({ ...noSkipped, autoConfirmed: false }, baseTemplate()))
      .toBe('Полный цикл · v3 · подтверждён вами');
  });

  it('шаблона нет: имя = templateId, имя снятого этапа = его id', () => {
    expect(confirmedLineText(CONFIRMED, null))
      .toBe('full-cycle · v3 · снято: qa · подтверждён вами');
  });

  it('этап без title — его id; несколько снятых — через «, » в порядке skipped', () => {
    const twoSkipped = { ...CONFIRMED, skipped: ['review', 'qa'] };
    expect(confirmedLineText(twoSkipped, baseTemplate()))
      .toBe('Полный цикл · v3 · снято: review, qa · подтверждён вами');
    // review с title, qa без — смешанная подпись.
    const reviewTitled = baseTemplate({
      stages: baseTemplate().stages.map(s => (s.role === 'review' ? { ...s, title: 'Ревью плана' } : s)),
    });
    expect(confirmedLineText({ ...twoSkipped, autoConfirmed: true }, reviewTitled))
      .toBe('Полный цикл · v3 · снято: Ревью плана, qa · выбрано моделью — можно сменить');
  });
});

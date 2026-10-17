// Карточка фичи (режим «Исполнение фичи»): статический рендер по фикстуре.
// Проверяем три вещи из постановки:
//   1. рендер по фикстуре — имя, «Сейчас», шаги, конвейер;
//   2. отображение progress — «N из M шагов»;
//   3. реакция на verdict — смена вердикта витка меняет подпись «↺».
//
// Рендер — renderToStaticMarkup (паттерн соседних тестов, @testing-library нет).
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { projectFeatureRun } from '../../../lib/featureRuns';
import type { FeatureTask } from '../../../types/featureRun';
import fixture from '../../../__fixtures__/feature-runs.json';
import { FeatureRunView } from './FeatureRunView';

const tasks = fixture as FeatureTask[];
const epic = tasks.find(t => t.role === 'feature')!;

// Копия фикстуры с переписанным вердиктом ревью cc-r1: исходный accepted_with_remarks
// (считается возвратом) → вариант 'accepted' (без возврата) и 'returned' (явный возврат).
function withVerdict(verdict: 'accepted' | 'returned'): FeatureTask[] {
  return tasks.map(t => (t.id === 'cc-r1'
    ? { ...t, verification: { verdict } }
    : t));
}

// createElement, а не JSX: тест .ts (в конфиге vitest include только *.test.ts).
const render = (list: FeatureTask[]) => {
  const run = projectFeatureRun(epic, list)!;
  return { run, html: renderToStaticMarkup(createElement(FeatureRunView, { run })) };
};

describe('FeatureRunView', () => {
  it('рендерит карточку по фикстуре: имя, «Сейчас», шаги и конвейер', () => {
    const { html } = render(tasks);
    expect(html).toContain('Пульт куратора');            // имя фичи
    expect(html).toContain('feat/curator-console');       // ветка
    expect(html).toContain('Сейчас');                     // идущие задачи
    expect(html).toContain('Конвейер');                   // секция конвейера
    expect(html).toContain('Производство');              // стадия конвейера
    // Все четыре шага фичи на месте
    for (const id of ['Ш1', 'Ш2', 'Ш3', 'Ш4']) expect(html).toContain(id);
  });

  it('показывает progress «N из M шагов»', () => {
    const { run, html } = render(tasks);
    // 4 шага, 2 завершены (Ш1, Ш2) → «2 из 4 шага» (4 → форма «шага»)
    expect(run.progress).toEqual({ done: 2, total: 4 });
    expect(html).toContain('2 из 4 шага');
  });

  it('реагирует на verdict: возврат витка даёт «↺1», принятие — без него', () => {
    const accepted = render(withVerdict('accepted'));
    const returned = render(withVerdict('returned'));
    // Принято — возврата нет; возвращено — виток считается возвратом.
    expect(accepted.html).not.toContain('↺1');
    expect(returned.html).toContain('↺1');
    // Итоговые разметки различаются — UI действительно зависит от вердикта.
    expect(returned.html).not.toEqual(accepted.html);
  });
});

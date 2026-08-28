// Схема командного плана: статик-рендер react-dom/server по паттерну
// TeamPlanView.test.ts (vitest гоняет окружение node — эффекты и клики в статике
// не воспроизводятся). Поэтому компонент принимает initialView/initialExpandedId:
// тесты стартуют сразу с нужного экрана/раскрытой строки, не эмулируя клики.
//
// Стор персон в тестах пуст (ensurePersonasLoaded — эффект, в статике не выполняется),
// поэтому чип исполнителя всегда показывает серое «не назначен» — попутно это и
// проверка фолбэка на неразрешённом id.
import { describe, it, expect } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { TeamPlan, TeamPlanSubtask } from '../../types';
import { TeamPlanScheme } from './TeamPlanScheme';

function subtask(over: Partial<TeamPlanSubtask> = {}): TeamPlanSubtask {
  return {
    id: 'st1', title: 'Эндпоинт экспорта', goal: '', executorPersonaId: 'p1',
    executorRationale: 'Бэкенд — его зона', files: ['Controllers/SpendController.cs'],
    wave: 1, doneCriteria: '',
    ...over,
  };
}

function plan(over: Partial<TeamPlan> = {}): TeamPlan {
  return {
    id: 'plan1', request: 'экспорт трат', summary: 'Экспорт трат в XLSX',
    createdAt: '2026-08-02T10:00:00Z', waveCount: 2, executorCount: 2,
    subtasks: [subtask()],
    version: 1, assumptions: [], changes: [],
    ...over,
  };
}

const render = (p: TeamPlan, props: {
  rootPath?: string | null;
  initialView?: 'essence' | 'map';
  initialExpandedId?: string | null;
} = {}) => renderToStaticMarkup(createElement(TeamPlanScheme, { plan: p, ...props }));

describe('TeamPlanScheme — «Суть»', () => {
  it('сводка планировщика и ряд чисел по фактической структуре', () => {
    const html = render(plan({ subtasks: [
      subtask({ id: 'st1', wave: 1, executorPersonaId: 'p1', files: ['src/a.ts'] }),
      subtask({ id: 'st2', title: 'Фронт таблицы', wave: 1, executorPersonaId: 'p1', files: ['src/b.ts'] }),
      subtask({ id: 'st3', title: 'Тесты', wave: 2, executorPersonaId: null, files: ['src/c.ts'] }),
    ] }));
    expect(html).toContain('командная реализация');
    expect(html).toContain('Экспорт трат в XLSX');
    // 3 под-задачи · 2 волны · 1 исполнитель · 1 без исполнителя · 3 файла в работе
    expect(html).toContain('под-задачи');
    expect(html).toContain('волны');
    expect(html).toContain('исполнитель');
    expect(html).toContain('под-задача без исполнителя');
    expect(html).toContain('файла в работе');
  });

  it('пустая сводка — показываем исходный запрос вместо пустого заголовка', () => {
    const html = render(plan({ summary: '' }));
    expect(html).toContain('экспорт трат');
  });

  it('intent непустой — блок «Замысел» с текстом', () => {
    const html = render(plan({ intent: 'Идём через SafeJoin, авторизацию не трогаем.' }));
    expect(html).toContain('Замысел');
    expect(html).toContain('Идём через SafeJoin');
  });

  it('без intent — блока «Замысел» нет', () => {
    expect(render(plan())).not.toContain('Замысел');
  });

  it('сигналы внимания: общий файл (с путями-владельцами) и под-задача без исполнителя', () => {
    const html = render(plan({ subtasks: [
      subtask({ id: 'st1', files: ['src/shared.ts', 'src/a.ts'] }),
      subtask({ id: 'st2', title: 'Фронт', files: ['src/shared.ts'] }),
      subtask({ id: 'st3', title: 'Тесты', executorPersonaId: null }),
    ] }));
    expect(html).toContain('Требует вашего внимания');
    expect(html).toContain('общий файл');
    expect(html).toContain('src/shared.ts');
    // Заголовки конфликтующих под-задач — рядом с файлом
    expect(html).toContain('Эндпоинт экспорта · Фронт');
    expect(html).toContain('нет исполнителя');
    expect(html).toContain('Тесты');
  });

  it('без сигналов — блока внимания нет вовсе', () => {
    const html = render(plan({ subtasks: [
      subtask({ id: 'st1', files: ['src/a.ts'] }),
      subtask({ id: 'st2', title: 'Фронт', files: ['src/b.ts'] }),
    ] }));
    expect(html).not.toContain('Требует вашего внимания');
  });

  it('пустой план (0 под-задач) — ряда чисел нет, компонент не падает', () => {
    const html = render(plan({ subtasks: [] }));
    expect(html).not.toContain('под-задач');
    expect(html).not.toContain('Требует вашего внимания');
  });
});

describe('TeamPlanScheme — «Карта»', () => {
  it('волны по возрастанию с подписями, под-задачи строками', () => {
    const html = render(plan({ subtasks: [
      subtask({ id: 'st1', wave: 1 }),
      subtask({ id: 'st2', title: 'Фронт таблицы', wave: 3 }),
    ] }), { initialView: 'map' });
    expect(html).toContain('Волна 1');
    expect(html).toContain('параллельно');
    expect(html).toContain('Волна 3');
    expect(html).toContain('после 2-й');
    expect(html).toContain('Эндпоинт экспорта');
    expect(html).toContain('Фронт таблицы');
  });

  it('чип исполнителя: персона не разрешается — серое «не назначен»', () => {
    const html = render(plan({ subtasks: [
      subtask({ id: 'st1', executorPersonaId: 'p1' }),
      subtask({ id: 'st2', title: 'Тесты', executorPersonaId: null }),
    ] }), { initialView: 'map' });
    expect(html).toContain('не назначен');
  });

  it('файлы: первые два + «+N», полный список в title, пути — relPath от корня', () => {
    const html = render(plan({ subtasks: [
      subtask({ id: 'st1', files: ['C:/proj/src/a.ts', 'C:/proj/src/b.ts', 'C:/proj/src/c.ts'] }),
    ] }), { rootPath: 'C:/proj', initialView: 'map' });
    expect(html).toContain('src/a.ts · src/b.ts +1');
    expect(html).toContain('title="src/a.ts · src/b.ts · src/c.ts"');
  });

  it('markdown в названии под-задачи чистится до плоского текста', () => {
    const html = render(plan({ subtasks: [
      subtask({ id: 'st1', title: '**Эндпоинт** экспорта' }),
    ] }), { initialView: 'map' });
    expect(html).toContain('Эндпоинт экспорта');
    expect(html).not.toContain('**');
  });

  it('детали скрыты, пока строка не раскрыта', () => {
    const p = plan({ subtasks: [
      subtask({ id: 'st1', goal: 'Эндпоинт по ТЗ', doneCriteria: 'Отвечает 200', executorRationale: 'Бэкенд — его зона' }),
    ] });
    const html = render(p, { initialView: 'map' });
    expect(html).not.toContain('Готово, когда');
    expect(html).not.toContain('Отвечает 200');
    expect(html).not.toContain('Бэкенд — его зона');
  });

  it('раскрытая строка показывает цель, критерии готовности и обоснование исполнителя', () => {
    const p = plan({ subtasks: [
      subtask({ id: 'st1', goal: 'Эндпоинт по ТЗ', doneCriteria: 'Отвечает 200', executorRationale: 'Бэкенд — его зона' }),
      subtask({ id: 'st2', title: 'Фронт', goal: 'Таблица трат', executorRationale: 'Фронт — его зона' }),
    ] });
    const html = render(p, { initialView: 'map', initialExpandedId: 'st1' });
    expect(html).toContain('Цель');
    expect(html).toContain('Эндпоинт по ТЗ');
    expect(html).toContain('Готово, когда');
    expect(html).toContain('Отвечает 200');
    expect(html).toContain('Почему этот исполнитель');
    expect(html).toContain('Бэкенд — его зона');
    // Раскрыта одна строка — детали соседней не подтекают
    expect(html).not.toContain('Таблица трат');
  });

  it('пустой план — «Карта» не падает и честно пуста', () => {
    const html = render(plan({ subtasks: [] }), { initialView: 'map' });
    expect(html).toContain('Под-задач нет');
  });
});

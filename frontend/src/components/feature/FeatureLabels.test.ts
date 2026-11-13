// Общая точка подписей блоков «процесса» фичи (хвост R-D1e): словарь статуса задачи и
// слово для пустого значения. Чистый модуль — в node, без DOM.
//
// Покрытие: подписи todo/inProgress/done; вне словаря и пустое (undefined/null) — «не начата»
// (бэк опередил фронт — тот же фолбэк, что в stageStatusLabel); NO_DATA — «нет данных».
//
// Мутации (в отчёте): (1) TASK_STATUS['done'] → «закрыта» → краснеет «done — готово»;
// (2) фолбэк taskStatusLabel → '' → краснеет «вне словаря — не начата»;
// (3) NO_DATA → '—' → краснеет «NO_DATA».

import { describe, it, expect } from 'vitest';
import { taskStatusLabel, NO_DATA } from './FeatureLabels';

describe('FeatureLabels — статус задачи', () => {
  it('словарь: todo / inProgress / done', () => {
    expect(taskStatusLabel('todo')).toBe('не начата');
    expect(taskStatusLabel('inProgress')).toBe('в работе');
    expect(taskStatusLabel('done')).toBe('готово');
  });

  it('пустое значение — «не начата»', () => {
    expect(taskStatusLabel(undefined)).toBe('не начата');
    expect(taskStatusLabel(null)).toBe('не начата');
  });

  it('вне словаря (бэк опередил фронт) — «не начата»', () => {
    expect(taskStatusLabel('archived')).toBe('не начата');
  });
});

describe('FeatureLabels — пустое значение', () => {
  it('NO_DATA — «нет данных»', () => {
    expect(NO_DATA).toBe('нет данных');
  });
});

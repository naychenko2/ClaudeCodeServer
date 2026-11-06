// Сторож полноты фронтового реестра ключей подсистем (SUBSYSTEMS): он обязан
// покрывать ровно те ключи, которые бэк может назвать в ActiveKeys() — поле
// `subsystems` ответа /api/auth/me (см. SubsystemStateStore.ActiveKeys()).
//
// Зачем он нужен. Инвариант фичи: «ни один пункт меню, рельсы, вида контекста и
// маршрута не ведёт в выключенную подсистему». Дальнейшие шаги (меню аватара,
// рельса, строка контекста, маршруты) смогут гейтить подсистему только если у её
// ключа есть type-safe пара в SUBSYSTEMS; пропущенный ключ = гейт сырой строкой
// или отсутствие гейта вовсе.
//
// Формулировка проверки — множество, а не порядок: ActiveKeys() сортируется по
// ключу, но порядок здесь не контракт; контракт — состав и отсутствие дублей.

import { describe, it, expect } from 'vitest';
import { SUBSYSTEMS } from '../subsystems';

// Перечень сверен с кодом бэка: AddSubsystems(Program.cs) — статические подсистемы —
// плюс секция DynamicModules в appsettings.json (динамические модули). Соответствие
// держит бэковый кейс SubsystemDisableMatrixTests.Фронтовый_реестр_содержит_каждый_ключ_стора:
// он читает этот файл и проверяет, что каждый ключ SubsystemStateStore есть в SUBSYSTEMS.
// Заглушка
// __stub не включена: это PoC-модуль, не подсистема пользователя. Ключи
// контрибьюторов хода (prompt-sections, persona-layer, …) и провайдеров
// (fal, glif, youtube, …) не включены: это не IAppSubsystem и в ActiveKeys
// бэка не попадают.
const BACKEND_KEYS = [
  'architecture', 'audioeditor', 'backgrounds', 'changelog', 'code-graph', 'deploy',
  'dossiers', 'git', 'imageeditor', 'images', 'knowledge', 'llm', 'memory', 'notes',
  'plugins', 'project-icons', 'project-services', 'reader', 'skills', 'spend',
  'tasks', 'test-runs', 'tts', 'video', 'videoeditor', 'yandex',
];

describe('SUBSYSTEMS — полнота реестра ключей', () => {
  it('значения SUBSYSTEMS равны множеству ключей бэка', () => {
    expect(new Set(Object.values(SUBSYSTEMS))).toEqual(new Set(BACKEND_KEYS));
  });

  it('в реестре нет дублей и лишнего: длина совпадает с фикстурой', () => {
    const values = Object.values(SUBSYSTEMS);
    expect(BACKEND_KEYS.length).toBe(new Set(BACKEND_KEYS).size);
    expect(values.length).toBe(BACKEND_KEYS.length);
    expect(new Set(values).size).toBe(values.length);
  });

  // Ключевые имена в camelCase, значения — ровно бэковые ключи (с дефисами).
  // Проверяем пару «имя свойства → значение» на образцах с дефисом: это то место,
  // где имя и значение расходятся, и опечатка здесь молча ломает только один ключ.
  it('имена свойств в camelCase, значения — ключи бэка как есть', () => {
    expect(SUBSYSTEMS.codeGraph).toBe('code-graph');
    expect(SUBSYSTEMS.testRuns).toBe('test-runs');
    expect(SUBSYSTEMS.projectIcons).toBe('project-icons');
    expect(SUBSYSTEMS.projectServices).toBe('project-services');
  });
});

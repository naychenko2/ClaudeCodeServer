// Обратная совместимость детектора механик после переименования «Командная реализация»
// → «Командный спринт»: отправленные ходы `/team-implement {JSON}` лежат в истории
// дословно, и карточка в ленте (плюс превью в списке чатов) рисуется по префиксу.
// Если id механики поменять, старые ходы превратятся в сырой JSON.
import { describe, it, expect } from 'vitest';
import {
  detectTeamMechanic, describeTeamTurn, teamTurnPreview, teamMechanic, buildTeamTurnText,
  DEFAULT_TEAM_SETTINGS, TEAM_MECHANICS,
} from './teamMechanics';

const OLD_TURN = '/team-implement {"task":"Добавить экспорт в CSV","worktree":false,"verify":true,"executors":["denis","kira"]}';

describe('детектор командных ходов', () => {
  it('старый ход /team-implement по-прежнему опознаётся — как «Командный спринт»', () => {
    expect(detectTeamMechanic(OLD_TURN)).toBe('implement');
    expect(teamMechanic('implement').name).toBe('Командный спринт');
  });

  it('старый ход раскладывается на тему и чипы, а не остаётся JSON', () => {
    const info = describeTeamTurn(OLD_TURN);
    expect(info).not.toBeNull();
    expect(info!.topic).toBe('Добавить экспорт в CSV');
    expect(info!.chips).toEqual(['с проверкой', '@denis', '@kira']);
  });

  it('превью в списке чатов показывает новое имя механики', () => {
    expect(teamTurnPreview(OLD_TURN)).toBe('Командный спринт: Добавить экспорт в CSV');
  });

  it('спринт по-прежнему собирается на префиксе /team-implement', () => {
    const text = buildTeamTurnText('implement', 'Задача', DEFAULT_TEAM_SETTINGS);
    expect(text.startsWith('/team-implement ')).toBe(true);
    expect(detectTeamMechanic(text)).toBe('implement');
  });

  it('режим чата обвязки не несёт — тема уходит обычным сообщением', () => {
    const text = buildTeamTurnText('implementMode', 'Реализовать фичу', DEFAULT_TEAM_SETTINGS);
    expect(text).toBe('Реализовать фичу');
    expect(detectTeamMechanic(text)).toBeNull();
    expect(teamMechanic('implementMode').name).toBe('Командная реализация');
  });
});

// Словарь коротких имён (TeamMechanicBadge, TeamImplementBadge, teamPill в Composer) —
// единая точка сокращений для чипов, чтобы длинные названия не переполняли пилюлю
// на узкой ширине (320px). Полное имя остаётся в title/aria-label этих же чипов.
describe('короткие имена механик для чипов', () => {
  it('у каждой механики есть непустое короткое имя короче полного или равное ему', () => {
    for (const m of TEAM_MECHANICS) {
      expect(m.shortName.length).toBeGreaterThan(0);
      expect(m.shortName.length).toBeLessThanOrEqual(m.name.length);
    }
  });

  it('самые длинные полные названия сокращены заметно', () => {
    expect(teamMechanic('implementMode').shortName).toBe('КР');
    expect(teamMechanic('implement').shortName).toBe('КС');
    expect(teamMechanic('panel').shortName).toBe('Панель');
    expect(teamMechanic('review').shortName).toBe('Консилиум');
    expect(teamMechanic('redteam').shortName).toBe('Ред-тим');
  });
});

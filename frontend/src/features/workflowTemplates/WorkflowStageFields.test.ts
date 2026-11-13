// F24: поля настройки этапа — имя со счётчиком, присутствие, промпт со счётчиком и ошибкой,
// модели «как у специальности»/«своя», радио-карточки молчания, порог с ошибкой, MCP «не выдан персоне».
// Статический рендер (паттерн WorkflowStageSettings.test).
import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { WorkflowStage } from './types';
import type { Persona } from '../personas/types';

vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1280, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }),
}));
vi.mock('../../lib/breakpoints', async orig => ({ ...(await orig<object>()), useIsMobile: () => false }));
vi.mock('../../lib/models', async orig => ({ ...(await orig<object>()), useModels: () => [] }));
vi.mock('../../lib/presets', async orig => ({ ...(await orig<object>()), usePresets: () => [] }));
vi.mock('../../lib/modelTiers', async orig => ({
  ...(await orig<object>()), useTierModels: () => ({ strong: 'Opus 5.5', medium: 'Sonnet 5.5', weak: 'Flash' }),
}));

const { StageFields, mcpNotGranted } = await import('./WorkflowStageFields');
const { templateSnapshot } = await import('./workflowEditorLogic');

const noop = () => {};
const base: WorkflowStage = { role: 'review', presence: 'on', after: ['cut'] };
const cut: WorkflowStage = { role: 'cut', presence: 'on' };
const html = (stage: WorkflowStage, extra: Record<string, unknown> = {}, stages: WorkflowStage[] = [cut, stage]) =>
  renderToStaticMarkup(createElement(StageFields, {
    stage, stages, index: stages.indexOf(stage), readOnly: false, specialties: [], personas: [], mcpChoices: [],
    onChange: noop, onRenameId: noop, onRemove: noop, ...extra,
  })).replace(/<!-- -->/g, '');

describe('поля этапа', () => {
  it('имя: «— необязательно» и счётчик «0 / 40»; при общей паре роль+линия — «— обязательно», «14 / 40»', () => {
    const h = html(base);
    expect(h).toContain('— необязательно');
    expect(h).toContain('0 / 40');
    const a: WorkflowStage = { role: 'qa', presence: 'on', id: 'qa-plan', title: 'Проверка плана' };
    const b: WorkflowStage = { role: 'qa', presence: 'optional', id: 'qa', after: ['qa-plan'] };
    const h2 = html(a, {}, [a, b]);
    expect(h2).toContain('— обязательно');
    expect(h2).toContain('14 / 40');
  });

  it('присутствие: Всегда / По решению / Нет', () => {
    const h = html({ ...base, role: 'qa' });
    for (const t of ['Всегда', 'По решению', 'Нет']) expect(h).toContain(t);
  });

  it('промпт: счётчик «212 / 4000»; сверх лимита — ошибка «…на 120» и красный счётчик', () => {
    expect(html({ ...base, prompt: 'x'.repeat(212) })).toContain('212 / 4000');
    const over = html({ ...base, prompt: 'x'.repeat(4120) });
    expect(over).toContain('4120 / 4000');
    expect(over).toContain('на 120');
  });

  it('модели: пустая — «как у специальности · Opus 5.5», своя — «Medium · своя»', () => {
    const h = html({ ...base, tierMedium: 'sonnet' });
    expect(h).toContain('как у специальности · Opus 5.5');
    expect(h).toContain('Medium · своя');
    expect(h).not.toContain('Strong · своя');
  });

  it('уровень по умолчанию: Strong/Medium/Weak/как у спец.', () => {
    const h = html(base);
    for (const t of ['Strong', 'Medium', 'Weak', 'как у спец.']) expect(h).toContain(t);
  });

  it('порог: подсказка «Пусто — как в проекте: 20 мин»; вне диапазона — «От 5 до 240 минут»', () => {
    expect(html(base)).toContain('Пусто — как в проекте: 20 мин');
    expect(html(base)).not.toContain('От 5 до 240 минут');
    expect(html({ ...base, stallMinutes: 300 })).toContain('От 5 до 240 минут');
  });

  it('молчание: три радио-карточки, выбранная — aria-checked=true', () => {
    const h = html({ ...base, stallPolicy: 'stop-alert' });
    for (const t of ['Подтолкнуть и сообщить', 'Только сообщить', 'Остановить и сообщить']) expect(h).toContain(t);
    expect((h.match(/role="radio"/g) ?? []).length).toBe(3);
    expect((h.match(/aria-checked="true"/g) ?? []).length).toBe(1);
  });

  it('запреты: чипы с «×» и поле «+ Инструмент»', () => {
    const h = html({ ...base, disallowedTools: ['Edit', 'Write'] });
    expect(h).toContain('Edit');
    expect(h).toContain('Убрать «Write»');
    expect(h).toContain('+ Инструмент');
  });

  it('MCP: сетка видна при суженном списке; ключ с выключенной привязкой персоны — «не выдан персоне»', () => {
    const persona = {
      id: 'p1', name: 'Глеб', bindings: [{ id: 'b', type: 'tool', target: 'mcp:higgsfield', mode: 'off' }],
    } as unknown as Persona;
    const stage = { ...base, personaId: 'p1', mcpAllow: ['tasks'] };
    const h = html(stage, { personas: [persona], mcpChoices: ['tasks', 'higgsfield'] });
    expect(h).toContain('не выдан персоне');
    expect(h).toContain('data-mcp-key="tasks"');
    expect(html(base, { mcpChoices: ['tasks'] })).not.toContain('data-mcp-key');
    expect(mcpNotGranted(persona, 'higgsfield')).toBe(true);
    expect(mcpNotGranted(persona, 'tasks')).toBe(false);
  });

  it('снимок dirty учитывает defaultTier', () => {
    const t = (tier: 'strong' | null) => templateSnapshot({
      id: 'g', name: 'n', version: 1, stages: [{ ...cut, defaultTier: tier }],
    });
    expect(t('strong')).not.toBe(t(null));
  });
});

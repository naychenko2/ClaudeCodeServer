import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

// Сторож паритета (ADR-023 §Д6): подпись и запуск кнопки поля и низа панели идут из одной точки.
// Скан исходников: вторая подпись у панели или поля, мимо useActionRun, краснит тест.
const SRC = join(__dirname, '..', '..');
const read = (p: string) => readFileSync(join(SRC, p), 'utf8');

describe('одна точка запуска', () => {
  it('поле ввода и хост панели берут ActionRun из useActionRun, заглушки в продукте нет', () => {
    for (const f of ['components/Composer.tsx', 'components/generation/ContextPanelHost.tsx']) {
      const s = read(f);
      expect(s, f).toMatch(/useActionRun\(/);
      expect(s, f).not.toMatch(/actionRunStub|stubActionRun/);
    }
  });

  it('низ панели рисует run.label и не строит подпись сам', () => {
    const s = read('components/generation/ContextPanel.tsx');
    expect(s).toMatch(/<RunLabel parts=\{run\.labelParts\}/);
    expect(s).not.toMatch(/[`'"]✦/);
    expect(s).toMatch(/run\.run\(run\.text\)/);
  });

  it('подпись кнопки строит только runLabel', () => {
    const files = ['components/Composer.tsx', 'components/generation/ContextPanelHost.tsx', 'components/generation/ContextPanel.tsx', 'lib/chatContext/actionMode.ts', 'lib/chatContext/useActionRun.ts'];
    for (const f of files) expect(read(f), f).not.toMatch(/[`'"]✦/);
  });
});

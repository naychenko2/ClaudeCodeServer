// Сторож регрессии «мерж теряет врезки» (edd570c9, 27–28.08): мерж ветки,
// трогавшей общие файлы (ChatPanel/Composer/SessionList), может взять файл
// «целиком со стороны ветки» и молча унести master-правки. git log -S такое
// удаление не видит — оно случается внутри мержа, а не отдельным коммитом.
// Фиксы acc06ba7 и 40728e78 вернули потерянное; этот тест — записная книжка
// врезок: по каждому якорю видно, что врезка жива в исходнике, и пропажа
// любого якоря = красный прогон с именем потерянной врезки ещё до прода.

import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const SRC = join(process.cwd(), 'src');

// Записная книжка врезок: {файл, якорь, смысл}. Якоря — имена импортов,
// компонентов, пропсов и вызовов (не случайные подстроки): они переживают
// точечный рефакторинг вокруг врезки.
const INLAYS: ReadonlyArray<{ file: string; anchor: string; meaning: string }> = [
  // Карточка хода выкатки в ленте — потеряна мержем edd570c9, вернулась acc06ba7
  { file: 'components/ChatPanel.tsx', anchor: 'import { DeployProgressCard }',
    meaning: 'карточка выкатки: импорт DeployProgressCard' },
  { file: 'components/ChatPanel.tsx', anchor: 'isDeployStart(',
    meaning: 'карточка выкатки: опознание вызова deploy_start (isDeployStart)' },
  { file: 'components/ChatPanel.tsx', anchor: '<DeployProgressCard',
    meaning: 'карточка выкатки: рендер <DeployProgressCard> в ленте' },
  // Быстрые фразы и настройка видимости кнопок губы — потеряны edd570c9, вернулись 40728e78
  { file: 'components/Composer.tsx', anchor: '<QuickPhrasesMenu',
    meaning: 'быстрые фразы: меню выбора фразы у микрофона' },
  { file: 'components/Composer.tsx', anchor: 'QuickPhrasesDialog',
    meaning: 'быстрые фразы: модалка правки набора' },
  { file: 'components/Composer.tsx', anchor: "useActionVisibility('composer'",
    meaning: 'губа: настройка видимости кнопок ряда (useActionVisibility)' },
  // Кнопка создания десктопного чата — потеряна edd570c9, вернулась acc06ba7
  { file: 'components/SessionList.tsx', anchor: 'onNewDesktop=',
    meaning: 'кнопка создания десктопного чата в списке (onNewDesktop)' },
];

describe('сторож врезок: master-правки, которые мерж не должен уносить', () => {
  it.each(INLAYS)('«$meaning» жива в $file', ({ file, anchor, meaning }) => {
    const src = readFileSync(join(SRC, file), 'utf8');
    expect(
      src.includes(anchor),
      `Потеряна врезка «${meaning}»: якорь «${anchor}» не найден в src/${file}. `
      + 'Похоже, мерж снова взял файл целиком со стороны ветки (как edd570c9). '
      + 'Верни врезку; если сущность переименована рефакторингом — обнови якорь '
      + 'в записной книжке этого теста осознанно.',
    ).toBe(true);
  });
});

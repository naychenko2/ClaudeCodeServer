// Генератор списка имён значков (ADR-009 §5.2). Единственный источник — `iconNames`
// установленного пакета lucide-react; из него пишутся ОБА артефакта, поэтому набор
// бэкенда и набор фронта совпадают по построению:
//   1. backend/ClaudeHomeServer.ProjectIcons/Services/ProjectIcons/lucide-icon-names.g.txt
//   2. frontend/src/lib/lucideIconNames.g.ts — статический массив строк. Нужен, чтобы
//      стартовый граф не тянул карту loader'ов (~2000 import()) ради проверки имени.
// Запуск: из frontend/ — `node scripts/gen-glyph-names.mjs`. После апгрейда lucide-react
// прогнать заново и закоммитить оба файла (vitest-сторож проверит равенство с пакетом).
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { iconNames } from 'lucide-react/dynamic.mjs';

const names = [...iconNames].sort();

const txtUrl = new URL('../../backend/ClaudeHomeServer.ProjectIcons/Services/ProjectIcons/lucide-icon-names.g.txt', import.meta.url);
const txtHeader = '# сгенерировано из iconNames установленного lucide-react; руками не править; генератор — frontend/scripts/gen-glyph-names.mjs (node scripts/gen-glyph-names.mjs из frontend/)';
writeFileSync(txtUrl, `${txtHeader}\n${names.join('\n')}\n`);

const tsUrl = new URL('../src/lib/lucideIconNames.g.ts', import.meta.url);
const tsHeader = [
  '// Сгенерировано из iconNames установленного lucide-react — руками не править.',
  '// Генератор: frontend/scripts/gen-glyph-names.mjs (node scripts/gen-glyph-names.mjs из frontend/).',
  '// Состав совпадает с бэкенд-списком lucide-icon-names.g.txt (ADR-009 §5.2).',
].join('\n');
writeFileSync(tsUrl, `${tsHeader}\nexport const LUCIDE_ICON_NAMES_LIST: readonly string[] = [\n${names.map(n => `  '${n}',`).join('\n')}\n];\n`);

console.log(`[gen-glyph-names] имён: ${names.length}; txt: ${fileURLToPath(txtUrl)}; ts: ${fileURLToPath(tsUrl)}`);

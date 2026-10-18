// Сторож стартового графа: считает <link rel="modulepreload"> в dist/index.html и падает
// выше порога. Контекст: статический `import { icons } from 'lucide-react'` и статическая
// карта loader'ов lucide-react/dynamic раздували index.html до 1756 тегов (весь lucide,
// ~6 МБ) — браузер до первого кадра качал и разбирал 1700+ модулей.
// Запуск: из frontend/ после `vite build` — `node scripts/check-preload-budget.mjs [путь/к/index.html]`.
// Порог 350: сейчас ~270 (из них ~250 — иконки, которые стартовые компоненты импортируют
// статически по имени; слить их в один чанк нельзя — module-federation отключает
// output.manualChunks). Рост выше порога — почти наверняка снова протёк большой набор.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const BUDGET = 350;
const file = process.argv[2] ?? fileURLToPath(new URL('../dist/index.html', import.meta.url));
const html = readFileSync(file, 'utf8');
const count = (html.match(/<link\s+rel="modulepreload"/g) ?? []).length;
console.log(`[preload-budget] modulepreload в ${file}: ${count} (порог ${BUDGET})`);
if (count > BUDGET) {
  console.error(`[preload-budget] ПРЕВЫШЕНИЕ: ${count} > ${BUDGET}. Проверьте статические импорты lucide-react (icons, lucide-react/dynamic).`);
  process.exit(1);
}

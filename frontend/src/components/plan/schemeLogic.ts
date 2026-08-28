// Чистые функции разворота плана схемой, вынесенные для юнит-тестов.
// Эти же функции зовутся из PlanScheme.tsx и НЕ дублируются там: компонент
// только оркеструет state и UI, а логика резолва заголовков и нарезки
// markdown живёт здесь, чтобы её можно было проверить без браузерного окружения.

import type { Heading } from '../../hooks/useHeadings';

// Пара (anchor, anchorIndex) → живой Heading из DOM. Возвращает null, если блок
// ссылается на раздел, которого нет в отрендеренном плане.
export function resolveHeading(
  anchor: string,
  anchorIndex: number,
  headings: Heading[],
): Heading | null {
  return headings.find(h => h.text === anchor && h.occurrence === anchorIndex) ?? null;
}

// Заголовок с таким текстом встречается в плане больше одного раза? Подпись (N-й)
// показывается у ВСЕХ одноимённых, не только у второго и далее — то же правило,
// что в buildPlanFeedback.
export function headingHasDuplicates(text: string, headings: Heading[]): boolean {
  let count = 0;
  for (const h of headings) { if (h.text === text) { count++; if (count > 1) return true; } }
  return false;
}

// Нарезка исходного markdown раздела по заголовку из DOM. Чистая функция —
// собирает markdown между двумя соседними заголовками того же или более высокого
// уровня.
//
// Резолв по паре (heading.text, heading.occurrence): при двух одноимённых
// разделах «Дизайн» идём во второе вхождение, а не в первое. Это клейка блока
// карты с конкретным разделом ради которой на бэке заводили AnchorIndex.
//
// Нормализует ОБЕ стороны сравнения: текст в DOM может быть без inline-разметки
// (textContent даёт «Шаг — код»), а в исходнике остаётся «Шаг — `код`». Без
// нормализации заголовки с inline-кодом/жирным/ссылками не находились бы.
export function sliceSection(planText: string, heading: Heading, _all?: Heading[]): string {
  const lines = planText.split('\n');
  const prefix = '#'.repeat(heading.level) + ' ';
  const target = stripInlineMarkdown(heading.text);
  // Счётчик встреч нужного заголовка в исходнике — по нему находим именно то
  // вхождение, на которое указывает occurrence. Первое совпадение текста
  // больше не подходит (см. эпиграф).
  let startLine = -1;
  let seen = 0;
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (!line.startsWith(prefix)) continue;
    const lineText = stripInlineMarkdown(line.slice(prefix.length).trim());
    if (lineText !== target) continue;
    if (seen === heading.occurrence) { startLine = i; break; }
    seen++;
  }
  if (startLine < 0) return '';
  const sameOrHigher = new RegExp(`^#{1,${heading.level}}\\s+`);
  let endLine = lines.length;
  for (let i = startLine + 1; i < lines.length; i++) {
    const line = lines[i];
    if (sameOrHigher.test(line)) { endLine = i; break; }
  }
  return lines.slice(startLine, endLine).join('\n').trim();
}

// Снятие inline-разметки из заголовка для сопоставления с DOM-текстом.
// Не претендует на полноценный markdown-парсер: режет то, что встречается в
// заголовках планов на практике.
export function stripInlineMarkdown(s: string): string {
  let out = s;
  out = out.replace(/`([^`]+)`/g, '$1');
  out = out.replace(/\*\*([^*]+)\*\*/g, '$1');
  out = out.replace(/__([^_]+)__/g, '$1');
  out = out.replace(/\*([^*\n]+)\*/g, '$1');
  out = out.replace(/_([^_\n]+)_/g, '$1');
  out = out.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '$1');
  out = out.replace(/<(https?:\/\/[^>]+)>/g, '$1');
  return out;
}
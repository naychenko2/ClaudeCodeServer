// Текст сообщения «Обсудить с Claude» (ADR-017, раздел 6): запрос человека, пометки
// словами и просьба вернуть предложенный запрос блоком ```image-prompt. Ответ Claude
// показывается в редакторе, а блок достаётся отсюда же — «Подставить в запрос».

import type { Mark } from '../marks';

export const IMAGE_PROMPT_LANG = 'image-prompt';

// Где на картинке точка: «слева вверху», «в центре»…
function place(x: number, y: number, w: number, h: number): string {
  const col = x < w / 3 ? 'слева' : x > (2 * w) / 3 ? 'справа' : '';
  const row = y < h / 3 ? 'вверху' : y > (2 * h) / 3 ? 'внизу' : '';
  if (!col && !row) return 'в центре';
  return [col, row].filter(Boolean).join(' ');
}

export function marksToWords(marks: Mark[], w: number, h: number): string[] {
  return marks.map(m => {
    switch (m.type) {
      case 'mask': {
        const xs = m.points.map(p => p[0]), ys = m.points.map(p => p[1]);
        const cx = (Math.min(...xs) + Math.max(...xs)) / 2, cy = (Math.min(...ys) + Math.max(...ys)) / 2;
        return `закрашено место ${place(cx, cy, w, h)} — его менять`;
      }
      case 'rect': return `рамка ${place(m.x + m.w / 2, m.y + m.h / 2, w, h)}`;
      case 'arrow': return `стрелка указывает ${place(m.x2, m.y2, w, h)}`;
      default: return `подпись «${m.text}» ${place(m.x, m.y, w, h)}`;
    }
  });
}

// Путь к папке подключённого персонажа дописывает сервер
export function buildDiscussText({ fileName, prompt, marks, size }: {
  fileName: string | null;
  prompt: string;
  marks: Mark[];
  size: { w: number; h: number } | null;
}): string {
  const lines: string[] = [];
  lines.push(fileName
    ? `Помоги с правкой картинки ${fileName} в редакторе картинок. Во вложении — картинка с моими пометками.`
    : 'Помоги с новой картинкой в редакторе картинок.');
  lines.push('', prompt.trim() ? `Мой запрос: ${prompt.trim()}` : 'Запроса пока нет — предложи, что можно сделать.');
  const words = size ? marksToWords(marks, size.w, size.h) : [];
  if (words.length) lines.push('', 'Пометки на картинке:', ...words.map(s => `- ${s}`));
  lines.push('',
    'Учти материалы проекта. Предложи запрос для модели рисования и верни его отдельным блоком',
    '```' + IMAGE_PROMPT_LANG, '…', '```');
  return lines.join('\n');
}

// Последний блок ```image-prompt из ответа; незакрытый (ответ ещё пишется) не берём
export function extractImagePrompt(text: string): string | null {
  const re = new RegExp('^[ \\t]{0,3}(`{3,}|~{3,})[ \\t]*' + IMAGE_PROMPT_LANG + '[ \\t]*\\n([\\s\\S]*?)\\n[ \\t]{0,3}\\1[ \\t]*$', 'gm');
  let last: string | null = null;
  for (let m = re.exec(text); m; m = re.exec(text)) last = m[2].trim();
  return last || null;
}

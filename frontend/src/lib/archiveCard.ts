// Чистая логика карточки архива (план «Архив чатов» v4, шаг 4+5): приоритет
// текста карточки и свежесть кэша сводки. Извлечено из ArchivePage.tsx ради
// тестируемости — без дубликата формул. Дубликат на бэке — ChatDigestService,
// но здесь нужна независимая реализация: заметка может быть локальной
// (Dify недоступно) или ещё не синкнута, и фронт показывает хоть что-то.

import type { Session } from '../types';

// Свежесть кэша сводки: та же формула, что на бэке (ChatDigestService.
//
//FreshSummary, UpdatedAt <= ArchiveSummaryAt — равные таймстемпы считаются
//свежими). При UpdatedAt > ArchiveSummaryAt сводка НЕ выдаётся за актуальную:
// после возврата чата, новых сообщений и повторной архивации карточка не
// показывает устаревший итог.
export function isFreshArchiveSummary(chat: Session): boolean {
  if (!chat.archiveSummary || !chat.archiveSummaryAt) return false;
  const summaryAt = new Date(chat.archiveSummaryAt).getTime();
  const updatedAt = new Date(chat.updatedAt).getTime();
  return updatedAt <= summaryAt && chat.archiveSummary.trim().length > 0;
}

// Приоритет текста карточки архива (канон — docs/product/archive-chats.md):
//   1. свежая archiveSummary (UpdatedAt <= ArchiveSummaryAt и не пустая);
//   2. первые строки заметки-итога (если передан noteLines — резолв заметки
//      вынесен наружу, чтобы модуль оставался чистой функцией);
//   3. lastMessage, если он непустой;
//   4. заглушка «Сообщений нет».
//
// Чистая функция: всё решает вход. Порядок приоритета зафиксирован каноном —
// перестановка ломала бы ожидания поддержки и пользователя («карточка вдруг
// перестала показывать сводку после правки»).
export const NO_MESSAGES_TEXT = 'Сообщений нет';

export function archiveCardText(
  chat: Session,
  noteLines: string | null,
): string {
  if (isFreshArchiveSummary(chat) && chat.archiveSummary) return chat.archiveSummary;
  if (noteLines) return noteLines;
  const last = chat.lastMessage?.trim();
  if (last) return last;
  return NO_MESSAGES_TEXT;
}

// Первые содержательные строки заметки: пропустить YAML-frontmatter и пустоты,
// взять до трёх строк, длинная строка режется на 300 символах (тот же
// алгоритм, что в ChatDigestService.FirstLines).
export function firstNoteLines(content: string): string | null {
  const lines = content.split('\n');
  // YAML frontmatter: первый --- на 0-й строке + закрывающий --- дальше.
  // Незакрытый блок НЕ считается шапкой — отдаём весь текст как есть, первый
  // `---` тогда попадёт в «содержательные строки». Зеркально с бэком:
  // ChatDigestService.StripFrontmatter при отсутствии второго `---` возвращает
  // исходный контент без изменений.
  if (lines[0] === '---') {
    const close = lines.indexOf('---', 1);
    if (close >= 0) lines.splice(0, close + 1);
  }
  const out: string[] = [];
  for (const line of lines) {
    if (out.length >= 3) break;
    const t = line.trim();
    if (t.length > 0) out.push(t);
  }
  let text = out.join('\n').trim();
  if (text.length > 300) text = text.slice(0, 300).trimEnd() + '…';
  return text.length === 0 ? null : text;
}
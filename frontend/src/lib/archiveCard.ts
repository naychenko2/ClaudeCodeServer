// Чистая логика карточки архива: свежесть кэша сводки. Раньше здесь жил и
// приоритет текста подвала карточки — подвала больше нет (карточка архивного
// чата обычная, действия ушли в контекстное меню), и вместе с ним ушли
// archiveCardText/firstNoteLines. Дубликат формулы на бэке — ChatDigestService.

import type { Session } from '../types';

// Свежесть кэша сводки: та же формула, что на бэке (ChatDigestService.
//
//FreshSummary, UpdatedAt <= ArchiveSummaryAt — равные таймстемпы считаются
//свежими). При UpdatedAt > ArchiveSummaryAt сводка НЕ выдаётся за актуальную:
// после возврата чата, новых сообщений и повторной архивации пункт меню
// предлагает собрать её заново, а не «обновить».
export function isFreshArchiveSummary(chat: Session): boolean {
  if (!chat.archiveSummary || !chat.archiveSummaryAt) return false;
  const summaryAt = new Date(chat.archiveSummaryAt).getTime();
  const updatedAt = new Date(chat.updatedAt).getTime();
  return updatedAt <= summaryAt && chat.archiveSummary.trim().length > 0;
}

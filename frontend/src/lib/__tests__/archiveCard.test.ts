// Юниты карточки архива: инвалидация сводки активностью чата. По этому
// предикату пункт меню карточки выбирает подпись «Собрать сводку» /
// «Обновить сводку». Чистая функция из lib/archiveCard — никаких асинхронных
// эффектов, тесты гоняются в node-окружении vitest.

import { describe, it, expect } from 'vitest';
import { isFreshArchiveSummary } from '../archiveCard';
import type { Session } from '../../types';

function mkChat(over: Partial<Session> = {}): Session {
  return {
    id: 'c',
    mode: 'auto',
    status: 'finished',
    messageCount: 0,
    createdAt: '2026-08-22T10:00:00Z',
    updatedAt: '2026-08-22T10:00:00Z',
    origin: 'manual',
    ...over,
  } as Session;
}

describe('isFreshArchiveSummary: инвалидация сводки активностью чата', () => {
  it('сводка отсутствует — не свежая', () => {
    expect(isFreshArchiveSummary(mkChat())).toBe(false);
  });

  it('сводка есть, ArchiveSummaryAt проставлен и UpdatedAt <= его — свежая', () => {
    const chat = mkChat({
      archiveSummary: 'короткая сводка разговора',
      archiveSummaryAt: '2026-08-23T12:00:00Z',
      updatedAt: '2026-08-23T11:00:00Z',
    });
    expect(isFreshArchiveSummary(chat)).toBe(true);
  });

  it('равные таймстемпы считаются свежими (симметрия с IsArchived)', () => {
    const chat = mkChat({
      archiveSummary: 'сводка',
      archiveSummaryAt: '2026-08-23T12:00:00Z',
      updatedAt: '2026-08-23T12:00:00Z',
    });
    expect(isFreshArchiveSummary(chat)).toBe(true);
  });

  it('после активности (UpdatedAt > ArchiveSummaryAt) сводка НЕ свежая', () => {
    const chat = mkChat({
      archiveSummary: 'устаревшая сводка',
      archiveSummaryAt: '2026-08-23T12:00:00Z',
      updatedAt: '2026-08-23T12:00:01Z',
    });
    expect(isFreshArchiveSummary(chat)).toBe(false);
  });

  it('пустая строка сводки трактуется как «нет сводки»', () => {
    const chat = mkChat({
      archiveSummary: '   ',
      archiveSummaryAt: '2026-08-23T12:00:00Z',
      updatedAt: '2026-08-23T11:00:00Z',
    });
    expect(isFreshArchiveSummary(chat)).toBe(false);
  });
});

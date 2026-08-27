// Юниты карточки архива (план «Архив чатов» v4, шаг 4+5): приоритет текста
// и инвалидация сводки. Чистые функции из lib/archiveCard — никаких
// асинхронных эффектов, тесты гоняются в node-окружении vitest.

import { describe, it, expect } from 'vitest';
import { archiveCardText, firstNoteLines, isFreshArchiveSummary, NO_MESSAGES_TEXT } from '../archiveCard';
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

describe('archiveCardText: приоритет текста карточки (канон)', () => {
  // Полный приоритет ровно один раз на каждом слое: свежая archiveSummary →
  // первые строки заметки (noteLines) → lastMessage → «Сообщений нет».
  it('приоритет 1: свежая archiveSummary показывается вместо всего остального', () => {
    const chat = mkChat({
      archiveSummary: 'Свежая сводка',
      archiveSummaryAt: '2026-08-23T12:00:00Z',
      updatedAt: '2026-08-23T11:00:00Z',
      lastMessage: 'просто сообщение',
      summaryNoteId: 'note-1',
    });
    expect(archiveCardText(chat, 'первая строка заметки')).toBe('Свежая сводка');
  });

  it('приоритет 2: при устаревшей сводке показываются первые строки заметки', () => {
    const chat = mkChat({
      archiveSummary: 'Устаревшая сводка',
      archiveSummaryAt: '2026-08-23T12:00:00Z',
      updatedAt: '2026-08-23T12:00:01Z', // > ArchiveSummaryAt → сводка не свежая
      lastMessage: 'просто сообщение',
      summaryNoteId: 'note-1',
    });
    expect(archiveCardText(chat, 'первая строка заметки')).toBe('первая строка заметки');
  });

  it('приоритет 3: без сводки и без заметки показывается lastMessage', () => {
    const chat = mkChat({ lastMessage: 'последняя реплика разговора' });
    expect(archiveCardText(chat, null)).toBe('последняя реплика разговора');
  });

  it('приоритет 4: ничего нет — заглушка «Сообщений нет»', () => {
    expect(archiveCardText(mkChat(), null)).toBe(NO_MESSAGES_TEXT);
  });

  it('whitespace-only lastMessage считается пустым', () => {
    const chat = mkChat({ lastMessage: '   ' });
    expect(archiveCardText(chat, null)).toBe(NO_MESSAGES_TEXT);
  });

  it('пустая строка в noteLines не блокирует lastMessage', () => {
    const chat = mkChat({ lastMessage: 'просто сообщение' });
    // noteLines === '' означает «резолв вернул пустоту», не «заметки нет»
    // (отсутствие SummaryNoteId трактуется как null). Здесь фронт не должен
    // показывать пустоту вместо доступного lastMessage
    expect(archiveCardText(chat, '')).toBe('просто сообщение');
  });

  it('порядок приоритета устойчив: сводка побеждает заметку, заметка побеждает lastMessage', () => {
    const baseChat = mkChat({
      lastMessage: 'm',
      summaryNoteId: 'note-1',
    });

    // Свежая сводка > заметка > lastMessage
    expect(archiveCardText(
      { ...baseChat, archiveSummary: 'S', archiveSummaryAt: '2026-08-23T12:00:00Z', updatedAt: '2026-08-23T11:00:00Z' } as Session,
      'N',
    )).toBe('S');

    // Устаревшая сводка — пропускаем на следующий уровень
    expect(archiveCardText(
      { ...baseChat, archiveSummary: 'S', archiveSummaryAt: '2026-08-23T12:00:00Z', updatedAt: '2026-08-23T13:00:00Z' } as Session,
      'N',
    )).toBe('N');

    // Заметки нет — lastMessage
    expect(archiveCardText(
      { ...baseChat, archiveSummary: 'S', archiveSummaryAt: '2026-08-23T12:00:00Z', updatedAt: '2026-08-23T13:00:00Z' } as Session,
      null,
    )).toBe('m');
  });
});

describe('firstNoteLines: первые строки заметки', () => {
  it('обычный текст: первые 3 непустые строки', () => {
    expect(firstNoteLines('A\nB\n\nC\nD')).toBe('A\nB\nC');
  });

  it('YAML frontmatter пропускается', () => {
    const note = '---\ntitle: x\ndate: 2026-08-22\n---\nСводка\nразговора';
    expect(firstNoteLines(note)).toBe('Сводка\nразговора');
  });

  it('незакрытый frontmatter (без второго ---) не считается шапкой', () => {
    // Нет закрывающего --- → контент не считается YAML-шапкой, идёт в тело.
    // Первые непустые строки: ---, title: x, A
    const note = '---\ntitle: x\nA\nB';
    expect(firstNoteLines(note)).toBe('---\ntitle: x\nA');
  });

  it('длинная строка режется на 300 символах с многоточием', () => {
    const long = 'x'.repeat(500);
    const out = firstNoteLines(long);
    expect(out).not.toBeNull();
    expect(out!.endsWith('…')).toBe(true);
    // 300 'x' + '…' = 301 (зеркально с бэком: ChatDigestService.FirstLines
    // делает text[..300].TrimEnd() + '…', давая 300 символов + 1 знак многоточия)
    expect(out!.length).toBe(301);
    // Голова строки сохранена
    expect(out!.startsWith('xxx')).toBe(true);
  });

  it('пустой контент и только-whitespace дают null', () => {
    expect(firstNoteLines('')).toBeNull();
    expect(firstNoteLines('\n   \n\n')).toBeNull();
  });

  it('возвращает максимум 3 строки даже если их много', () => {
    const note = 'A\nB\nC\nD\nE';
    const out = firstNoteLines(note);
    expect(out!.split('\n')).toHaveLength(3);
  });
});
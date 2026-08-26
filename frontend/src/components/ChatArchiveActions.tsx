import { useEffect, useState } from 'react';
import { ArchiveRestore, FileText, Sparkles } from 'lucide-react';
import type { Session } from '../types';
import { C, FS, SP, FONT } from '../lib/design';
import { Button } from './ui';
import { ICON_SIZE, ICON_STROKE } from './ui/icons';
import { api } from '../lib/api';
import { archiveCardText, firstNoteLines, isFreshArchiveSummary } from '../lib/archiveCard';

// Подвал карточки архивного чата: то полезное, что раньше жило на отдельной
// странице «Архив» (её больше нет — архив стал РЕЖИМОМ списка чатов). Показывается
// только у архивных карточек и только когда список дал обработчики (см. ChatCard).
//
// Содержимое: текст по канону (свежая сводка → первые строки заметки-итога →
// последнее сообщение → «Сообщений нет») и три действия — вернуть, собрать/обновить
// сводку, сохранить в заметки.
//
// Сетевого слоя подвал не знает: запросы (archiveApi.buildDigest,
// saveArchiveSessionAsNote, снятие архива) делает владелец списка — он же держит
// состояние сессий и рисует тосты. Здесь только локальный индикатор ожидания:
// сборка сводки зовёт модель и занимает секунды, а повторный клик в полёте сервер
// встречает 409 «сводка уже собирается».
export function ChatArchiveActions({
  chat, onRestore, onBuildDigest, onSaveAsNote,
}: {
  chat: Session;
  // Возврат из архива идёт тем же каналом, что и пункт меню карточки (onArchive(false)) —
  // второго пути к эндпоинту не заводим
  onRestore: () => void;
  // Обработчик ОБЯЗАН сам поймать ошибку и показать тост: подвал её не глотает и
  // не рисует — сообщения о неудаче принадлежат владельцу списка
  onBuildDigest: () => Promise<unknown>;
  onSaveAsNote: () => Promise<unknown>;
}) {
  // Текст карточки в приоритете канона. Резолв заметки — побочный эффект
  // (api.notes.resolve), его держим здесь; приоритет и формат строк живут в
  // lib/archiveCard, чтобы их можно было покрыть юнитами без побочек
  const [noteLines, setNoteLines] = useState<string | null>(null);
  useEffect(() => {
    let cancelled = false;
    setNoteLines(null);
    if (!chat.summaryNoteId) return;
    api.notes.resolve(chat.summaryNoteId).then(r => {
      if (cancelled) return;
      setNoteLines(firstNoteLines(r.note?.content ?? ''));
    }).catch(() => { if (!cancelled) setNoteLines(null); });
    return () => { cancelled = true; };
  }, [chat.summaryNoteId]);

  // Какое действие сейчас в полёте: у него крутится спиннер, соседнее выключено —
  // сводка и заметка спорят за одну и ту же сессию
  const [busy, setBusy] = useState<'digest' | 'note' | null>(null);
  const run = (kind: 'digest' | 'note', fn: () => Promise<unknown>) => {
    if (busy) return;
    setBusy(kind);
    void (async () => {
      try {
        await fn();
      } finally {
        setBusy(null);
      }
    })();
  };

  const text = archiveCardText(chat, noteLines);
  // Свежая сводка уже лежит в чате — кнопка предлагает её обновить, а не собрать
  // заново «с нуля»: иначе непонятно, почему текст на карточке не меняется
  const fresh = isFreshArchiveSummary(chat);

  return (
    // Клики гасим на обёртке: карточка целиком открывает чат, а подвал — это
    // действия НАД чатом, а не вход в него
    <div
      onClick={e => e.stopPropagation()}
      style={{
        // position: relative — иначе подложка с лицом собеседника (absolute)
        // легла бы поверх кнопок
        position: 'relative',
        marginTop: SP.sm,
        paddingTop: SP.sm,
        borderTop: `1px solid ${C.borderLight}`,
        display: 'flex', flexDirection: 'column', gap: SP.sm,
        cursor: 'default',
      }}
    >
      <p style={{
        margin: 0, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary,
        lineHeight: 1.45,
        // Многоточие: длинная сводка не должна раздувать карточку в списке
        display: '-webkit-box', WebkitLineClamp: 3, WebkitBoxOrient: 'vertical',
        overflow: 'hidden',
      }}>
        {text}
      </p>
      {/* Кнопки переносятся по строкам: колонка списка чатов узкая, в одну
          строку три подписи не встают */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, flexWrap: 'wrap' }}>
        <Button
          variant="ghostAccent"
          size="xs"
          leftIcon={<ArchiveRestore size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          onClick={onRestore}
        >
          Вернуть из архива
        </Button>
        <Button
          variant="secondary"
          size="xs"
          loading={busy === 'digest'}
          disabled={busy !== null}
          leftIcon={<Sparkles size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          onClick={() => run('digest', onBuildDigest)}
        >
          {fresh ? 'Обновить сводку' : 'Собрать сводку'}
        </Button>
        <Button
          variant="secondary"
          size="xs"
          loading={busy === 'note'}
          disabled={busy !== null}
          leftIcon={<FileText size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
          onClick={() => run('note', onSaveAsNote)}
        >
          Сохранить в заметки
        </Button>
      </div>
    </div>
  );
}

// Докачка истории чата по курсору (перф-2 B5): вместо полной истории (до 2,5 МБ на каждое
// открытие чата) клиент просит у сервера только записи после последней, что у него уже есть.
//
// Курсор выдаёт СЕРВЕР: anchorIndex — индекс последней завершающей запись хода
// (result/error/interrupted), anchorFp — отпечаток префикса истории до неё включительно.
// Клиент отпечаток не вычисляет, а эхоит. Сервер сверяет его: совпал — отдаёт хвост, не
// совпал (что-то до курсора переписано) — reset и полную историю. Идущий ход лежит ПОСЛЕ
// якоря, поэтому хвост с ним отдаётся всегда.
//
// Склейка работает над СЫРОЙ историей (записи как есть, до normalizeHistory): тогда
// normalizeHistory и serverHistoryNewer видят ровно тот же массив, что при полной загрузке.

// Ответ ручки с параметром after
export interface HistoryTailEnvelope {
  reset: boolean;
  messages: unknown[];
  anchorIndex: number;
  anchorFp: string | null;
}

// Ответ ручки истории: плоский массив (без after, старый сервер) либо конверт докачки
export type HistoryReply = unknown[] | HistoryTailEnvelope;

// Что клиент помнит о последней загрузке: сырая история и курсор для следующей
export interface HistoryCursor {
  raw: unknown[];
  anchorIndex: number;
  anchorFp: string | null;
}

// Параметры запроса: after=-1 — «ленты нет, дай всё и якорь» (так клиент получает первый курсор)
export interface HistoryCursorQuery {
  after: number;
  fp?: string;
}

// Курсор годится для докачки, только если у сервера был завершённый ход (якорь есть)
export function cursorQuery(cursor: HistoryCursor | undefined): HistoryCursorQuery {
  if (cursor && cursor.anchorIndex >= 0 && cursor.anchorFp)
    return { after: cursor.anchorIndex, fp: cursor.anchorFp };
  return { after: -1 };
}

// Конверт ли это (а не плоский массив старого контракта)
const isEnvelope = (reply: HistoryReply): reply is HistoryTailEnvelope => !Array.isArray(reply);

// Применяет ответ к тому, что было: возвращает полную сырую историю и новый курсор.
// cursor=undefined — сервер курсора не дал (плоский массив): следующая загрузка снова полная.
export function applyHistoryReply(
  prev: HistoryCursor | undefined,
  sent: HistoryCursorQuery,
  reply: HistoryReply,
): { raw: unknown[]; cursor: HistoryCursor | undefined } {
  if (!isEnvelope(reply)) return { raw: reply, cursor: undefined };

  // reset — сервер велит заменить всё; то же, если хвост не к чему приклеить
  // (курсора у нас не было или в ответе не хвост, а полная выдача после after=-1)
  if (reply.reset || !prev || sent.after < 0 || sent.after >= prev.raw.length) {
    return {
      raw: reply.messages,
      cursor: { raw: reply.messages, anchorIndex: reply.anchorIndex, anchorFp: reply.anchorFp },
    };
  }

  // Префикс [0..after] у нас и у сервера общий (отпечаток совпал) — дописываем хвост
  const raw = prev.raw.slice(0, sent.after + 1).concat(reply.messages);
  return { raw, cursor: { raw, anchorIndex: reply.anchorIndex, anchorFp: reply.anchorFp } };
}

// Класс конфликта сохранения заметки и тип «заметка + etag». Сами методы REST «Заметок»
// живут в lib/notesApi.ts: этот файл входит в область сторожа графа хранилища заметок
// (modules/notes/guardLists.mjs), а значения lib/offline (request, conflictBodyOf)
// напрямую сюда нельзя, через 'aihome_shell/kit' — даёт цикл featureFlags → api → kit.
// Типы стираются при сборке, поэтому берём их из кита.
import type { ConflictBody, FileConflictAuthor, FileConflictSource } from 'aihome_shell/kit';
import type { NoteDetail } from './types';

// Конфликт сохранения заметки (S2c-F3, К8): 409 с телом {code:'note_changed', …} на
// PUT /notes/{id} с If-Match. Поля — тело 409 как есть, тот же контракт К8, что у файла:
// актуальное состояние заметки на момент отказа. author и source бэк заполняет с S3-3,
// сейчас всегда null. Бросается ТОЛЬКО при ifMatch в notes.update: без If-Match сервер 409
// по конфликту не отдаёт вовсе, поэтому существующие вызывающие (без ifMatch) её не видят.
// Тип тела и интерфейсы author/source — в lib/offline: тот же контракт К8 служит
// конфликту файла (FileConflictError).
export class NoteConflictError extends Error {
  readonly status = 409;
  readonly code = 'note_changed';
  // Тег актуальной версии (без кавычек, как в теле 409) — по нему повторяют сохранение
  readonly sha256: string | null;
  // Актуальный текст чужой версии (заметка — всегда текст)
  readonly content: string | null;
  readonly author: FileConflictAuthor | null;
  readonly changedAt: string | null;
  readonly source: FileConflictSource | null;
  constructor(body: ConflictBody) {
    // Текст тот же, что заголовок диалога конфликта (SaveConflictDialog) — отдельной копии нет
    super('Заметку изменили, пока вы правили');
    this.name = 'NoteConflictError';
    this.sha256 = body.sha256 ?? null;
    this.content = body.content ?? null;
    this.author = body.author ?? null;
    this.changedAt = body.changedAt ?? null;
    this.source = body.source ?? null;
  }
}

// Заметка + тег прочитанной версии (S2c-F3): etag есть только у сетевых get/update;
// офлайн-доставка из кэша и локальный черновик тега не имеют — версии, по которой можно
// сохранить по If-Match, без сервера нет. Вызывающие без etag работают как раньше.
export type NoteWithEtag = NoteDetail & { etag?: string };

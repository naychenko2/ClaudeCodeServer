// Тип «заметка + etag». Сами методы REST «Заметок» живут в lib/notesApi.ts: этот файл входит
// в область сторожа графа хранилища заметок (modules/notes/guardLists.mjs). Класс
// NoteConflictError живёт в lib/offline и отдаётся через 'aihome_shell/kit' (единый экземпляр
// для хоста и remote).
import type { NoteDetail } from './types';

// Заметка + тег прочитанной версии (S2c-F3): etag есть только у сетевых get/update;
// офлайн-доставка из кэша и локальный черновик тега не имеют — версии, по которой можно
// сохранить по If-Match, без сервера нет. Вызывающие без etag работают как раньше.
export type NoteWithEtag = NoteDetail & { etag?: string };

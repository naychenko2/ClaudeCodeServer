// Открыть заметку по id через общий SPA-канал: обработчик #/notes/{id} в App
// переключает раздел «Заметки» и подхватывает id. Общий хелпер для виджета
// заметок и быстрых действий дашборда.
export function openNote(id: string): void {
  window.dispatchEvent(new CustomEvent('cc-open-url', {
    detail: { url: `#/notes/${encodeURIComponent(id)}` },
  }));
}

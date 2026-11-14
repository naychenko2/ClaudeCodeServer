// «Скачать» картинку нити: карточки версий, карточка-стопка и попап «Редактор»

function clickLink(href: string, name: string) {
  const a = document.createElement('a');
  a.href = href;
  a.download = name;
  a.click();
}

// Скачивание через blob: имя с расширением по типу ответа (у черновика его неоткуда взять).
// Сбой запроса — обычная ссылка, как раньше
export async function download(src: string, name: (mime: string) => string) {
  try {
    const r = await fetch(src);
    if (!r.ok) throw new Error(String(r.status));
    const blob = await r.blob();
    const url = URL.createObjectURL(blob);
    clickLink(url, name(blob.type));
    setTimeout(() => URL.revokeObjectURL(url), 60_000);
  } catch {
    clickLink(src, name(''));
  }
}

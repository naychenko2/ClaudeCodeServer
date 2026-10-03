// Подписи объектов контекста: сервер отдаёт путь файла проекта («img/hero.png»), человеку нужно имя («hero.png»).
// Пометка « · черновик» в подпись хода не едет: «✦ Снимаем Сцена 1…», а не «…Сцена 1 · черновик…»
export const baseName = (label: string): string => {
  const bare = label.replace(/ · черновик$/, '');
  const i = bare.lastIndexOf('/');
  return i >= 0 && i < bare.length - 1 ? bare.slice(i + 1) : bare;
};

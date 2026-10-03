// Подписи объектов контекста: сервер отдаёт путь файла проекта («img/hero.png»), человеку нужно имя («hero.png»).
export const baseName = (label: string): string => {
  const i = label.lastIndexOf('/');
  return i >= 0 && i < label.length - 1 ? label.slice(i + 1) : label;
};

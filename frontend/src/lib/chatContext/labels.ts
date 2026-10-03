// Подписи объектов контекста: сервер отдаёт путь файла проекта («img/hero.png»), человеку нужно имя («hero.png»).
// Пометка « · черновик» в подпись хода не едет: «✦ Снимаем Сцена 1…», а не «…Сцена 1 · черновик…»
export const baseName = (label: string): string => {
  const bare = label.replace(/ · черновик$/, '');
  const i = bare.lastIndexOf('/');
  return i >= 0 && i < bare.length - 1 ? bare.slice(i + 1) : bare;
};

// Безымянные черновики: «Новая картинка · черновик», «Новый звук · черновик». Их название в подпись хода не
// ставим — оно в именительном падеже и ломает фразу («Рисуем новая картинка…»)
export const isUnnamedDraft = (label: string): boolean => /^(Новая картинка|Новый звук) · черновик$/.test(label);

// Объект в подписи хода: у безымянного черновика его нет вовсе; после глагола с предлогом («Убираем фон у») имя
// идёт через пробел, после остальных — через двоеточие («Рисуем: кот.png»), чтобы падеж имени не ломал фразу
export function runObjectSuffix(verb: string, label: string | null | undefined): string {
  if (!label || isUnnamedDraft(label)) return '';
  const name = baseName(label);
  if (!name) return '';
  const last = verb.trim().split(/\s+/).pop() ?? '';
  return last.length <= 2 ? ` ${name}` : `: ${name}`;
}

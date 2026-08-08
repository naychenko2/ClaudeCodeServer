// Значок состояния файла — квадратик со знаком справа от имени: изменён, новый,
// удалён, переименован. Общий для дерева «Файлов» (там помечены отдельные строки
// среди всех прочих) и панели «Изменения» (там помечена каждая строка).
//
// Зачем общий: состояние файла раньше говорилось двумя разными языками — значком
// в «Файлах» и цветом самого имени в «Изменениях». Из-за этого зелёное имя значило
// «новый файл» в одной панели и «файл в базе знаний» в другой, а один и тот же
// изменённый файл выглядел в соседних панелях по-разному.

import { C } from '../../lib/design';

export type FileStatus = 'M' | 'A' | 'D' | 'R' | '?';

// Знак, цвета и подсказка по коду состояния. Знаки — однобуквенные коды git
// (как в `git status --porcelain`): M/A/D/R. Untracked git помечает «??», но семантически
// это новый файл, поэтому ему отдаём ту же букву A. Так набор однообразен: ни плюсов,
// ни стрелок, только буквы, и каждая — привычное программисту сокращение
const META: Record<FileStatus, { sign: string; fg: string; bg: string; title: string }> = {
  M: { sign: 'M', fg: C.accent,      bg: C.accentLight, title: 'Изменён' },
  A: { sign: 'A', fg: C.successText, bg: C.successBg,   title: 'Новый' },
  '?': { sign: 'A', fg: C.successText, bg: C.successBg, title: 'Новый (вне истории)' },
  D: { sign: 'D', fg: C.dangerText,  bg: C.dangerBg,    title: 'Удалён' },
  R: { sign: 'R', fg: C.info,        bg: C.infoBg,      title: 'Переименован' },
};

/** Значок состояния файла. Неизвестный код не рисуется вовсе. */
export function FileStatusBadge({ status }: { status: string }) {
  const m = META[status as FileStatus];
  if (!m) return null;
  return (
    <span title={m.title} style={{
      width: 16, height: 16, borderRadius: 4, flexShrink: 0,
      background: m.bg, color: m.fg,
      fontSize: 9, fontWeight: 700, lineHeight: 1,
      display: 'flex', alignItems: 'center', justifyContent: 'center',
    }}>{m.sign}</span>
  );
}

// Нижнее препятствие для круглешка AI (FAB): композер чата или футер мастера персон.
// Кнопка всегда стоит в правом нижнем углу и НЕ уезжает вверх — вместо этого она
// ужимается, когда препятствие доходит до её угла (узкое окно, широкая колонка чата).
//
// Владелец публикует сюда свой узел, замер пересечения делает сам FAB: только он знает
// свою геометрию, а она зависит от режима панелей. Узел, а не прямоугольник — препятствие
// меняет и высоту (композер растёт), и ширину (открылась панель), и следить за этим
// удобнее наблюдателем на месте замера.
type Listener = () => void;

let node: HTMLElement | null = null;
const subs = new Set<Listener>();

// null — препятствия нет (кнопка в углу в полный размер)
export function setFabObstacle(el: HTMLElement | null) {
  if (node === el) return;
  node = el;
  for (const f of subs) f();
}

export function getFabObstacle(): HTMLElement | null {
  return node;
}

export function subscribeFabObstacle(f: Listener): () => void {
  subs.add(f);
  return () => { subs.delete(f); };
}

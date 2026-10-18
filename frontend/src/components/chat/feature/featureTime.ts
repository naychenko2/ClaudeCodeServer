// Время в режиме фичи: ISO → «ЧЧ:ММ» / «dd.mm ЧЧ:ММ» в МЕСТНОМ поясе браузера.
//
// Бэк отдаёт UTC-ISO (строка с Z); макет v2 считает время по МСК, т.е. в местном
// поясе человека — поэтому локальные геттеры (getHours/getMinutes/getDate/getMonth),
// а не getUTC*. Тесты вычисляют ожидаемые значения теми же локальными геттерами
// от той же Date, поэтому не зависят от пояса машины (CI в UTC, разработка в МСК).

// ISO → «ЧЧ:ММ» (локальные часы:минуты); невалидная/пустая → null.
export function hhmm(iso?: string | null): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

// ISO → «dd.mm ЧЧ:ММ» (локальная дата-время); невалидная/пустая → null.
export function dateTimeLabel(iso?: string | null): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const dd = String(d.getDate()).padStart(2, '0');
  const hh = String(d.getHours()).padStart(2, '0');
  const mi = String(d.getMinutes()).padStart(2, '0');
  return `${dd}.${mm} ${hh}:${mi}`;
}

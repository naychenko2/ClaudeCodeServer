// Ф2d: раздел «Время и лимиты» окна настроек ребёнка (ADR-041 ред. 7; макет Майи
// kids-settings-window.html, раздел `time`). Поля и проверки перенесены один в один из прежнего
// редактора профиля ребёнка (KM-F11, тот редактор уже удалён) — но раздел — onChange-компонент:
// сам он ничего не сохраняет, черновик и «Сохранить» принадлежат каркасу окна (Ф2b).
//
// Контракт. `values` — текущие время и бюджет черновика родителя (те же поля, что у
// ChildSettingsProfile: окна и дневный лимит пространства «Уроки», бюджеты, предупреждение);
// `enabled` — флаг детского режима: раздел его не включает, он знает по нему только правило
// подсветки. Раздел держит свой черновик строк ввода и отдаёт через onChange целые корректные
// значения: пока в поле ошибка, из раздела не выходит ничего — черновик родителя хранит
// последние корректные значения, и красный текст в них не попадёт
// (тот же смысл, что «ни одного запроса» у образца, R-KM-F11).
// Внешняя смена values (загрузка профиля, ответ сохранения) перечитывает черновик; свой
// эхо подавляется сверкой СОДЕРЖАНИЯ, а не ссылки: каркас вправе выводить `values` из своего
// черновика на каждый рендер — сверка по ссылке сбрасывала бы ввод на каждое нажатие.
//
// Отличие от образца: бюджеты здесь разбираются по-настоящему — запятая принимается как
// десятичная точка («1,5» → 1.5), неразбираемое или отрицательное — ошибка поля. Образец
// на такие значения молча отдавал null: молчаливое «бюджета нет» опасно для детской границы
// так же, как молчаливое «лимита нет» (R-KM-F11).
// При выключенном режиме поля не подсвечиваются: их никуда не отправляем, и красное поле за
// значение, которое никто не отправит, пугает человека зря (тот же вывод, что у образца).

import { useRef, useState } from 'react';
import { Plus, Trash2 } from 'lucide-react';
import { C, FS, R, SP } from '../../lib/design';
import { Button } from '../../components/ui/Button';
import { Field, TextField } from '../../components/ui/Field';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import type { TimeWindow } from './types';

// Время и бюджет детской части профиля — те же поля, что у ChildSettingsProfile (./types):
// окна и дневный лимит «Уроков», бюджеты на день и месяц, порог предупреждения. Родитель
// хранит values как есть и строит из них тело запроса; раздел — их единственный редактор.
export interface ChildTimeValues {
  windows: TimeWindow[];
  dailyMinutes: number | null; // null — дневного лимита нет
  warnMinutes: number; // пустое поле — значение по умолчанию 5 (как в образце)
  dailyBudgetUsd: number | null; // null — дневного бюджета нет
  monthlyBudgetUsd: number | null; // null — месячного бюджета нет
}

interface Props {
  // Флаг детского режима: при выключенном поля не подсвечиваются (их никто не отправит).
  enabled: boolean;
  values: ChildTimeValues;
  onChange: (next: ChildTimeValues) => void;
}

const DEFAULT_WARN_MINUTES = 5;
// Строгий формат времени контракта: две цифры часа 00..23, две цифры минуты 00..59 (ADR-041).
const TIME_RE = /^([01]\d|2[0-3]):[0-5]\d$/;
// Дни недели — ISO 1..7 (Пн..Вс); пустой выбор — каждый день (поля weekdays в теле нет).
const DAY_LABELS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
// «Минут в день»: пусто — лимита нет; иначе — целое 1..1440. Всё прочее («3O», «0», «12.5»,
// «2000») — ошибка поля: молчаливое «лимита нет» из нечисловых значений опасно для детской
// границы (R-KM-F11).
const INT_RE = /^\d+$/;
const MINUTES_ERROR = 'Введи число минут, например 30';
const badMinutes = (s: string): boolean => {
  const t = s.trim();
  if (!t) return false;
  if (!INT_RE.test(t)) return true;
  const n = Number(t);
  return n < 1 || n > 1440;
};
// «Предупредить за»: пусто — значение по умолчанию (DEFAULT_WARN_MINUTES); иначе — целое 1..60.
const WARN_MAX = 60;
const WARN_ERROR = 'Введи число от 1 до 60, например 5';
const badWarn = (s: string): boolean => {
  const t = s.trim();
  if (!t) return false;
  if (!INT_RE.test(t)) return true;
  const n = Number(t);
  return n < 1 || n > WARN_MAX;
};
// Бюджеты: целое или дробное, запятая принимается как десятичная точка («1,5» → 1.5).
// Неразбираемое («abc», «-5») — ошибка поля; пусто — бюджета нет.
const USD_RE = /^\d+(?:[.,]\d+)?$/;
const USD_ERROR = 'Введи число долларов, например 2';
const badUsd = (s: string): boolean => {
  const t = s.trim();
  return t !== '' && !USD_RE.test(t);
};

// Черновик — строки полей ввода (пусто = поля нет в теле), окна списком, дни — множества ISO.
interface DraftWindow { from: string; to: string; days: number[] }
interface Draft {
  windows: DraftWindow[];
  dailyMinutes: string;
  warnMinutes: string;
  dailyBudget: string;
  monthlyBudget: string;
}

const draftOfValues = (v: ChildTimeValues): Draft => ({
  windows: (v.windows ?? []).map((w) => ({ from: w.from, to: w.to, days: w.weekdays ?? [] })),
  dailyMinutes: v.dailyMinutes == null ? '' : String(v.dailyMinutes),
  warnMinutes: String(v.warnMinutes ?? DEFAULT_WARN_MINUTES),
  dailyBudget: v.dailyBudgetUsd == null ? '' : String(v.dailyBudgetUsd),
  monthlyBudget: v.monthlyBudgetUsd == null ? '' : String(v.monthlyBudgetUsd),
});

const numOrNull = (s: string): number | null => {
  const t = s.trim();
  if (!t) return null;
  const n = Number(t);
  return Number.isFinite(n) ? n : null;
};
// Запятая — десятичная точка: «1,5» → 1.5. Пусто — null (бюджета нет).
const usdOrNull = (s: string): number | null => {
  const t = s.trim();
  if (!t) return null;
  const n = Number(t.replace(',', '.'));
  return Number.isFinite(n) ? n : null;
};

const valuesOf = (d: Draft): ChildTimeValues => ({
  windows: d.windows.map((w) => ({ from: w.from, to: w.to, weekdays: w.days.length ? w.days : undefined })),
  dailyMinutes: numOrNull(d.dailyMinutes),
  warnMinutes: numOrNull(d.warnMinutes) ?? DEFAULT_WARN_MINUTES,
  dailyBudgetUsd: usdOrNull(d.dailyBudget),
  monthlyBudgetUsd: usdOrNull(d.monthlyBudget),
});

// «Такие же, как мы послали» — эхо хранимых родителем значений, а не его изменение.
// Сверка по содержанию: каркас вправе выводить values из своего черновика на каждый рендер,
// сверка по ссылке перечитывала бы черновик на каждое нажатие.
const sameValues = (a: ChildTimeValues, b: ChildTimeValues): boolean => {
  if (a.dailyMinutes !== b.dailyMinutes || a.warnMinutes !== b.warnMinutes) return false;
  if (a.dailyBudgetUsd !== b.dailyBudgetUsd || a.monthlyBudgetUsd !== b.monthlyBudgetUsd) return false;
  if (a.windows.length !== b.windows.length) return false;
  return a.windows.every((w, i) => {
    const o = b.windows[i];
    const wa = w.weekdays ?? [];
    const ob = o.weekdays ?? [];
    return w.from === o.from && w.to === o.to && wa.length === ob.length && wa.every((x, j) => x === ob[j]);
  });
};

export function ChildTimeSection({ enabled, values, onChange }: Props) {
  const isMobile = useIsMobile();
  const [d, setD] = useState<Draft>(() => draftOfValues(values));
  // Что мы послали родителю (или что получили при монтировании): по этому опознаём эхо.
  const lastSent = useRef<ChildTimeValues>(values);
  // Внешняя смена values (загрузка профиля, ответ сохранения) — новый черновик: чужая правка
  // не теряется. Наш эхо (родитель вернул то, что мы послали) — черновик не трогаем:
  // он и есть эти значения, включая то, что человек правит сейчас.
  if (!sameValues(values, lastSent.current)) {
    setD(draftOfValues(values));
    lastSent.current = values;
  }

  // Единственный путь правки черновика. Основа — черновик текущего рендера: после каждого
  // ввода компонент перерисовывается (и вместе с ним родитель, чей setState в том же
  // событии), следующий ввод уже видит обновлённый черновик. Отвечаем целым значением или
  // не отвечаем вовсе: пока в полях ошибка, из раздела не выходит ничего — черновик родителя
  // хранит последние корректные значения, красный текст в них не попадёт (тот же смысл,
  // что «ни одного запроса» в save() образца).
  const commit = (fn: (prev: Draft) => Draft) => {
    const next = fn(d);
    setD(next);
    const v = valuesOf(next);
    if (
      v.windows.every((w) => TIME_RE.test(w.from) && TIME_RE.test(w.to)) &&
      !badMinutes(next.dailyMinutes) && !badWarn(next.warnMinutes) &&
      !badUsd(next.dailyBudget) && !badUsd(next.monthlyBudget)
    ) {
      lastSent.current = v;
      onChange(v);
    }
  };
  const patchWindow = (i: number, fn: (w: DraftWindow) => DraftWindow) =>
    commit((prev) => ({ ...prev, windows: prev.windows.map((w, j) => (j === i ? fn(w) : w)) }));

  const badTime = (v: string) => !TIME_RE.test(v);
  // Пустое поле нового окна не краснеем сразу (образец красил пустое только после попытки
  // сохранить, которой здесь нет); некорректное — красное, пока правят.
  const showBadTime = (v: string) => badTime(v) && v !== '';
  // «Минут в день» и «Предупредить за» краснеют только при включённом режиме: при выключенном
  // эти значения никуда не уходят, и красное поле за них пугает зря (R-KM-F11).
  const showBadDaily = enabled && badMinutes(d.dailyMinutes);
  const showBadWarn = enabled && badWarn(d.warnMinutes);
  const showBadDailyUsd = enabled && badUsd(d.dailyBudget);
  const showBadMonthlyUsd = enabled && badUsd(d.monthlyBudget);

  const rowGap = isMobile ? SP.sm : SP.md;
  const fieldStyle = { display: 'flex', flexDirection: 'column' as const, gap: rowGap };
  const dayChip = (day: number, i: number) => {
    const on = d.windows[i].days.includes(day);
    return (
      <Button
        key={day}
        variant={on ? 'ghostAccent' : 'ghost'}
        size="md"
        ariaPressed={on}
        ariaLabel={`День недели ${DAY_LABELS[day - 1]}`}
        onClick={() => patchWindow(i, (w) => ({ ...w, days: on ? w.days.filter((x) => x !== day) : [...w.days, day].sort() }))}
      >
        {DAY_LABELS[day - 1]}
      </Button>
    );
  };

  return (
    <div data-testid="child-time-section" style={fieldStyle}>
      <span style={{ fontSize: FS.xs, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>Окно «Уроков»</span>
      {d.windows.length === 0 && (
        <span data-testid="no-windows" style={{ fontSize: FS.sm, color: C.textMuted }}>Окон нет — время разрешено весь день, если хватит дневных минут.</span>
      )}
      {d.windows.map((w, i) => (
        <div key={i} data-testid="time-window" style={{ ...fieldStyle, padding: SP.sm, border: `1px solid ${C.borderLight}`, borderRadius: R.lg }}>
          <div data-testid="window-row" style={{ display: 'flex', gap: rowGap, flexDirection: isMobile ? 'column' : 'row' }}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <Field label="С" error={showBadTime(w.from) ? 'Формат ЧЧ:ММ' : undefined}>
                <TextField value={w.from} mono invalid={showBadTime(w.from)} inputMode="numeric" placeholder="09:00"
                  onChange={(v) => patchWindow(i, (prev) => ({ ...prev, from: v }))} />
              </Field>
            </div>
            <div style={{ flex: 1, minWidth: 0 }}>
              <Field label="По" error={showBadTime(w.to) ? 'Формат ЧЧ:ММ' : undefined} hint="Раньше «С» — окно через полночь">
                <TextField value={w.to} mono invalid={showBadTime(w.to)} inputMode="numeric" placeholder="20:00"
                  onChange={(v) => patchWindow(i, (prev) => ({ ...prev, to: v }))} />
              </Field>
            </div>
            <Button variant="ghost" size="md" ariaLabel="Удалить окно" dataTestId={`window-remove-${i}`}
              onClick={() => commit((prev) => ({ ...prev, windows: prev.windows.filter((_, j) => j !== i) }))}>
              <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
            </Button>
          </div>
          <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
            {DAY_LABELS.map((_, dayIdx) => dayChip(dayIdx + 1, i))}
          </div>
          <span style={{ fontSize: FS.sm, color: C.textMuted }}>
            {w.days.length ? `Дни: ${w.days.map((x) => DAY_LABELS[x - 1]).join(', ')}` : 'Дни не выбраны — каждый день'}
          </span>
        </div>
      ))}
      <Button variant="dashed" size="md" dataTestId="add-window" leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
        onClick={() => commit((prev) => ({ ...prev, windows: [...prev.windows, { from: '', to: '', days: [] }] }))}>
        Добавить окно
      </Button>
      <Field label="Минут в день" hint="Пусто — дневного лимита нет" error={showBadDaily ? MINUTES_ERROR : undefined}>
        <TextField value={d.dailyMinutes} inputMode="numeric" invalid={showBadDaily} onChange={(v) => commit((prev) => ({ ...prev, dailyMinutes: v }))} placeholder="30" />
      </Field>
      <Field label="Предупредить за, мин" hint="Сколько остаётся, когда ребёнок видит предупреждение" error={showBadWarn ? WARN_ERROR : undefined}>
        <TextField value={d.warnMinutes} inputMode="numeric" invalid={showBadWarn} onChange={(v) => commit((prev) => ({ ...prev, warnMinutes: v }))} />
      </Field>

      <span style={{ fontSize: FS.xs, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>Бюджет</span>
      <div data-testid="budget-row" style={{ display: 'flex', gap: rowGap, flexDirection: isMobile ? 'column' : 'row' }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <Field label="Бюджет на день, $" hint="Пусто — дневного бюджета нет" error={showBadDailyUsd ? USD_ERROR : undefined}>
            <TextField value={d.dailyBudget} inputMode="decimal" invalid={showBadDailyUsd} onChange={(v) => commit((prev) => ({ ...prev, dailyBudget: v }))} placeholder="2" />
          </Field>
        </div>
        <div style={{ flex: 1, minWidth: 0 }}>
          <Field label="Бюджет на месяц, $" hint="Пусто — месячного бюджета нет" error={showBadMonthlyUsd ? USD_ERROR : undefined}>
            <TextField value={d.monthlyBudget} inputMode="decimal" invalid={showBadMonthlyUsd} onChange={(v) => commit((prev) => ({ ...prev, monthlyBudget: v }))} placeholder="40" />
          </Field>
        </div>
      </div>
      <span style={{ fontSize: FS.sm, color: C.textMuted }}>
        Подписка считается по номинальной цене, как API. Локальная модель бюджет не тратит — её ограничивают окна и минуты.
      </span>
    </div>
  );
}

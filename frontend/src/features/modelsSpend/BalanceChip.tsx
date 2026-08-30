// Чип баланса внешнего сервиса: единый формат для виджета «Использование» и
// одноимённого блока в модалке «Модели и расход». Рендер и пороги «мало»
// лежат здесь — чтобы цифра и цвет числа не разъезжались между двумя
// местами при правке стиля.
import { C, FONT } from '../../lib/design';

// Денежный порог «мало осталось»: доллар и рубль на одной шкале сравнивать
// нельзя — 5 ₽ это уже почти ноль, поэтому пороги разные
export const LOW_BALANCE = 5;
export const LOW_BALANCE_RUB = 300;

export interface BalanceChipData {
  key: string;
  label: string;
  value: number;
  credits?: boolean;
  rub?: boolean;
}

export function BalanceChip({ b }: { b: BalanceChipData }) {
  const low = b.value < (b.rub ? LOW_BALANCE_RUB : LOW_BALANCE);
  return (
    <div style={{
      display: 'flex', alignItems: 'baseline', gap: 6, borderRadius: 10,
      padding: '7px 11px', background: C.bgCard, border: `1px solid ${C.borderLight}`,
    }}>
      <span style={{
        fontFamily: FONT.mono, fontSize: 15, fontWeight: 700,
        color: low ? C.dangerText : C.textHeading,
      }}>
        {b.credits
          ? `${(Number.isInteger(b.value) ? b.value.toLocaleString('ru-RU') : b.value.toFixed(2))} кр.`
          : b.rub
            ? `${b.value.toLocaleString('ru-RU', { maximumFractionDigits: 2 })} ₽`
            : `$${b.value < 1 ? b.value.toFixed(3) : b.value.toFixed(2)}`}
      </span>
      <span style={{ fontFamily: FONT.sans, fontSize: 11.5, color: C.textMuted }}>{b.label}</span>
    </div>
  );
}
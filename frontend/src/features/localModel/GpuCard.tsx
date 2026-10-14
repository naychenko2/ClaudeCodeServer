// Карточка видеокарты — общая для вкладок «LLM» и «Медиа»:
// C.bgWhite, рамка C.borderLight, R.lg. Подпись — имя карты из nvidia-smi хоста
// плюс место работы («RTX 3090 · текст» / «RTX 3090 · медиа»), под ней «карта N из M».
// Загрузка — полоса 4 px; память «21,4/24 ГБ»; температура (≥ 80 °C — warning);
// мощность «212/220 Вт».

import { C, FONT, FS, R, SP } from 'aihome_shell/kit';
import { formatGpuMem, formatPower, formatPerc, gpuLabel, gpuOrdinal } from './format';

// Поля видеокарты, общие для вкладок «LLM» (vLLM) и «Медиа» (ComfyUI).
// Числовые поля nullable: у медиа-карты (MediaGpu) они могут быть null,
// у текстовой (GpuDto) — undefined; GpuCard читает их через `!= null`.
// Отдельный интерфейс (не пересечение): пересечение с текстовым типом
// схлопывало `null` в `undefined` и медиа-карта переставала влезать.
interface GpuFields {
  index: number;
  name: string;
  utilPerc?: number | null;
  memUsedMb?: number | null;
  memTotalMb?: number | null;
  tempC?: number | null;
  powerW?: number | null;
  powerLimitW?: number | null;
}

// Строка «ключ — значение»: FS.xs, значение моно 600; hot — C.warningText
function GpuKv({ k, v, hot }: { k: string; v: string; hot?: boolean }) {
  return (
    <div style={{
      display: 'flex', justifyContent: 'space-between', gap: SP.xs,
      fontSize: FS.xs, padding: '1px 0',
    }}>
      <span style={{ color: C.textSecondary }}>{k}</span>
      <span style={{ fontFamily: FONT.mono, fontWeight: 600, color: hot ? C.warningText : C.textPrimary, whiteSpace: 'nowrap' }}>{v}</span>
    </div>
  );
}

export function GpuCard({ gpu, total, kind }: {
  gpu: GpuFields;
  // Сколько карт этого стенда: для «карта N из M»
  total: number;
  // Место работы: «текст» (vLLM) или «медиа» (ComfyUI)
  kind: 'text' | 'media';
}) {
  const totalGb = gpu.memTotalMb != null ? Math.round(gpu.memTotalMb / 1024) : null;
  // null-терпимость: tempC=null → не hot (не «0 °C»)
  const hot = gpu.tempC != null && gpu.tempC >= 80;
  return (
    <div style={{
      background: C.bgWhite, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
      padding: SP.sm, minWidth: 0,
    }}>
      <div style={{
        display: 'flex', justifyContent: 'space-between', flexWrap: 'wrap', gap: '0 6px',
        fontSize: FS.sm, fontWeight: 700, color: C.textHeading, marginBottom: SP.xxs, minWidth: 0,
      }}>
        <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {gpuLabel(gpu.name, kind)}
        </span>
        <span style={{ fontWeight: 400, color: C.textMuted, fontSize: FS.xs, whiteSpace: 'nowrap' }}>
          {totalGb != null ? `${totalGb} ГБ` : ''}
        </span>
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, margin: `-${SP.xs}px 0 ${SP.xs}px` }}>
        {gpuOrdinal(gpu.index, total)}
      </div>
      {/* utilPerc у бэка — целое 0..100 (nvidia-smi), а formatPerc ждёт долю 0..1 */}
      <GpuKv k="Загрузка" v={formatPerc(gpu.utilPerc != null ? gpu.utilPerc / 100 : null)} />
      <div style={{ height: 4, background: C.track, borderRadius: R.max, overflow: 'hidden', margin: `0 0 ${SP.xs}px` }}>
        <div style={{
          // null-терпимость: utilPerc=null → полоса 0% (не «null → 0 → 0%» как значение)
          width: `${gpu.utilPerc != null ? Math.max(0, Math.min(100, gpu.utilPerc)) : 0}%`,
          height: '100%', borderRadius: R.max, background: C.accent,
        }} />
      </div>
      <GpuKv k="Память" v={formatGpuMem(gpu.memUsedMb, gpu.memTotalMb)} />
      {/* null-терпимость: tempC=null → «—», не «0 °C» */}
      <GpuKv k="Темп." v={gpu.tempC != null ? `${Math.round(gpu.tempC)} °C` : '—'} hot={hot} />
      <GpuKv k="Мощность" v={formatPower(gpu.powerW, gpu.powerLimitW)} />
    </div>
  );
}

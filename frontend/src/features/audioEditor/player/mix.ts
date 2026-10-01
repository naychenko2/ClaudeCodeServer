// «Что сводим» в микшере стемов. Сводим то, что сейчас звучит: при включённом S
// звучит (и уходит в сведение) только солирующий стем, даже если на нём стоит M —
// S перекрывает M. Без S звучат все незаглушённые.

export const GAIN_MIN_DB = -24;
export const GAIN_MAX_DB = 6;

export interface StemInfo {
  id: string;
  name: string;
}

export interface MixerState {
  mute: Record<string, boolean>;
  /** Солирующий стем — один на микшер, как в макете */
  solo: string | null;
  /** Громкость стема, дБ; нет записи — 0 */
  gain: Record<string, number>;
}

export const EMPTY_MIXER: MixerState = { mute: {}, solo: null, gain: {} };

export interface MixPlanStem {
  id: string;
  name: string;
  gainDb: number;
}

export interface MixPlan {
  stems: MixPlanStem[];
  total: number;
  /** Можно ли свести: хотя бы один стем звучит */
  canMix: boolean;
  /** «вокал −3 дБ, барабаны, бас» — для подсказки и подписи будущей версии */
  description: string;
}

export function isAudible(id: string, st: MixerState): boolean {
  return st.solo ? st.solo === id : !st.mute[id];
}

export function clampGain(db: number): number {
  if (!Number.isFinite(db)) return 0;
  return Math.min(GAIN_MAX_DB, Math.max(GAIN_MIN_DB, Math.round(db)));
}

/** «−3 дБ» / «+2 дБ» / «0 дБ»; минус — типографский. */
export function fmtGain(db: number): string {
  if (db > 0) return `+${db} дБ`;
  if (db < 0) return `−${Math.abs(db)} дБ`;
  return '0 дБ';
}

export function mixPlan(stems: StemInfo[], st: MixerState): MixPlan {
  const live = stems
    .filter(s => isAudible(s.id, st))
    .map(s => ({ id: s.id, name: s.name, gainDb: clampGain(st.gain[s.id] ?? 0) }));
  return {
    stems: live,
    total: stems.length,
    canMix: live.length > 0,
    description: live.map(s => (s.gainDb ? `${s.name} ${fmtGain(s.gainDb)}` : s.name)).join(', '),
  };
}

/** M: переключить заглушку стема. */
export function toggleMute(st: MixerState, id: string): MixerState {
  return { ...st, mute: { ...st.mute, [id]: !st.mute[id] } };
}

/** S: солировать стем; повторное нажатие снимает соло. */
export function toggleSolo(st: MixerState, id: string): MixerState {
  return { ...st, solo: st.solo === id ? null : id };
}

export function setGain(st: MixerState, id: string, db: number): MixerState {
  return { ...st, gain: { ...st.gain, [id]: clampGain(db) } };
}

/** Децибелы → множитель для GainNode. */
export function dbToGain(db: number): number {
  return Math.pow(10, db / 20);
}

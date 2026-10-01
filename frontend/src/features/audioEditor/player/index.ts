// Компоненты плеера звука (шаг 2.8а): чистый UI на пропсах, к данным подключаются в 2.8б
export { AudioWave } from './AudioWave';
export type { AudioWaveProps } from './AudioWave';
export { AudioPlayer } from './AudioPlayer';
export type { AudioPlayerProps, AudioSource } from './AudioPlayer';
export { StemMixer } from './StemMixer';
export type { StemMixerProps, Stem } from './StemMixer';
export { useBrowserPeaks } from './useBrowserPeaks';
export { TO_END, clampSelection, fmtSelection, fmtTime, resolveEnd } from './selection';
export type { AudioSelection } from './selection';
export { mixPlan, EMPTY_MIXER } from './mix';
export type { MixPlan, MixerState } from './mix';

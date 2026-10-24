import type { PersonaVoice } from '../../types';

// Голос в DTO создания персоны. Различаем три состояния, а не два:
// - голос выбран — едет как есть;
// - человек явно выбрал «голос по умолчанию» (пунктом списка или «Сбросить») — пустой
//   объект: сервер создаёт персону без голоса и ничего не подбирает;
// - пикер не трогали — undefined: сервер подберёт голос по характеру сам.
export function createVoicePayload(voice: PersonaVoice | null, touched: boolean): PersonaVoice | undefined {
  if (voice?.voice) return voice;
  return touched ? {} : undefined;
}

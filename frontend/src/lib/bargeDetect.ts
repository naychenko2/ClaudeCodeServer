// Решение «перебивают ли меня» — чистая логика барж-ина, без браузера и без VAD-модели.
// Кадры (вероятность речи + громкость) приходят из lib/bargeVad, здесь только автомат
// решения. Вынесено отдельно ровно затем, зачем и редьюсер петли: покрыть юнитами.
//
// Две ступени вместо одной — потому что цена ошибки несимметрична. Услышали речь →
// ПРИГЛУШАЕМ озвучку (дёшево и обратимо); речь продолжилась → обрываем всерьёз и
// прерываем ход (дорого и необратимо). Чужая реплика из соседней комнаты стоит
// полусекундного «дака», а не потерянного ответа.
//
// Вторая защита — гейт громкости: перебивает только то, что заметно громче фона.
// Телевизор за стеной и разговор в отдалении отсекаются физикой, а не догадками.

export interface BargeThresholds {
  // Длительность кадра модели (v5: 512 сэмплов на 16 кГц = 32 мс)
  frameMs: number;
  // Кадр считается речью выше этого (Silero отдаёт 0..1)
  positive: number;
  // Ниже этого кадр заведомо не речь — по таким кадрам и меряем фон
  negative: number;
  // Речь накопилась столько — приглушаем озвучку
  duckMs: number;
  // ...и столько — обрываем её совсем (вместе с ходом). Меряется от НАЧАЛА речи, то есть
  // после приглушения нужно ещё (cutMs - duckMs). Перебивают обычно одним словом
  // («стоп», «покороче» — 400–600 мс), поэтому порог держим в этом диапазоне: с 800 мс
  // короткая команда не доживала до обрыва, озвучка возвращалась, и выглядело это как
  // «он меня не слышит» (замер по talkDiag 20.08.2026: приглушения по 450 и 950 мс,
  // обрыва — ни одного)
  cutMs: number;
  // Столько тишины после приглушения — ложная тревога, громкость обратно
  releaseMs: number;
  // Абсолютный минимум громкости (RMS): в тихой комнате фон близок к нулю, и одного
  // отношения к фону мало — шёпот телевизора прошёл бы гейт
  minRms: number;
  // Во сколько раз кадр должен быть громче фона
  bgFactor: number;
  // Инерция скользящего фона (0..1): чем меньше, тем медленнее он подстраивается
  bgAlpha: number;
}

export const BARGE_DEFAULTS: BargeThresholds = {
  frameMs: 32,
  positive: 0.8,
  negative: 0.65,
  duckMs: 300,
  cutMs: 550,
  releaseMs: 400,
  minRms: 0.015,
  bgFactor: 2.5,
  bgAlpha: 0.05,
};

export type BargeAction = 'none' | 'duck' | 'release' | 'cut';

export interface BargeDetector {
  // Один кадр: вероятность речи и его громкость (RMS). Возвращает, что делать
  push(prob: number, rms: number): BargeAction;
  // Канал закрывается/озвучка кончилась: если было приглушение — вернуть громкость
  reset(): BargeAction;
  // Для диагностики: текущий фоновый уровень
  background(): number;
  // Сколько речи было накоплено к последнему сбросу. Без этого числа в логе непонятно,
  // почему обрыв не наступил: «не дотянул 50 мс» и «не дотянул 400» лечатся по-разному
  lastSpeechMs(): number;
}

export function createBargeDetector(t: BargeThresholds = BARGE_DEFAULTS): BargeDetector {
  let speechMs = 0;
  let silenceMs = 0;
  let ducked = false;
  let bg = 0;
  let lastSpeech = 0;

  const clear = () => { lastSpeech = speechMs; speechMs = 0; silenceMs = 0; ducked = false; };

  return {
    push(prob, rms) {
      const gate = Math.max(t.minRms, bg * t.bgFactor);
      if (prob >= t.positive && rms >= gate) {
        speechMs += t.frameMs;
        silenceMs = 0;
        if (ducked) {
          if (speechMs >= t.cutMs) { clear(); return 'cut'; }
          return 'none';
        }
        if (speechMs >= t.duckMs) { ducked = true; return 'duck'; }
        return 'none';
      }

      // Фон подстраиваем ТОЛЬКО по заведомо неречевым кадрам: иначе речь сама себя
      // поднимет в фон, и гейт перестанет пропускать хоть что-то
      if (prob < t.negative) bg = bg * (1 - t.bgAlpha) + rms * t.bgAlpha;
      silenceMs += t.frameMs;
      if (silenceMs < t.releaseMs) return 'none';
      // Пауза затянулась: накопленное не в счёт (перебивание — непрерывная речь,
      // а не сумма реплик за минуту)
      const wasDucked = ducked;
      clear();
      return wasDucked ? 'release' : 'none';
    },

    reset() {
      const wasDucked = ducked;
      clear();
      return wasDucked ? 'release' : 'none';
    },

    background: () => bg,
    lastSpeechMs: () => lastSpeech,
  };
}

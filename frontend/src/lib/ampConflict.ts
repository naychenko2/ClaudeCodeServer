// Детект конфликта захватов микрофона по расхождению двух независимых источников:
// наша амплитуда (getUserMedia) голос слышит, а распознавание движка — нет.
//
// Полный труп движка ловит watchdog в useVoiceInput ('mic-dead'); здесь — тихая
// глухота: второй захват перехватил аудиопоток, движок формально жив (onstart
// пришёл), но слышит тишину и закрывает циклы без слов. Чистый класс без React —
// под юнит-тестом.

// Сколько человек должен говорить в пустоту, прежде чем мы объявим конфликт, не
// дожидаясь конца цикла. Цикл Web Speech без распознанной речи тянется 5-6 секунд,
// и всё это время сказанное уходит в никуда — расхождение же видно на второй
// секунде. Ниже брать нельзя: движок отзывается soundstart не мгновенно
export const EARLY_CONFLICT_MS = 1800;

export class AmpConflictDetector {
  // Амплитуда текущего цикла ушла выше порога речи — звук точно был
  private heardVoice = false;
  // Время первого громкого кадра цикла: от него отсчитывается ранний вердикт
  private voiceSince: number | null = null;
  // Движок распознавания подал признак слуха (soundstart/speechstart/результат) —
  // значит аудио до него доходит, и конфликта в этом цикле нет
  private engineHeard = false;
  // Наш getUserMedia-поток открыт: конфликт возможен только при живом втором захвате
  private streamOpen = false;

  // Амплитуда ушла выше порога речи (зовётся из rAF-лупа на каждом громком кадре)
  noteVoice(now: number = Date.now()): void {
    this.heardVoice = true;
    this.voiceSince ??= now;
  }

  // Движок услышал звук: снимает подозрение до конца цикла
  noteEngineHeard(): void { this.engineHeard = true; }

  // Ранний вердикт: мы слышим голос дольше порога, движок — молчит. Проверяется на
  // каждом кадре, поэтому дешёвый и без побочных эффектов: гасит конфликт вызывающий
  earlyConflict(now: number = Date.now()): boolean {
    if (!this.streamOpen || this.engineHeard || this.voiceSince === null) return false;
    return now - this.voiceSince >= EARLY_CONFLICT_MS;
  }

  // Открытие/закрытие нашего потока амплитуды. При закрытии слух обнуляем:
  // без захвата «слышали голос» относиться не к чему
  setStream(open: boolean): void {
    this.streamOpen = open;
    if (!open) this.resetCycle();
  }

  // Новый цикл распознавания: подозрения предыдущего к нему не относятся
  cycleStart(): void { this.resetCycle(); }

  private resetCycle(): void {
    this.heardVoice = false;
    this.voiceSince = null;
    this.engineHeard = false;
  }

  // Конец цикла распознавания; barren — движок не отдал ни слова за цикл.
  // Возвращает true, когда голос был у нас, но не у движка: второй захват
  // перехватил микрофон, честную амплитуду надо вырубить, пока петля жива
  cycleEnd(barren: boolean): boolean {
    const conflict = barren && this.streamOpen && this.heardVoice;
    // Слух живёт ровно один цикл: и вердикт, и плодный цикл одинаково обнуляют
    // его — иначе голос прошлого цикла ложился ложным конфликтом на молчаливый следующий
    this.resetCycle();
    return conflict;
  }
}

// --- Память устройства о конфликте захватов ---
//
// Вердикт детектора (или канарейки) раньше жил модульной переменной вкладки, и
// после каждой перезагрузки страницы устройство училось заново — а цена урока
// высока: первый цикл слушания уходит вхолостую целиком (замер на Android:
// ~5 с потерянной речи плюс ~2 с на медленный перезапуск движка). На планшете и
// телефоне, где система регулярно выгружает вкладку PWA, этот урок повторялся
// в каждом новом разговоре и выглядел как «распознавание срабатывает не сразу».
//
// Поэтому запоминаем вердикт на устройстве — как это делает micKeyboardFallback
// для мёртвого движка (lib/voiceInput.ts). Флаг односторонний: включает псевдо-
// амплитуду, разговора не ломает, поэтому ложный позитив безопасен и сбрасывать
// его нечем — сияние это украшение, распознавание важнее.
const AMP_UNSAFE_KEY = 'ampUnsafeDevice';

export function isAmpUnsafeDevice(): boolean {
  try { return localStorage.getItem(AMP_UNSAFE_KEY) === '1'; } catch { return false; }
}

export function markAmpUnsafeDevice(): void {
  try { localStorage.setItem(AMP_UNSAFE_KEY, '1'); } catch { /* недоступен — не критично */ }
}

export function clearAmpUnsafeDevice(): void {
  try { localStorage.removeItem(AMP_UNSAFE_KEY); } catch { /* недоступен — не критично */ }
}

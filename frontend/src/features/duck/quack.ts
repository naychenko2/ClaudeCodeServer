// Кряканье утки правой рельсы — синтез Web Audio, без звуковых файлов (ни веса в
// бандле, ни лицензии на сэмпл). Кряк = пила с падающей высотой через «носовой»
// полосовой фильтр плюс дребезг амплитуды — так и звучит утиный голос.
//
// AudioContext создаётся лениво на первом клике: браузер даёт звук только после
// жеста пользователя, а кряк всегда звучит из его клика.

export type QuackMood = 'normal' | 'angry' | 'sad';

let ctx: AudioContext | null = null;

function audio(): AudioContext | null {
  if (typeof window === 'undefined' || !window.AudioContext) return null;
  ctx ??= new AudioContext();
  // Вкладка долго простаивала — браузер мог приостановить контекст
  if (ctx.state === 'suspended') void ctx.resume();
  return ctx;
}

// Один кряк: высота from → to за dur секунд, громкость vol, старт со сдвигом at
function one(a: AudioContext, at: number, from: number, to: number, dur: number, vol: number) {
  const t0 = a.currentTime + at;

  const osc = a.createOscillator();
  osc.type = 'sawtooth';
  osc.frequency.setValueAtTime(from, t0);
  osc.frequency.exponentialRampToValueAtTime(to, t0 + dur);

  // Носовая окраска: полоса около 1.2 кГц, низ срезан
  const nose = a.createBiquadFilter();
  nose.type = 'bandpass';
  nose.frequency.value = 1200;
  nose.Q.value = 2.5;

  // Огибающая: быстрая атака, плавный спад
  const amp = a.createGain();
  amp.gain.setValueAtTime(0.0001, t0);
  amp.gain.exponentialRampToValueAtTime(vol, t0 + 0.015);
  amp.gain.setValueAtTime(vol, t0 + dur * 0.55);
  amp.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);

  // Дребезг: амплитуда дрожит ~38 раз в секунду — хрипотца утиного голоса
  const rasp = a.createOscillator();
  rasp.frequency.value = 38;
  const raspDepth = a.createGain();
  raspDepth.gain.value = vol * 0.45;
  rasp.connect(raspDepth).connect(amp.gain);

  osc.connect(nose).connect(amp).connect(a.destination);
  osc.start(t0); rasp.start(t0);
  osc.stop(t0 + dur + 0.02); rasp.stop(t0 + dur + 0.02);
}

export function quack(mood: QuackMood = 'normal') {
  const a = audio();
  if (!a) return;
  // Лёгкий разброс высоты, чтобы кряки подряд не звучали одним сэмплом
  const j = 1 + (Math.random() - 0.5) * 0.12;
  if (mood === 'angry') {
    // Злая очередь: три коротких низких кряка, громче обычного
    [0, 0.13, 0.26].forEach((at, i) => one(a, at, 420 * j - i * 20, 230 * j, 0.11, 0.22));
  } else if (mood === 'sad') {
    // Обиженный: один долгий, с провалом вниз
    one(a, 0, 360 * j, 150 * j, 0.42, 0.14);
  } else {
    one(a, 0, 520 * j, 300 * j, 0.17, 0.16);
  }
}

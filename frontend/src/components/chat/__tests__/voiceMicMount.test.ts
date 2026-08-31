// Сторож инварианта голосового ввода в полях: пока идёт запись, ни поле, ни сама
// кнопка микрофона НЕ размонтируются — поле прячется display:'none', а ряд индикации
// [точка, mm:ss, волна, ✕] рисует VoiceMicButton вместо себя (проп recordingRow).
//
// Регрессия, ради которой сторож: Field.tsx рендерил кнопку под условием
// `{voice && !isListening && (<VoiceMicButton …>)}`. Как только запись начиналась,
// кнопка уходила из DOM, а её useVoiceInput в cleanup зовёт recognition.abort() —
// распознавание умирало через мгновение после старта. Снаружи это выглядело
// исправной записью: таймер тикал, волна играла, а текст не приезжал никогда.
// Ровно так же ломает картину и размонтированное поле: ref обнуляется, и
// распознанному куску некуда дописываться.
//
// Проверяется исходник, а не поведение: рендер-инфраструктуры (jsdom +
// testing-library) во фронте нет, а инвариант структурный — он виден в разметке.

import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const SRC = join(process.cwd(), 'src');

function listTsx(root: string): string[] {
  const out: string[] = [];
  function walk(dir: string): void {
    let entries: string[];
    try { entries = readdirSync(dir); }
    catch { return; }
    for (const name of entries) {
      const full = join(dir, name);
      let st;
      try { st = statSync(full); }
      catch { continue; }
      if (st.isDirectory()) {
        if (name === 'node_modules' || name === 'dist' || name === 'dev-dist') continue;
        walk(full);
      } else if (st.isFile() && name.endsWith('.tsx')) {
        out.push(full);
      }
    }
  }
  walk(root);
  return out;
}

// Слова состояния записи: ими называют флаг «идёт запись» во всех местах показа
const REC_STATE = /recording|listening/i;

// Условие прямо перед тегом кнопки: `{ … && (<VoiceMicButton`. Внутрь условия
// не пускаем `{}<>` — так матч не перепрыгнет через соседнюю разметку
const GUARDED_MIC = /\{([^{}<>]*?)&&\s*\(?\s*<VoiceMicButton/g;

const users = listTsx(SRC)
  .filter(f => readFileSync(f, 'utf8').includes('<VoiceMicButton'))
  .map(f => ({ file: relative(SRC, f).replace(/\\/g, '/'), src: readFileSync(f, 'utf8') }));

describe('голосовой ввод: кнопка и поле переживают запись', () => {
  it('места показа кнопки найдены (иначе сторож сторожит пустоту)', () => {
    expect(users.length).toBeGreaterThan(0);
  });

  it.each(users)('$file: кнопка не спрятана под состояние записи', ({ file, src }) => {
    const guards = [...src.matchAll(GUARDED_MIC)].map(m => m[1].trim());
    const bad = guards.filter(g => REC_STATE.test(g));
    expect(
      bad,
      `В src/${file} <VoiceMicButton> рендерится под условием «${bad.join(' | ')}» — `
      + 'на время записи кнопка уйдёт из DOM, а useVoiceInput в cleanup сделает abort(): '
      + 'распознавание умрёт сразу после старта. Кнопка должна быть смонтирована всегда, '
      + 'ряд индикации она рисует сама по пропу recordingRow.',
    ).toEqual([]);
  });

  it.each(users.filter(u => u.src.includes('onListeningChange')))(
    '$file: поле прячется display, а ряд просят у кнопки',
    ({ file, src }) => {
      expect(
        src.includes('recordingRow'),
        `В src/${file} форма слушает onListeningChange (прячет своё поле), но не просит `
        + 'у кнопки ряд индикации (recordingRow) — на месте спрятанного поля будет пусто.',
      ).toBe(true);
      const hidesByState = /display:\s*[^,;\n]*(recording|listening)/i.test(src);
      expect(
        hidesByState,
        `В src/${file} поле на время записи должно ПРЯТАТЬСЯ (display по состоянию записи), `
        + 'а не подменяться другой веткой рендера: размонтированное поле обнуляет ref, '
        + 'и распознанному тексту некуда приезжать.',
      ).toBe(true);
    },
  );
});

// Тексты отказов фичи «Вики» (S7b-0): каждый код разбора по требованию (analyze /
// analyze-status / ingest/note) имеет свой русский текст — не сырой код и не общий
// статусный fallback; текст not_found выбирается по контексту вызова, а не по коду
// (W4-3, хвост R-S7b-0 H4). Прямой тест словаря: UI для этих кодов — S7b, его рендер — там.
// Согласование по числу (countWord, W4-3): формы для чисел с краевыми случаями
// правил (11–14 как 5+, 21 как 1, 22 как 2).

import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, it, expect } from 'vitest';
import { WikiError, type WikiErrorCode } from '../../../../lib/wiki';
import {
  INBOX_REASON_TEXT, INBOX_TEXT, PAGE_TYPE, analyzeRefusalText, countWord, isHumanTargetClass,
  wikiErrorText, wikiInboxReasonCode, wikiInboxReasonText,
} from '../wikiLabels';

describe('тексты кодов разбора по требованию (S7b-0)', () => {
  it('каждый код имеет свой русский текст, отличный от чужих, а не сырой код и не общий fallback', () => {
    const codes = [
      'not_found', 'task_execution', 'marked_skip', 'personal_section',
      'wiki_not_initialized', 'packages_limit', 'wiki_note_id',
      // хвост ревью R-S7c-2: общий словарь должен покрывать и эти коды
      'wiki_llm_wiki_off', 'wiki_class_key', 'wiki_triage_invalid',
    ];
    const texts = codes.map(code => wikiErrorText(new WikiError(409, code as WikiErrorCode, 'x')));
    codes.forEach((code, i) => {
      const text = texts[i];
      expect(text).toBeTruthy();
      expect(text).not.toBe(code);
      // своего текста нет → wikiErrorText отдаёт общий 409-fallback — это и есть провал
      expect(text).not.toBe('Действие отказано');
    });
    // попарная различность (доработка H1): подмена текста одного кода текстом другого —
    // провал, отдельная проверка каждого кода его не видит
    expect(new Set(texts).size).toBe(codes.length);
  });
});

describe('not_found — текст по контексту вызова (W4-3, хвост R-S7b-0 H4)', () => {
  it('чат и заметка — разные тексты; без контекста — нейтральный, не любой из них', () => {
    // бэк даёт один код для двух разных отказов: analyze — чужой/несуществующий чат
    // (WikiChatIngestService.cs:95-100), ingest/note — заметка не стала источником
    // (WikiController.cs:290-291)
    const e = new WikiError(404, 'not_found', 'x');
    const chat = wikiErrorText(e, 'chat');
    const note = wikiErrorText(e, 'note');
    expect(chat).toBe('Чат не найден');
    expect(note).toBe('Заметка не стала источником');
    // мутация «один текст на оба» краснит эту проверку
    expect(chat).not.toBe(note);
    const plain = wikiErrorText(e);
    expect(plain).not.toBe(chat);
    expect(plain).not.toBe(note);
  });
});

describe('согласование по числу (countWord, W4-3)', () => {
  it('1/2/5/11/12/14/21/22/25 — верные формы, в т.ч. 11–14 как 5+, 21 как 1, 22 как 2', () => {
    const cases: Array<[number, string]> = [
      [1, '1 пакет'], [2, '2 пакета'], [5, '5 пакетов'],
      [11, '11 пакетов'], [12, '12 пакетов'], [14, '14 пакетов'],
      [21, '21 пакет'], [22, '22 пакета'], [25, '25 пакетов'],
    ];
    for (const [n, want] of cases) {
      expect(countWord(n, 'пакет', 'пакета', 'пакетов')).toBe(want);
    }
  });
});

describe('текст wiki_triage_invalid (R-W4-1)', () => {
  it('файл разбора чатов не читается — русским текстом, не общим fallback', () => {
    const t = wikiErrorText(new WikiError(409, 'wiki_triage_invalid', 'x'));
    expect(t).toBe('Файл разбора чатов не читается');
  });
});

// LI-6: с LI-2 analyze/ingest/transfer сами заводят вики владельца, 409 wiki_not_initialized
// из действия человека не приходит (остаётся ответ сервисного слоя). Текст словаря —
// нейтральное сообщение о факте, без прежней готовой фразы «Твоя вики ещё не заведена» и
// без какой бы то ни было просьбы «завести» — кнопки «Завести вики» нет с LI-3.
describe('текст wiki_not_initialized — нейтральный, без просьбы «завести» (LI-6)', () => {
  it('никакой обращённой к человеку формы и ни одного побуждения к отсутствующей кнопке', () => {
    const t = wikiErrorText(new WikiError(409, 'wiki_not_initialized', 'x'));
    expect(t).toBe('Вики этого владельца не заведена');
    expect(t).not.toContain('Твоя');
    expect(t).not.toContain('завести');
    expect(t).not.toContain('заведите');
  });
});

// ─── Причины «Входящих» (F6a) ───
// Таблица «Входящие: причины правила замены» читается из файла ADR-038, а не копируется
// в тест: тест сверяет классификатор и тексты с самим ADR (постановка F6a). Строки-префиксы
// — левая колонка таблицы (зеркало строк WikiApplyPolicy.ReplacementBlocker и Decide),
// тексты — правая колонка, дословно.

const ADR_PATH = resolve(dirname(fileURLToPath(import.meta.url)),
  '../../../../../../docs/adr/ADR-038-notes-wiki-human-ai-coauthoring.md');

function adrReasonTable(): Array<{ prefix: string; label: string }> {
  const adr = readFileSync(ADR_PATH, 'utf8');
  const heading = '### «Входящие»: причины правила замены';
  const start = adr.indexOf(heading);
  expect(start).toBeGreaterThanOrEqual(0);
  const rest = adr.slice(start + heading.length);
  const end = rest.indexOf('\n### ');
  const section = end >= 0 ? rest.slice(0, end) : rest;
  const quoted = (c: string): string => {
    const a = c.indexOf('«');
    const b = c.lastIndexOf('»');
    return b > a ? c.slice(a + 1, b) : c;
  };
  // строки таблицы: обе колонки в «…»; «общий риск §12г» без кавычек — не причина
  // правила замены, её здесь нет
  return section
    .split('\n')
    .map(l => l.trim())
    .filter(l => l.startsWith('| «'))
    .map(l => l.split('|').map(c => c.trim()))
    .filter(cs => cs.length >= 3 && cs[1].startsWith('«') && cs[2].startsWith('«'))
    .map(cs => ({ prefix: quoted(cs[1]), label: quoted(cs[2]) }));
}

describe('причины «Входящих»: таблица сверяется с самим ADR-038 (F6a)', () => {
  const rows = adrReasonTable();
  const adr = readFileSync(ADR_PATH, 'utf8');

  it('таблица ADR прочитана: семь пунктов правила замены', () => {
    expect(rows.length).toBe(7);
  });

  it('каждый пункт: причина с началом строки ADR показывается текстом пункта; текст — из ADR', () => {
    for (const { prefix, label } of rows) {
      expect(wikiInboxReasonCode(prefix)).not.toBeNull();
      expect(wikiInboxReasonText(prefix)).toBe(label);
      expect(adr).toContain(`«${label}»`);
    }
  });

  it('нет секции «История»: строка бэка (TL-3, WikiApplyPolicy.cs:158) даёт новый код и текст для человека, а не unknown', () => {
    const raw = 'На этой странице нет секции «История», прежняя формулировка не сохранится — решите сами';
    expect(wikiInboxReasonCode(raw)).toBe('no_history_section');
    expect(wikiInboxReasonText(raw)).toBe('На этой странице нет раздела «История», прежняя формулировка не сохранится — решите сами');
    expect(wikiInboxReasonText(raw)).not.toBe(INBOX_TEXT.unknown);
  });

  it('тексты пунктов различны: подмена одного текста другим — провал', () => {
    expect(new Set(rows.map(r => r.label)).size).toBe(rows.length);
  });

  it('запасной текст: нераспознанное начало и пустая причина — нейтральный, без сырой строки', () => {
    const raw = 'целевая страница в статусе «disputed»: там уже нужен человек';
    expect(wikiInboxReasonCode(raw)).toBeNull();
    expect(wikiInboxReasonText(raw)).toBe(INBOX_TEXT.unknown);
    expect(wikiInboxReasonText(null)).toBe(INBOX_TEXT.unknown);
    expect(INBOX_TEXT.unknown).not.toContain('disputed');
  });

  it('подпись сверху списка — дословно из ADR-038', () => {
    expect(adr).toContain(INBOX_TEXT.caption);
  });

  it('цель человека — по хвосту класса @human (ADR-038, раздел «Входящие»)', () => {
    expect(isHumanTargetClass('session×supersede@human')).toBe(true);
    expect(isHumanTargetClass('session×supersede')).toBe(false);
    expect(isHumanTargetClass('')).toBe(false);
  });
});

// ─── Причина «число не подтверждено цитатой» (T-F1; ADR-039, решение 2) ───
// Строку бэка даёт WikiPlanValidator (.:434): фиксированное начало + динамический хвост
// чисел («: 15, 80%»). При вердикте политики «inbox» причина чисел дописывается после
// причины политики (та же строка бэка) — такой случай должен остаться за текстом политики.

describe('причина «число не подтверждено цитатой» (T-F1)', () => {
  it('строка бэка с числом классифицируется новым кодом, человеку — текст сверки с источником, а не unknown', () => {
    const raw = 'число не подтверждено цитатой: 15';
    expect(wikiInboxReasonCode(raw)).toBe('number_unconfirmed');
    expect(wikiInboxReasonText(raw)).toBe('В утверждении есть число, которого нет в цитатах — сверьте с источником');
    expect(wikiInboxReasonText(raw)).not.toBe(INBOX_TEXT.unknown);
  });

  it('несколько чисел и проценты в хвосте — та же классификация: хвост на неё не влияет', () => {
    expect(wikiInboxReasonCode('число не подтверждено цитатой: 15, 80%')).toBe('number_unconfirmed');
  });

  it('политика с дописанной причиной чисел классифицируется по первому префиксу (политики)', () => {
    const raw = 'Источник старше вашей правки; число не подтверждено цитатой: 15';
    expect(wikiInboxReasonCode(raw)).toBe('source_older_than_edit');
    expect(wikiInboxReasonText(raw)).toBe(INBOX_REASON_TEXT.source_older_than_edit);
  });
});

describe('отказ разбора чата по-человечески (WM-2)', () => {
  it('«пустой ответ» — «модель не ответила»; окна и проходы бэка в текст не попадают', () => {
    const raw = 'пустой ответ модели в окне m11-m381, проход A';
    const text = analyzeRefusalText(raw);
    expect(text).toBe('Не удалось разобрать: модель не ответила');
    expect(text).not.toContain('m11');
    expect(text).not.toContain('проход');
    expect(analyzeRefusalText('Пустой ответ')).toBe('Не удалось разобрать: модель не ответила');
  });

  it('неизвестная причина и её отсутствие — «Не удалось разобрать» без хвоста', () => {
    expect(analyzeRefusalText('нет утверждений с цитатами в окнах m1-m9')).toBe('Не удалось разобрать');
    expect(analyzeRefusalText(null)).toBe('Не удалось разобрать');
    expect(analyzeRefusalText('')).toBe('Не удалось разобрать');
  });
});

// ─── Типы страниц (дефект d7ea8ba0) ───
// Ключ словаря PAGE_TYPE — значение поля type шапки; источник правды — бэковый
// WikiFrontmatter.TypeValues. Без rule/term/state в словаре WikiPageView считал валидную
// шапку неразобранной и показывал плашку (.:103). Список читается из бэкового файла, а не
// копируется в тест (тот же приём, что с таблицей ADR-038 выше): новый вид на бэке краснит
// этот тест раньше, чем страница начнёт врать человеку о прочитанной шапке.

const FRONTMATTER_PATH = resolve(dirname(fileURLToPath(import.meta.url)),
  '../../../../../../backend/ClaudeHomeServer.Notes/Services/Wiki/WikiFrontmatter.cs');

function backendTypeValues(): string[] {
  const src = readFileSync(FRONTMATTER_PATH, 'utf8');
  // константы TypeXxx = "…" — сами значения вида
  const consts = new Map<string, string>();
  for (const m of src.matchAll(/public const string (Type\w+) = "([a-z]+)";/g)) consts.set(m[1], m[2]);
  // список TypeValues = [TypeOverview, …] — ссылки на константы (\b не даёт захватить CreatableTypeValues)
  const list = src.match(/\bTypeValues =\s*\[([^\]]*)\]/);
  expect(list).not.toBeNull();
  return [...list![1].matchAll(/\bType\w+\b/g)].map((m) => {
    const v = consts.get(m[0]);
    expect(v).toBeTruthy();
    return v!;
  });
}

describe('типы страниц: словарь знает все виды бэка (дефект d7ea8ba0)', () => {
  it('ключи PAGE_TYPE — ровно WikiFrontmatter.TypeValues; rule/term/state — русские подписи', () => {
    // сравнение по множеству, не по порядку: словарь читается по ключу, порядок записи
    // в нём — дело витрины, а порядок TypeValues — дело бэка
    expect([...Object.keys(PAGE_TYPE)].sort()).toEqual(backendTypeValues().sort());
    expect(PAGE_TYPE.rule).toBe('Правило');
    expect(PAGE_TYPE.term).toBe('Термин');
    expect(PAGE_TYPE.state).toBe('Состояние');
  });
});

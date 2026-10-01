// Режимы и операции раздела «Звук» (макет docs/mockups/audio-editor-v2.html, таблица OPS):
// подпись пилюли, глагол кнопки запуска, плейсхолдер композера и что композер отдаёт
// модели. Порядок внутри режима — порядок пилюль; первая — операция режима по умолчанию.

import type { AudioMode, AudioOp } from './api';

// Куда уходит текст композера: text — озвучка, prompt — стиль/описание, none — не нужен
export type ComposerField = 'text' | 'prompt' | 'none';

export interface OpInfo {
  op: AudioOp;
  mode: AudioMode;
  label: string;
  run: string;
  placeholder: string;
  field: ComposerField;
}

const NO_COMMENT = 'Комментарий не нужен';

const op = (op: AudioOp, mode: AudioMode, label: string, run: string, placeholder: string, field: ComposerField): OpInfo =>
  ({ op, mode, label, run, placeholder, field });

export const OPS: readonly OpInfo[] = [
  op('speak', 'voice', 'Озвучить', 'Озвучить', 'Текст для озвучки — паузы <#0.5#> и теги (laughs) понимают MiniMax и ElevenLabs', 'text'),
  op('dialogue', 'voice', 'Диалог', 'Озвучить диалог', 'Тема или подводка (необязательно) — реплики заполните ниже', 'text'),
  op('designVoice', 'voice', 'Голос по описанию', 'Озвучить', 'Текст для озвучки — голос опишите в настройках', 'text'),
  op('cloneVoice', 'voice', 'Клон по образцу', 'Озвучить', 'Текст для озвучки голосом из образца', 'text'),
  op('convertVoice', 'voice', 'Сменить голос', 'Сменить голос', `${NO_COMMENT} — нажмите «Сменить голос»`, 'none'),
  op('trainVoice', 'voice', 'Обучить голос', 'Обучить', 'Имя голоса для библиотеки, например «Андрей»', 'prompt'),
  op('song', 'music', 'Песня', 'Сочинить', 'Стиль: жанр, настроение, инструменты, голос — по-английски модели понимают лучше', 'prompt'),
  op('cover', 'music', 'Кавер', 'Сделать кавер', 'Новый стиль: например «акустика, женский вокал»', 'prompt'),
  op('repaint', 'music', 'Перегенерировать кусок', 'Перегенерировать', 'Что должно звучать в выделенном куске', 'prompt'),
  op('outpaint', 'music', 'Продолжить', 'Продолжить', 'Чем продолжить: например «плавное затухание, струнные»', 'prompt'),
  op('extract', 'music', 'Вытащить дорожку', 'Вытащить', `${NO_COMMENT} — выберите дорожку`, 'none'),
  op('lego', 'music', 'Дописать дорожку', 'Дописать', 'Характер новой дорожки: например «тёплый бас, пальцами»', 'prompt'),
  op('complete', 'music', 'Доаранжировать', 'Доаранжировать', 'Комментарий необязателен', 'prompt'),
  op('sfx', 'music', 'Звуковой эффект', 'Сгенерировать звук', 'Опишите звук до 450 символов: «скрип двери в пустом подъезде»', 'prompt'),
  op('separate', 'process', 'Стемы', 'Разделить', 'Для SAM Audio — что выделить: «лай собаки»', 'prompt'),
  op('denoise', 'process', 'Очистить шум', 'Очистить', NO_COMMENT, 'none'),
  op('upsample', 'process', 'Восстановить частоты', 'Восстановить', NO_COMMENT, 'none'),
  op('master', 'process', 'Мастеринг', 'Сделать мастеринг', `${NO_COMMENT} — выберите образец`, 'none'),
  op('transcribe', 'process', 'В текст', 'Распознать', 'Подсказка имён и терминов (необязательно)', 'prompt'),
  op('align', 'process', 'Время к тексту', 'Расставить время', 'Вставьте точный текст записи — получите .srt и .lrc', 'text'),
  op('toMidi', 'process', 'В MIDI', 'Снять ноты', NO_COMMENT, 'none'),
  op('trim', 'process', 'Обрезка и громкость', 'Применить', NO_COMMENT, 'none'),
  op('gainFade', 'process', 'Громкость и затухание', 'Применить', NO_COMMENT, 'none'),
  op('normalize', 'process', 'Нормализация', 'Применить', NO_COMMENT, 'none'),
  op('mixStems', 'process', 'Свести стемы', 'Свести', NO_COMMENT, 'none'),
  op('concat', 'process', 'Склеить', 'Склеить', `${NO_COMMENT} — соберите куски в панели и нажмите «Склеить»`, 'none'),
];

export const MODE_LABEL: Record<AudioMode, string> = { voice: 'Голос', music: 'Музыка', process: 'Обработка' };

export const opInfo = (op: AudioOp | null | undefined): OpInfo | null => OPS.find(o => o.op === op) ?? null;

export const defaultOp = (mode: AudioMode): AudioOp => OPS.find(o => o.mode === mode)!.op;

// Якоря кнопки «Ветвление» (фича chat-branch): для каждого элемента ленты — порядковый
// номер user_message, к которому этот элемент примыкает.
//
// У ленты элементы не имеют стабильного id, а индексы items и messages расходятся штатно
// (normalizeHistory вливает workflow_progress в tool_use и группирует ошибки), поэтому
// сервер и фронт считают одно и то же множество — порядковый номер user_message.
// Для самого user_message — его собственный номер; для ответа ассистента — номер
// ПОСЛЕДНЕГО user_message выше (то же значение, что у этого user_message).
// Нет user_message выше — якоря нет (−1).
//
// anchorText (первые ~120 символов сообщения, по которому сервер сверяет найденное и
// отвечает 409 на расхождении) здесь НЕ хранится. Это срез текста сообщения, а V8
// держит такой срез вместе с ИСХОДНОЙ строкой целиком. Структура пересобирается на
// каждое изменение ленты (то есть на каждую стрим-дельту), а лента не виртуализирована —
// в часовом замере это дало прирост живой кучи под местом вызова (см. отчёт по профилю).
// Текст нужен серверу только в момент клика, поэтому берём его оттуда же.
//
// Хранение — два типизированных массива: 5 байт на элемент ленты.

import type { ChatItem } from '../types';

// Вид ветвления: ответ ассистента (turn) и само сообщение человека (beforePrompt)
export const BRANCH_TURN = 1;
export const BRANCH_BEFORE_PROMPT = 2;

export interface BranchAnchors {
  // Порядковый номер user_message по индексу элемента ленты; −1 — якоря нет
  userIdx: Int32Array;
  // Один из BRANCH_*; 0 — якоря нет
  include: Uint8Array;
}

export function buildBranchAnchors(items: ChatItem[]): BranchAnchors {
  const userIdx = new Int32Array(items.length).fill(-1);
  const include = new Uint8Array(items.length);
  let count = 0;
  for (let i = 0; i < items.length; i++) {
    if (items[i].kind === 'user_message') {
      userIdx[i] = count;
      include[i] = BRANCH_BEFORE_PROMPT;
      count++;
    } else if (count > 0) {
      userIdx[i] = count - 1;
      include[i] = BRANCH_TURN;
    }
  }
  return { userIdx, include };
}

// Текст якорного сообщения по его порядковому номеру среди user_message.
// Вне диапазона — пустая строка (сервер ответит 409, клик по такой кнопке невозможен).
export function branchAnchorText(items: ChatItem[], userMessageIdx: number): string {
  let seen = 0;
  for (const it of items) {
    if (it.kind !== 'user_message') continue;
    if (seen === userMessageIdx) return it.text.slice(0, 120);
    seen++;
  }
  return '';
}
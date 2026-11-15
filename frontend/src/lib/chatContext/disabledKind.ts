// Признак неактивного вида контекста (S2b-3a) и его причина — два разных состояния.
//
// Реестр слота context-kind собирается по вкладам ВКЛЮЧЕННЫХ фронтовых подсистем (гейт чтения
// в registryCore.getSlotContributions), поэтому вида нет в реестре в двух случаях:
//  1. ключ выключен администратором: бэк не отдаёт его MF-remote вовсе (SubsystemModulesController
//     фильтрует по ActiveKeys), и бэковый провайдер вида не зарегистрирован (выключенная
//     вертикаль не вызывает *Subsystem.Register → AddContextKindProvider); Describe не находит
//     провайдера и ставит в DTO Missing=true (ChatContextDtoBuilder.cs);
//  2. ключ включён, но фронтовый MF-remote не загрузился за срок старта (App.tsx ждёт
//     loadSubsystemRemotes 2 с): бэковый провайдер есть, он описывает объект и ставит
//     Missing=false — не доехал только фронт.
//
// Различает их поле `missing` самого объекта: true — «у бэка нет провайдера вида», false —
// «бэковый провайдер нашёл и описал объект». По ключу подсистемы различить нельзя: строка
// контекста знает только kind (в DTO ключа вертикали нет), а статической карты «вид → ключ»
// в каркаде нет — виды объявлены только в манифестах самих вертикалей.
//
// Крайний случай, которого различить нечем: вертикаль включена, фронт не загрузился И сам
// объект удалён — бэк ставит Missing=true по второй причине, и плашка назовёт «выключен».
// Это требует двух неисправностей сразу; отдельного текста на такую пару продукт не согласовал.
//
// Исключение — виды файлов «project-file» и «personal-file»: кита у них нет by design (владелец вида —
// бэковый ProjectFileContextKind / PersonalFileContextKind, фронт рисует их файлом-заглушкой),
// это не «выключенный раздел».

import { SUBSYSTEM_DISABLED_REASON } from '../subsystems';
import type { ChatContextPrimary, ChatContextRef, ContextKindApi } from './types';

// Причина неактивности вида: 'admin' — ключ выключен администратором; 'notLoaded' — ключ
// включён, но фронтовый MF-remote вертикали не загрузился.
export type DisabledKindReason = 'admin' | 'notLoaded';

// Виды, чей провайдер живёт на бэкенде, а фронтового кита у них нет by design
const BACKEND_ONLY_KINDS = new Set(['project-file', 'personal-file']);

export const isKindDisabled = (kind: string, registry: ReadonlyMap<string, ContextKindApi>): boolean =>
  !BACKEND_ONLY_KINDS.has(kind) && !registry.has(kind);

// Причина для неактивного вида: по признаку «бэковый провайдер описывает объект».
// itemMissing — поле `missing` DTO этого объекта: false — провайдер есть и объект жив
// (вертикаль включена, не доехал фронт); true — провайдера нет (вертикаль выключена)
// или объект удалён. null — вид активен или это файл проекта.
export const disabledKindReason = (
  kind: string,
  registry: ReadonlyMap<string, ContextKindApi>,
  itemMissing: boolean,
): DisabledKindReason | null => {
  if (!isKindDisabled(kind, registry)) return null;
  return itemMissing ? 'admin' : 'notLoaded';
};

// Пользовательский текст причины (согласован продуктом, менять нельзя): 'admin' — общий с
// заглушкой раздела текст (SUBSYSTEM_DISABLED_REASON), 'notLoaded' — отдельный: администратор
// ничего не выключал, модуль не доехал сам.
export const disabledKindText = (reason: DisabledKindReason): string =>
  reason === 'notLoaded' ? 'Раздел не загрузился' : `Раздел ${SUBSYSTEM_DISABLED_REASON}`;

// Проводка объекта строки контекста (S2b-3a): неактивный вид получает вместо label текст причины,
// версию (на плашке с текстом-пояснением она только шум) и missing=true — по нему вью гасит чип
// (ContextRowView → RowChip: opacity). Активный вид и файлы «project-file»/«personal-file» — как есть.
export const dimmedKindItem = <T extends ChatContextPrimary | ChatContextRef>(
  item: T,
  registry: ReadonlyMap<string, ContextKindApi>,
): T => {
  const reason = disabledKindReason(item.kind, registry, item.missing);
  return reason ? { ...item, label: disabledKindText(reason), version: null, missing: true } : item;
};

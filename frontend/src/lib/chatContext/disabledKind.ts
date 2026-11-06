// Признак выключенного вида контекста (S2b-3a): у вида нет кита во фронтовом реестре слота
// context-kind. Реестр собирается по вкладам включённых подсистем (гейт чтения в
// registryCore.getSlotContributions), поэтому вида выключенной или удалённой вертикали в нём нет —
// и прикреплённый объект такой вертикали не может иметь ни иконки, ни действий, ни «Формата».
// Исключение — вид файла «project-file»: кита у него нет by design (владелец вида — бэковый
// ProjectFileContextKind, фронт рисует его файлом-заглушкой), это не «выключенный раздел».

import type { ContextKindApi } from './types';

export const isKindDisabled = (kind: string, registry: ReadonlyMap<string, ContextKindApi>): boolean =>
  kind !== 'project-file' && !registry.has(kind);

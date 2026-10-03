// Образцы и персонаж как референсы контекста чата (ADR-023, шаг 2к-3).
// `_samples` вкладки и `prefs.characterSlug` не пишутся: образец с диска — загрузка в рабочую папку модуля и
// `attachRef({kind: 'image', ref: {upload}})`, файл проекта — `project-file`, персонаж — `image-character`.
// Так выбор переживает перезагрузку страницы и виден агенту в `context_state`.

import { attachRef, detachRef, getChatContextState, type ChatContextDto, type ChatContextRef } from 'aihome_shell/kit';
import { imageEditorApi } from '../api';
import { IMAGE_KIND } from './state';

export const CHARACTER_KIND = 'image-character';
export const FILE_KIND = 'project-file';
export const SAMPLE_ACCEPT = 'image/png,image/jpeg,image/webp';

// Образцы: картинки и файлы проекта с ролью образца (стиль, объект, персонаж)
export const sampleRefs = (state: ChatContextDto): ChatContextRef[] =>
  state.refs.filter(r => (r.kind === IMAGE_KIND || r.kind === FILE_KIND) && r.role !== null);

export const characterRefs = (state: ChatContextDto): ChatContextRef[] => state.refs.filter(r => r.kind === CHARACTER_KIND);

export const characterSlugOf = (state: ChatContextDto): string | null => {
  const slug = characterRefs(state)[0]?.ref.slug;
  return typeof slug === 'string' ? slug : null;
};

// Входы запуска по ревизии контекста: бэкенд читает образцы и персонажа из стора, а фронту нужны только их
// число и признак персонажа — для котировки (цена зависит от числа референсов)
export function contextInputCounts(sessionId: string): { references: number; hasCharacter: boolean } {
  const state = getChatContextState(sessionId);
  return { references: sampleRefs(state).length, hasCharacter: characterRefs(state).length > 0 };
}

// Файл с диска: загрузка в рабочую папку, затем референс; отказ загрузки — исключение вызывающему
export async function uploadSampleRef(scope: string, file: File): Promise<Record<string, unknown>> {
  const { uploadId } = await imageEditorApi().uploadSample(scope, file, file.name);
  return { upload: uploadId };
}

export async function addUploadSamples(scope: string, sessionId: string, files: File[], role: string) {
  for (const file of files) {
    await attachRef(sessionId, { kind: IMAGE_KIND, ref: await uploadSampleRef(scope, file), role });
  }
}

export const addProjectSample = (sessionId: string, path: string, role: string) =>
  attachRef(sessionId, { kind: FILE_KIND, ref: { path }, role });

// Роль образца меняется заменой: тот же объект под другой ролью — отдельная запись
export async function changeSampleRole(sessionId: string, item: ChatContextRef, role: string) {
  if (await attachRef(sessionId, { kind: item.kind, ref: item.ref, role }) === 'ok') await detachRef(sessionId, item.id);
}

// Персонаж один: прежний снимается, новый встаёт ролью «персонаж»; null — просто снять
export async function setCharacterRef(sessionId: string, slug: string | null) {
  for (const prev of characterRefs(getChatContextState(sessionId))) {
    if (prev.ref.slug !== slug) await detachRef(sessionId, prev.id);
  }
  if (slug && characterSlugOf(getChatContextState(sessionId)) !== slug) {
    await attachRef(sessionId, { kind: CHARACTER_KIND, ref: { slug }, role: 'character' });
  }
}

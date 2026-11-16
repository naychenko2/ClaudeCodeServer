// Роли референсов основного объекта «звук» (ADR-023 §1, Дополнение 3): зеркало AcceptedRefs AudioContextKind
// на бэкенде — роль принимает он, чужую отвечает 400 role_not_accepted. Голос из библиотеки входит единственной
// ролью «голос» (одна роль — «В контекст» без вопроса); звук ленты и файл проекта — образцом голоса или куском
// склейки. Файл «Личного» (личный вид ссылки, Ф-9) — только куском склейки: образцом голоса он не бывает.
// Остальные виды основной звук не берёт.

import type { ContextRole } from 'aihome_shell/kit';
import { AUDIO_KIND } from './state';

// Личный вид ссылки: файл «Личного» в контексте личного чата (Ф-9). Путь — от корня «Личного»
// (как его отдаёт дерево api/personal/chats/{id}/files/tree).
export const PERSONAL_FILE_KIND = 'personal-file';

export const VOICE_ROLES: readonly ContextRole[] = [{ role: 'voice', label: 'Как голос' }];
export const SOUND_ROLES: readonly ContextRole[] = [
  { role: 'reference', label: 'Как образец голоса' },
  { role: 'piece', label: 'Как кусок склейки' },
];
// Личный файл — только кусок склейки: роль «образец голоса» бэкенд личному файлу не даёт (AudioContextKind:
// в AcceptedRefs роль «Кусок» знает personal-file, «Образец» — нет).
export const PERSONAL_FILE_ROLES: readonly ContextRole[] = [{ role: 'piece', label: 'Как кусок склейки' }];

export function audioRefRoles(candidateKind: string): readonly ContextRole[] {
  if (candidateKind === 'audio-voice') return VOICE_ROLES;
  if (candidateKind === PERSONAL_FILE_KIND) return PERSONAL_FILE_ROLES;
  if (candidateKind === 'audio' || candidateKind === 'project-file') return SOUND_ROLES;
  return [];
}

// Роли личного файла при основном объекте: кусок склейки принимает только звуковой основной объект
// (AudioContextKind.AcceptedRefs отвечает ролями лишь при primary.Kind === «audio»). При другом основном
// объекте роль не предлагается вовсе — запрос с ней сервер отверг бы (400 role_not_accepted).
export function personalFileRoles(primaryKind: string | null | undefined): readonly ContextRole[] {
  return primaryKind === AUDIO_KIND ? PERSONAL_FILE_ROLES : [];
}

// Ссылка-кандидат личного файла: вид «personal-file» и путь от корня «Личного» (как его отдаёт
// дерево api/personal/chats/{id}/files/tree). Проектный вид в личном чате бэкенд отвергает.
export const personalFileCandidate = (path: string): { kind: string; ref: { path: string } } =>
  ({ kind: PERSONAL_FILE_KIND, ref: { path } });

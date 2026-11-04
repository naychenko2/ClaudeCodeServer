// Чистая логика вкладки «Участники» в настройках проекта (S1-F3): сводка в заголовке,
// тексты отказов формы, роль в подписи. Без React — проверяется юнит-тестами.

import { MEMBER_REFUSAL } from '../../../types';
import type { ProjectMember, ProjectMemberRole } from '../../../types';

export const ROLE_LABEL: Record<ProjectMemberRole, string> = {
  editor: 'Редактор',
  reader: 'Читатель',
};

// «1 человек», «2 человека», «5 человек»
export function peopleCount(n: number): string {
  const m10 = n % 10;
  const m100 = n % 100;
  const word = m10 === 1 && m100 !== 11 ? 'человек'
    : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? 'человека' : 'человек';
  return `${n} ${word}`;
}

// Сводка в заголовке аккордеона: «только вы» (нейтрально) / «3 человека» (ok)
export function membersSummary(count: number): { text: string; tone: 'neutral' | 'ok' } {
  return count === 0
    ? { text: 'только вы', tone: 'neutral' }
    : { text: peopleCount(count), tone: 'ok' };
}

// Код отказа из тела ошибки (lib/offline кладёт тело целиком в err.body)
export function memberRefusalCode(err: unknown): string | null {
  const body = (err as { body?: unknown } | null | undefined)?.body;
  const code = body && typeof body === 'object' ? (body as { code?: unknown }).code : null;
  return typeof code === 'string' ? code : null;
}

// Ошибка без ответа сервера (нет связи, таймаут): у HTTP-отказа всегда есть status
export function isNetworkFailure(err: unknown): boolean {
  return (err as { status?: unknown } | null | undefined)?.status === undefined;
}

// Текст под полем «Имя учётки» по ответу на приглашение. Введённое не стирается, поэтому
// каждый текст говорит, что делать дальше. existing — участники на момент отказа: для
// «уже в проекте» подпись называет его роль
export function inviteErrorText(err: unknown, username: string, existing: ProjectMember[]): string {
  const name = username.trim();
  switch (memberRefusalCode(err)) {
    case MEMBER_REFUSAL.UserNotFound:
      return `Не нашли учётку «${name}». Проверьте написание — нужно точное имя.`;
    case MEMBER_REFUSAL.AlreadyMember: {
      const found = existing.find(m => m.username?.toLowerCase() === name.toLowerCase());
      return found
        ? `«${name}» уже в проекте (${ROLE_LABEL[found.role].toLowerCase()}). Роль можно сменить в списке ниже.`
        : `«${name}» уже в проекте. Роль можно сменить в списке ниже.`;
    }
    case MEMBER_REFUSAL.Self:
      return 'Это вы — себя приглашать не нужно.';
    case MEMBER_REFUSAL.FlagDisabled:
      return `Попросите ${name} включить «Общие проекты» в «Экспериментах», потом добавьте снова.`;
    case MEMBER_REFUSAL.LocalProject:
      return 'Проект хранится на вашем устройстве, поэтому открыть его другим людям нельзя.';
    case MEMBER_REFUSAL.SwitchDisabled:
      return 'Общие проекты сейчас выключены на сервере. Обратитесь к администратору.';
    default:
      if (isNetworkFailure(err)) return 'Нет связи с сервером. Ничего не потеряно — попробуйте ещё раз.';
      return err instanceof Error && err.message ? err.message : 'Не получилось добавить. Попробуйте ещё раз.';
  }
}

// Текст сбоя смены роли / удаления / выхода (подпись строки или плашка секции)
export function memberActionErrorText(err: unknown): string {
  if (isNetworkFailure(err)) return 'Нет связи с сервером. Ничего не изменилось — попробуйте ещё раз.';
  return err instanceof Error && err.message ? err.message : 'Не получилось. Ничего не изменилось.';
}

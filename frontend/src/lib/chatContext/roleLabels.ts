// Подписи ролей референсов для списка «Подключено к ходу». Роль в DTO — непрозрачный ключ
// вида; неизвестный ключ показываем как есть, а не прячем.
const ROLE_LABELS: Readonly<Record<string, string>> = {
  style: 'образец стиля',
  object: 'объект',
  face: 'лицо',
  char: 'персонаж',
  character: 'персонаж',
  frameA: 'кадр A',
  frameB: 'кадр B',
  voice: 'голос',
};

export const roleLabel = (role: string | null): string | null =>
  role === null ? null : (ROLE_LABELS[role] ?? role);

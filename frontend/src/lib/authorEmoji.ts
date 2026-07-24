// Иконка-роль автора — чтобы «на глаз» различать, кто сделал (ненавязчиво, тегом).
// Известные закреплены по имени; новые авторы получают роль из пула детерминированно
// по имени (стабильно и без правки кода) — так у любого нового будет своя иконка.
// Общий модуль: одна и та же иконка у автора и в «Что нового», и в списке коммитов.
export const AUTHOR_EMOJI: Record<string, string> = {
  'Григорий': '🧑‍💼',
  'Андрей': '👨‍💻',
};

const ROLE_POOL = ['🧑‍🚀', '🥷', '🧑‍🎨', '🧑‍🍳', '🕵️', '🧑‍🏭', '🧑‍🌾', '🧑‍⚕️', '🧑‍🏫', '🧑‍✈️'];

// Каноничное имя по e-mail — зеркало Changelog:AuthorAliases (appsettings.json).
// В git-логе стоит локальное имя автора («depeche81», «Найченко Андрей»), а
// продуктовая история показывает человеческое («Григорий», «Андрей»): без этой
// таблицы у одного человека были бы РАЗНЫЕ иконки в «Что нового» и в коммитах.
const EMAIL_ALIASES: Record<string, string> = {
  'depeche81@msn.com': 'Григорий',
  'andrey@naychenko.ru': 'Андрей',
  'anaychenko@ya.ru': 'Андрей',
};

// Имя автора для показа: по возможности каноничное (по e-mail), иначе как в git
export function authorName(name: string, email?: string | null): string {
  const alias = email ? EMAIL_ALIASES[email.trim().toLowerCase()] : undefined;
  return alias ?? name;
}

export function authorEmoji(name: string): string {
  if (AUTHOR_EMOJI[name]) return AUTHOR_EMOJI[name];
  let h = 0;
  for (const ch of name) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
  return ROLE_POOL[h % ROLE_POOL.length];
}

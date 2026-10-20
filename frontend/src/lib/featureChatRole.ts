// Роль чата в фиче (FeatureChatLink.kind) → русское слово. ЕДИНЫЙ словарь для
// страницы фичи (featurePageBody) и плашки (FeatureBanner): раньше было два
// независимых словаря одних и тех же слов (CHAT_ROLE_LABEL / KIND_LABEL) — при новом
// виде связи от бэка они разошлись бы (плашка в чате — код, страница фичи — слово).
//
// ADR-031 §1: бэк вид `key` в chats НЕ отдаёт — ключевой чат приходит как
// curator/orchestrator (определяется по keySessionId, не по kind), поэтому `key`
// в словаре нет (мёртвая ветка). Неизвестный kind (бэк расширил контракт) — как есть,
// не роняем строку.
export const CHAT_ROLE_LABEL: Record<string, string> = {
  curator: 'куратор',
  orchestrator: 'оркестратор',
  executor: 'исполнитель',
};

// kind → слово; неизвестный kind — как есть (не роняем строку).
export function chatRoleLabel(kind: string): string {
  return CHAT_ROLE_LABEL[kind] ?? kind;
}

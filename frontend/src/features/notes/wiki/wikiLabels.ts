// Подписи фичи «Вики»: словари статуса и типа страницы, тексты отказов контракта,
// префикс раздела. Раньше они жили в WikiTab, а страница импортировала их оттуда при том,
// что WikiTab рендерит страницу, — импортный цикл (хвост ревью S5b). Здесь только
// словари: ни компонентов, ни стора. Их читают каталог (WikiTab), страница (WikiPageView),
// «Входящие» (WikiInbox) и карточка пакета (WikiPackageView).
import { type BadgeTone } from '../../../components/ui/Badge';
import { parseSectionKey, type WikiError, type WikiSectionDto } from '../../../lib/wiki';

// Статусы страницы — их считает код, не модель (ADR-035 §3, WikiFrontmatter.StatusValues)
export const PAGE_STATUS: Record<string, { tone: BadgeTone; text: string; title: string }> = {
  active: { tone: 'success', text: 'актуальна', title: 'Утверждения подтверждены источниками, противоречий нет' },
  stale: { tone: 'neutral', text: 'устарела', title: 'Источник новее опроверг утверждение или страница не менялась 90 дней' },
  disputed: { tone: 'warning', text: 'спорная', title: 'Есть открытые противоречия — см. раздел на странице' },
  drift: { tone: 'danger', text: 'дрейф', title: 'Файл изменён мимо Хранителя' },
};

// Типы страниц — WikiFrontmatter.TypeValues
export const PAGE_TYPE: Record<string, string> = {
  overview: 'Обзор', topic: 'Тема', entity: 'Сущность', concept: 'Понятие',
  decision: 'Решение', chronicle: 'Хроника', source: 'Источник',
};

// Отказы контракта (WikiErrorCode, S2f) — понятным русским текстом; 401/404 приходят без
// тела и без кода, их отличаем по статусу. Формулировки — черновые (тексты фичи ведёт Софья).
export function wikiErrorText(e: WikiError): string {
  switch (e.code) {
    case 'wiki_foreign': return 'Нет доступа';
    case 'wiki_section_key': return 'Раздел не найден';
    case 'wiki_page_key': return 'Страница не найдена';
    case 'wiki_project_missing': return 'Проект не найден';
    case 'wiki_section_off': return 'Вики локальных проектов — отдельной фичей';
    case 'wiki_sphere_reserved': return 'Раздел сферы — следующая фича';
    case 'wiki_refused': return 'Правку нельзя применить к этому состоянию страницы';
    case 'wiki_drift': return 'Страницу изменили мимо Хранителя — сначала разберите дрейф';
    case 'wiki_not_orphan': return 'Удалить можно только раздел удалённого проекта';
    // Журнал (S7c-2): origin-фильтр вне закрытого перечня (400); источник отката не найден или чужой (404)
    case 'wiki_origin_key': return 'Источник не из списка';
    case 'wiki_retract_source': return 'Такого источника в твоей вики нет';
    // Разбор по требованию (S7b-0): чат (analyze / analyze-status) и заметка (ingest/note).
    // Точную причину бэкенд несёт в message отказа; здесь — понятный текст по коду.
    case 'not_found': return 'Ничего не нашлось';
    case 'task_execution': return 'Чаты исполнения задач не разбираются: их итоги уже источники';
    case 'marked_skip': return 'Чат помечен «не разбирать» — сними пометку';
    case 'personal_section': return 'Личный раздел — следующее (Ф2)';
    case 'wiki_not_initialized': return 'Твоя вики ещё не заведена';
    case 'packages_limit': return 'Открытых пакетов слишком много: разберите «Входящие», потом повторяй заявку';
    case 'wiki_note_id': return 'Нужен id заметки';
  }
  if (e.status === 401 || e.status === 403) return 'Нет доступа';
  if (e.status === 404) return 'Не найдено';
  if (e.status === 409) return 'Действие отказано';
  return 'Не удалось выполнить действие';
}

// Префикс раздела — один форматтер на всю фичу (тот же вид, что у бэка в выдаче notes_*):
// «Проекты / AI Home», «Сферы / Банк», «Общее», «Личное»
export function sectionPrefix(s: WikiSectionDto): string {
  const ref = parseSectionKey(s.key);
  if (!ref) return '—';
  if (ref.kind === 'common') return 'Общее';
  if (ref.kind === 'personal') return 'Личное';
  if (ref.kind === 'sphere') return `Сферы / ${s.name ?? ''}`;
  return `Проекты / ${s.name ?? '—'}`;
}

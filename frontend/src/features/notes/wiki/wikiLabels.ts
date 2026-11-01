// Подписи фичи «Вики»: словари статуса и типа страницы, подписи источников, согласование
// по числу, тексты отказов контракта, префикс раздела. Раньше они жили в WikiTab (а страница
// импортировала их оттуда при том, что WikiTab рендерит страницу, — импортный цикл, хвост
// ревью S5b); countWord — в WikiChatActions, ORIGIN_META — в WikiInbox: тот же согласователь
// и те же подписи нужны «Журналу», второй копии помощника в фиче не нужно (W4-3, хвосты
// R-T-1 H3/H4, R-S7c-1 (5)). Здесь только словари и чистые функции: ни компонентов, ни
// стора, ни импорта чего-либо из фичи. Их читают каталог (WikiTab), страница (WikiPageView),
// «Входящие» (WikiInbox), карточка пакета (WikiPackageView), «Журнал» и действия чата
// (WikiChatActions) — часть по прежним путям, через реэкспорт старых мест; их перевод
// на этот файл — отдельный шаг (W4-4).
import { type BadgeTone } from '../../../components/ui/Badge';
import { Brain, FileText, GitCommit, Link2, ListChecks, MessageSquare, StickyNote } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { parseSectionKey, type WikiError, type WikiSectionDto } from 'aihome_shell/kit';

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

// ─── Согласование по числу ───

// Согласование по числу (тот же приём, что stepWord в FeatureProdCardsView):
// 1 / 2–4 / 5+; 11–14 — как 5+. Коротое причастие при «1» согласуется с существительным,
// при остальних — средний род: так оно читается с числительным-подметом.
export function isOne(n: number): boolean {
  const m10 = n % 10, m100 = n % 100;
  return m10 === 1 && m100 !== 11;
}

// счетное слово по числу: 1 пакет / 2 пакета / 5 пакетов; 11–14 — как 5+, 21 — как 1,
// 22 — как 2. Нужен «Журналу» (форма «1 применённая запись» в модалке отката) и действиям
// чата («откатан/откатано») — второй копии согласователя в фиче нет (доработка H5-чисел).
export function countWord(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10, m100 = n % 100;
  const w = (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) ? few : many;
  return `${n} ${isOne(n) ? one : w}`;
}

// ─── Подписи источников ───

// origin источника — закрытый перечень бэка (WikiGate.OriginValues). Подпись и икона —
// только для них; нераспознанный origin показывается как есть, не выдумывая тип.
// genitive — родительный падеж с указательным местоимением («из этого чата»): нужен там,
// где фраза требует падежа, — «Откатить всё из …» в «Журнале» (доработка H4). Подстановка
// именительного label в такую фразу даёт «из этого чат» — хвост ревью R-S7c-2.
export const ORIGIN_META: Record<string, { label: string; genitive: string; icon: LucideIcon }> = {
  session: { label: 'чат', genitive: 'из этого чата', icon: MessageSquare },
  note: { label: 'заметка', genitive: 'из этой заметки', icon: StickyNote },
  file: { label: 'файл', genitive: 'из этого файла', icon: FileText },
  url: { label: 'ссылка', genitive: 'из этой ссылки', icon: Link2 },
  task: { label: 'задача', genitive: 'из этой задачи', icon: ListChecks },
  dossier: { label: 'досье', genitive: 'из этого досье', icon: GitCommit },
  memory: { label: 'память', genitive: 'из этой памяти', icon: Brain },
};

// Отказ разбора личной заметки (доработка H5): бэкенд отвечает «личный раздел — с Ф2» —
// 409 personal_section (разбор чата) или 200 {outcome:'rejected', reason:…} (заявка
// заметки, noteIngestText в WikiChatActions). Оба показывают этот текст: внутренняя ссылка
// бэка на Ф2 без пути решения человеку не говорит ничего.
export const personalSectionRefusalText = 'Личный раздел пока не разбирается — разбор доступен для заметок проекта.';

// Контекст вызова кода not_found: один код значит два разных отказа, и текст выбирается
// по контексту, а не по коду (хвост ревью R-S7b-0 H4). Разбор чата (analyze /
// analyze-status): чужой или несуществующий чат (WikiChatIngestService.cs:95-100). Заявка
// заметки (ingest/note): заметка не стала источником — чужая, нет такой, не источник
// (WikiController.cs:290-291). Остальные кода контекста не требуют: их текст определяется
// кодом. Без контекста — нейтральный текст: вызывающий не знает, какой отказ перед ним.
export type NotFoundContext = 'chat' | 'note';

// Отказы контракта (WikiErrorCode, S2f) — понятным русским текстом; 401/404 приходят без
// тела и без кода, их отличаем по статусу. Формулировки — черновые (тексты фичи ведёт Софья).
export function wikiErrorText(e: WikiError, ctx?: NotFoundContext): string {
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
    // Точную причину бэкенд несёт в message отказа; здесь — понятный текст по коду,
    // not_found — по контексту вызова (см. NotFoundContext).
    case 'not_found':
      if (ctx === 'chat') return 'Чат не найден';
      if (ctx === 'note') return 'Заметка не стала источником';
      return 'Ничего не нашлось';
    case 'task_execution': return 'Чаты исполнения задач не разбираются: их итоги уже источники';
    case 'marked_skip': return 'Чат помечен «не разбирать» — сними пометку';
    case 'personal_section': return personalSectionRefusalText;
    case 'wiki_not_initialized': return 'Твоя вики ещё не заведена';
    case 'packages_limit': return 'Открытых пакетов слишком много: разберите «Входящие», потом повторяй заявку';
    case 'wiki_note_id': return 'Нужен id заметки';
    // Здоровье (S3-3) и класс операции (хвост R-S7c-2): общие тексты по коду. У «Здоровья»
    // свой текст прогона (healthErrorText) — он точнее для кнопки линта и остаётся там.
    case 'wiki_llm_wiki_off': return 'Вики выключена в настройках';
    case 'wiki_class_key': return 'Неизвестный класс';
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

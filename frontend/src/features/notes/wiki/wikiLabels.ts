// Подписи фичи «Вики»: словари статуса и типа страницы, подписи источников, согласование
// по числу, тексты отказов контракта, префикс раздела. Раньше они жили в WikiTab (а страница
// импортировала их оттуда при том, что WikiTab рендерит страницу, — импортный цикл, хвост
// ревью S5b); countWord жил в WikiChatActions, ORIGIN_META — в WikiInbox: тот же согласователь
// и те же подписи нужны «Журналу», второй копии помощника в фиче не нужно (W4-3, хвосты
// R-T-1 H3/H4, R-S7c-1 (5)). Здесь только словари и чистые функции: ни компонентов, ни
// стора, ни импорта чего-либо из фичи. Их читают каталог (WikiTab), страница (WikiPageView),
// «Входящие» (WikiInbox), карточка пакета (WikiPackageView), «Журнал» и действия чата
// (WikiChatActions) — все напрямую отсюда, реэкспортов старых мест нет (W4-4).
import { type BadgeTone } from '../../../components/ui/Badge';
import { Brain, FileText, GitCommit, Link2, ListChecks, MessageSquare, StickyNote } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { parseSectionKey, type WikiError, type WikiRetractDto, type WikiSectionDto } from 'aihome_shell/kit';

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

// Итог отката по источнику — текстом; один на «Журнал» (тост) и действия чата. Контракт
// WikiRetractDto (WikiPackagesController.cs): revertedPackages — id побайтно откатанных
// пакетов; unlinkedPackages — id пакетов, обработанных снятием сносок; remainingPackages —
// id пакетов, чьи страницы не удалось обработать (причины — в failed); removedChronicles —
// снятые страницы хроники. Всё, кроме пометки, — пакеты и хроники, не страницы: счёт ведётся
// ими. subject — кому поставлена пометка «не разбирать» («чат» / «источник»).
export function retractResultText(r: WikiRetractDto, subject = 'источник'): string {
  const parts = [
    r.revertedPackages.length ? `${countWord(r.revertedPackages.length, 'пакет', 'пакета', 'пакетов')} ${isOne(r.revertedPackages.length) ? 'откатан' : 'откатано'}` : '',
    r.unlinkedPackages.length ? `${countWord(r.unlinkedPackages.length, 'пакет', 'пакета', 'пакетов')} ${isOne(r.unlinkedPackages.length) ? 'очищен' : 'очищено'} от сносок` : '',
    r.removedChronicles.length ? `${countWord(r.removedChronicles.length, 'хроника', 'хроники', 'хроник')} ${isOne(r.removedChronicles.length) ? 'снята' : 'снято'}` : '',
    r.remainingPackages.length ? `${countWord(r.remainingPackages.length, 'пакет', 'пакета', 'пакетов')} ${isOne(r.remainingPackages.length) ? 'остался' : 'осталось'} с необработанными страницами` : '',
    r.markedSkip ? `${subject} помечен «не разбирать»` : '',
  ].filter(Boolean);
  const failed = r.failed.length ? ` Частично: ${r.failed.join('; ')}` : '';
  return `${parts.join(', ') || 'ничего не изменилось'}.${failed}`;
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
    // Список чатов разбора (GET /wiki/chats): файл разбора чатов не читается (R-W4-1)
    case 'wiki_triage_invalid': return 'Файл разбора чатов не читается';
    // Правка страницы человеком (F4a)
    case 'wiki_page_changed': return 'Страница изменилась, пока вы правили';
    case 'wiki_unified_off': return 'Правка страниц сейчас выключена';
    case 'wiki_edit_invalid': return 'Страницу нельзя сохранить в таком виде';
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

// ─── Правка страницы (F4a) ───

// Заголовки причин отказа гейта по кодам issues[] (ADR-038, SV1-Г); неизвестный код — без
// заголовка, остаётся текст бэка
export const EDIT_ISSUE_TITLE: Record<string, string> = {
  wiki_link_sideways: 'Ссылка ведёт в соседний раздел',
  wiki_link_unknown: 'Ссылка на страницу, которой нет',
  wiki_link_prefix: 'Ссылка с неверным префиксом раздела',
  wiki_header_field: 'В шапке правятся только summary, aliases и owns',
  wiki_title_change: 'Заголовок страницы не правится',
  wiki_notes_footnote: 'В секции «Заметки» не бывает сносок',
  wiki_parse: 'Текст не удалось разобрать как страницу',
};

// Строка violations начинается с «путь»: — префикс человеку не нужен
export function stripViolationPrefix(v: string): string {
  return v.replace(/^«[^»]*»:\s*/, '');
}

export const EDIT_TEXT = {
  edit: 'Править',
  save: 'Сохранить',
  cancel: 'Отмена',
  remove: 'Убрать',
  restore: 'Вернуть',
  saved: 'Сохранено',
  unchanged: 'Ничего не изменилось',
  removeAsk: 'Убрать утверждение?',
  removeHistory: 'Оно уйдёт в «Историю» страницы, удалено не будет.',
  aiWarn: 'Это формулировка ИИ. После сохранения прежняя уйдёт в «Историю» страницы, а утверждение станет вашим.',
  blank: 'Пустое утверждение не сохранить — уберите его кнопкой «Убрать».',
  offTitle: 'Правка страниц выключена',
  offText: 'Ваши изменения не сохранены. Включите «Заметки и Вики» в настройках и повторите.',
  conflictTitle: 'Страница изменилась, пока вы правили',
  conflictText: 'ИИ дописал её, пока вы правили. Ваш текст не потерян и ничего не перезаписано: выберите, что оставить.',
  conflictBadge: 'Нужно решить',
  conflictBoth: 'изменили и вы, и ИИ',
  conflictOnlyMine: 'правили только вы',
  conflictOnlyAi: 'правил только ИИ',
  conflictSame: 'вы и ИИ написали одно и то же',
  conflictMine: 'Моё',
  conflictAi: 'ИИ',
  conflictWas: 'Было, когда вы начали',
  conflictRemoved: 'раздела здесь нет',
  takeMine: 'Взять мою версию поверх',
  takeAi: 'Взять версию ИИ',
  keepEditing: 'Продолжить правку',
  closeNoSave: 'Закрыть без сохранения',
  conflictGone: 'Страницы больше нет — сохранить правку некуда.',
  invalidTitle: 'Страницу нельзя сохранить',
  invalidKeep: 'Ваша правка осталась в редакторе — поправьте и сохраните ещё раз.',
  failTitle: 'Не удалось сохранить',
  failText: 'Ваша правка осталась в редакторе. Проверьте соединение и повторите.',
} as const;

// ─── Метки «ИИ заменил / оспаривает» и «Вернуть мою» (F3b; тексты — записка notes-wiki-unified-v1) ───

export const OVERRIDE_TEXT = {
  replacedTitle: 'ИИ заменил вашу формулировку',
  replacedText: 'Прежняя — в «Истории».',
  replacedTag: 'ИИ заменил вашу формулировку',
  disputedTitle: 'ИИ оспаривает ваше утверждение',
  disputedText: 'Ваш текст на месте. Довод ИИ — в «Противоречиях»: пока вы не решите, автоматика страницу не трогает.',
  disputedTag: 'ИИ оспаривает',
  restore: 'Вернуть мою',
  restored: 'Вернули вашу формулировку. 7 дней ИИ не меняет ваше на этой странице сам.',
  historyCaption: 'Ваша формулировка, заменена ИИ',
  changed: 'Страница изменилась, пока вы возвращали. Показали свежую: проверьте и повторите.',
  failed: 'Не удалось вернуть формулировку. Страница осталась как есть — повторите.',
  dotTitle: 'ИИ заменил ваши формулировки',
} as const;

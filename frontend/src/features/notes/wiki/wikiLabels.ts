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
import { parseSectionKey, type WikiError, type WikiLastNight, type WikiNightlyOutcome, type WikiRetractDto, type WikiSectionDto } from 'aihome_shell/kit';

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
  }
  if (e.status === 401 || e.status === 403) return 'Нет доступа';
  if (e.status === 404) return 'Не найдено';
  if (e.status === 409) return 'Действие отказано';
  return 'Не удалось выполнить действие';
}

// ─── «Ночной разбор»: итог ночи и его тексты (F2) ───

// Тексты — по записке llm-wiki-v2-proposal.md «Срез Ф2 02.11» (принята Андреем 02.11):
// формулировки слово в слово, динамические — только числа из lastNight (контракт N7c).
// Времён остановки в тексты нет: date — DateOnly 'yyyy-MM-dd' (WikiNightlyService.
// FormatDate), поля времени остановки в контракте нет — «Закончилась в 05:12» из макета
// придумывать нечем. Рендер и состояния — у WikiNightlyBlock.

// Потолок открытых пакетов — зеркало бэкового WikiPackage.MaxOpenPackages: им же
// IsValidNightly мерит оба лимита пакетов и потолок токенов (5 000 000). Нужен и для
// локальной валидации полей (тот же потолок, что на бэке), и для текста open_limit:
// ночь встаёт ровно на этот предел (CountOpen < 20, план N7b).
export const NIGHTLY_MAX_PACKAGES = 20;
export const NIGHTLY_MAX_TOKENS = 5_000_000;

// Пустой итог (lastNight — null: файла нет или он не читается): заголовок карточки и
// текст по состоянию тумблера. Для включённого — окно динамическое: то, что человек
// видит в полях прямо сейчас (в макете — пример значения 03:00–06:00).
export const NIGHTLY_NONE_TITLE = 'Ночного разбора ещё не было';

export function nightlyNoneText(enabled: boolean, windowFrom: string, windowTo: string): string {
  return enabled
    ? `Первый пройдёт сегодня с ${windowFrom} до ${windowTo}. Утром придёт уведомление: что применилось само и что ждёт вас.`
    : 'Включите разбор — первая ночь пройдёт в ближайшее окно, утром придёт уведомление с итогом.';
}

// Строка под карточкой completed (записка: «Закончилась в 05:12 — разобрано всё
// отобранное» — без времени, его в контракте нет)
export const NIGHTLY_COMPLETED_LINE = 'Разобрано всё отобранное.';

// Предупреждение над карточкой для остальных исходов. title — только у warning-исходов
// (у info по макету заголовка нет). completed и неизвестный исход — null: карточка без
// предупреждения; неизвестный outcome — будущее значение контракта, проходит как есть
// (тот же приём, что у WikiChatAnalyzeOutcome). Число «остановились на N-м чате» —
// analyzed: дальше очереди ночь не пошла.
export function nightOutcomeNotice(
  outcome: WikiNightlyOutcome, last: WikiLastNight,
): { tone: 'warning' | 'info'; title: string | null; text: string } | null {
  switch (outcome) {
    case 'budget': {
      const rest = Math.max(0, last.selected - last.analyzed);
      return {
        tone: 'warning',
        title: 'Не хватило бюджета на всё',
        text: `Разобрали ${countWord(last.analyzed, 'чат', 'чата', 'чатов')} из ${last.selected} отобранных. Остальные ${rest} разберём следующей ночью — сначала они. Если так каждую ночь, поднимите бюджет.`,
      };
    }
    case 'open_limit':
      return {
        tone: 'warning',
        title: `Остановились: во «Входящих» ${NIGHTLY_MAX_PACKAGES} пакетов ждут вашего решения`,
        text: `Больше ${NIGHTLY_MAX_PACKAGES} спорных пакетов сразу Хранитель не копит — иначе их не разобрать. Решите часть, и следующая ночь продолжит с того же места.`,
      };
    case 'window_closed':
      return { tone: 'info', title: null, text: 'Окно закончилось раньше, чем разбор: остальное следующей ночью.' };
    case 'disabled_midway':
      return { tone: 'info', title: null, text: `Вы выключили разбор посреди ночи — остановились на ${last.analyzed}-м чате.` };
    case 'error':
      return { tone: 'info', title: null, text: 'Ночь прервалась из-за сбоя. Повторим следующей ночью.' };
    default:
      return null;
  }
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

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

// Отказ разбора чата (исход rejected и любой неизвестный исход) — по-человечески. reason —
// сырая строка бэка («пустой ответ модели в окне m11-m381, проход A»): окна, проходы и прочее
// внутреннее человеку не показываем никогда. Известна одна причина; чужая — без хвоста:
// смысла не выдумываем, сырая строка уходит только в title пункта.
export function analyzeRefusalText(reason: string | null): string {
  const base = 'Не удалось разобрать';
  if (reason && reason.toLowerCase().includes('пустой ответ')) return `${base}: модель не ответила`;
  return base;
}

// Контекст вызова кода not_found: один код значит два разных отказа, и текст выбирается
// по контексту, а не по коду (хвост ревью R-S7b-0 H4). Разбор чата (analyze /
// analyze-status): чужой или несуществующий чат (WikiChatIngestService.cs:95-100). Заявка
// заметки (ingest/note): заметка не стала источником — чужая, нет такой, не источник
// (WikiController.cs:290-291). Остальные кода контекста не требуют: их текст определяется
// кодом. Без контекста — нейтральный текст: вызывающий не знает, какой отказ перед ним.
export type NotFoundContext = 'chat' | 'note';

// Отказы «правка недоступна по устройству раздела» (локальный проект, раздел сферы) —
// одна формулировка на оба места: её даёт и отказ контракта (коды ниже), и плашка
// нечитаемой страницы (READONLY_TEXT, F3a-qa) — второй копии текста в фиче нет
export const SECTION_OFF_TEXT = 'Вики локальных проектов — отдельной фичей';
export const SPHERE_RESERVED_TEXT = 'Раздел сферы — следующая фича';

// «Вики выключена в настройках» — один текст на два отказа: чтения настроек (эта таблица) и
// ручного запуска ночи (nightlyRunRefusalText): второй копии строки в фиче нет
const WIKI_OFF_TEXT = 'Вики выключена в настройках';

// Отказы контракта (WikiErrorCode, S2f) — понятным русским текстом; 401/404 приходят без
// тела и без кода, их отличаем по статусу. Формулировки — черновые (тексты фичи ведёт Софья).
export function wikiErrorText(e: WikiError, ctx?: NotFoundContext): string {
  switch (e.code) {
    case 'wiki_foreign': return 'Нет доступа';
    case 'wiki_section_key': return 'Раздел не найден';
    case 'wiki_page_key': return 'Страница не найдена';
    case 'wiki_project_missing': return 'Проект не найден';
    case 'wiki_section_off': return SECTION_OFF_TEXT;
    case 'wiki_sphere_reserved': return SPHERE_RESERVED_TEXT;
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
    // С LI-2 analyze/ingest/transfer сами заводят вики владельца: этот 409 из действия
    // человека не приходит, код остаётся как ответ сервисного слоя (MCP/вторая линия
    // защиты). Текст нейтральный — без просьбы «завести»: кнопки «Завести вики» нет (LI-3).
    case 'wiki_not_initialized': return 'Вики этого владельца не заведена';
    case 'packages_limit': return 'Открытых пакетов слишком много: разберите «Входящие», потом повторяй заявку';
    case 'wiki_note_id': return 'Нужен id заметки';
    // Здоровье (S3-3) и класс операции (хвост R-S7c-2): общие тексты по коду. У «Здоровья»
    // свой текст прогона (healthErrorText) — он точнее для кнопки линта и остаётся там.
    case 'wiki_llm_wiki_off': return WIKI_OFF_TEXT;
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
// видит в полях прямо сейчас (в макете — пример значения 03:00–06:00). «Сегодня» из
// текста записки убрано (хвост R-F2 (1)): если разбор включили после конца окна, первая
// ночь — завтра, а серверного часа у фронта нет (окно — время сервера); «ближайшая
// ночь» верна в обоих случаях.
export const NIGHTLY_NONE_TITLE = 'Ночного разбора ещё не было';

export function nightlyNoneText(enabled: boolean, windowFrom: string, windowTo: string): string {
  return enabled
    ? `Первый пройдёт ближайшей ночью с ${windowFrom} до ${windowTo}. Утром придёт уведомление: что применилось само и что ждёт вас.`
    : 'Включите разбор — первая ночь пройдёт в ближайшее окно, утром придёт уведомление с итогом.';
}

// Строка под карточкой completed (записка: «Закончилась в 05:12 — разобрано всё
// отобранное» — без времени, его в контракте нет)
export const NIGHTLY_COMPLETED_LINE = 'Разобрано всё отобранное.';

// Предупреждение над карточкой для остальных исходов. title — только у warning-исходов
// (у info по макету заголовка нет). completed и неизвестный исход — null: карточка без
// предупреждения; неизвестный outcome — будущее значение контракта, проходит как есть
// (тот же приём, что у WikiChatAnalyzeOutcome).
//
// Формулировки причины остановки — по хвостам R-F2 (2)–(3), язык пользы: исходов
// «лимит пакетов» и «упор в бюджет» контракт не различает — outcome=budget ставят и
// лимит пакетов за ночь, и остаток бюджета (WikiNightlyService.cs:332,373,402), поэтому
// текст называет причину одним «лимитом ночи» и указывает оба поля, где лимиты заданы;
// «поднимите бюджет» из записки убрано: при упоре в лимит пакетов оно не применимо
// (потолок 20 общий с «Входящими»). Лимит раздела ночь не останавливает — такая ночь
// остаётся completed (.:398–401), отдельного текста у него нет. disabled_midway при
// analyzed=0 (CheckStop зовётся перед каждым чатом, .:330) больше не «на 0-м чате»:
// analyzed — законченные разборки, недочитанного чата контрактом нет.
export function nightOutcomeNotice(
  outcome: WikiNightlyOutcome, last: WikiLastNight,
): { tone: 'warning' | 'info'; title: string | null; text: string } | null {
  switch (outcome) {
    case 'budget': {
      const rest = Math.max(0, last.selected - last.analyzed);
      return {
        tone: 'warning',
        title: 'Ночь остановилась на своём лимите',
        text: `Разобрали ${countWord(last.analyzed, 'чат', 'чата', 'чатов')} из ${last.selected} отобранных. Остальные ${rest} разберём следующей ночью — сначала они. Лимиты заданы в «Пакетах за ночь» и «Бюджете за ночь» — если так каждую ночь, проверьте их.`,
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
      return {
        tone: 'info',
        title: null,
        text: last.analyzed === 0
          ? 'Вы выключили разбор до первого чата — ничего не разобрано.'
          // причастие по тому же правилу файла, что и countWord: при «1» согласуется с
          // существительным, при остальных — средний род
          : `Вы выключили разбор посреди ночи: ${countWord(last.analyzed, 'чат', 'чата', 'чатов')} ${isOne(last.analyzed) ? 'разобран' : 'разобрано'}, чего не успели — следующей ночью.`,
      };
    case 'error':
      return { tone: 'info', title: null, text: 'Ночь прервалась из-за сбоя. Повторим следующей ночью.' };
    default:
      return null;
  }
}

// ─── «Запустить сейчас» (WN-6b) ───

// Формулировки — из карточки плана 0ab580a7 (Софья): текст диалога называет пакеты за проход
// (решение 1 плана: лимита чатов за ночь в настройках нет — в subtitle то, что есть,
// maxPackagesPerNight). Состояния и рендер — у WikiNightlyBlock.
export const NIGHTLY_RUN_BUTTON = 'Запустить сейчас';
export const NIGHTLY_RUN_BUSY = 'Идёт разбор…';
export const NIGHTLY_RUN_OFF_HINT = 'Ночной разбор выключен';
export const NIGHTLY_RUN_ASK_TITLE = 'Запустить ночной разбор сейчас?';
export const NIGHTLY_RUN_CONFIRM = 'Запустить';

export function nightlyRunAskText(maxPackagesPerNight: number): string {
  return `Хранитель разберёт новые чаты — не больше ${maxPackagesPerNight} пакетов за проход — и при включённом автоприменении сразу запишет найденное в вики. Откатить можно из „Журнала“.`;
}

// Отказ ручного запуска (WN-6b): стор возвращает код, а не WikiError (WikiRunNightlyResult, WN-6a).
// 'wiki_nightly_running' сюда не доходит: «уже идёт» стор считает началом прохода (там же).
// Прочие кода — отказы ручки run (WikiSettingsController.cs:237-251) и сетевой сбой (null).
// Неизвестный код — общий текст: смысла не выдумываем.
export function nightlyRunRefusalText(code: WikiError['code']): string {
  switch (code) {
    case 'wiki_llm_wiki_off': return WIKI_OFF_TEXT;
    case 'wiki_nightly_disabled': return 'Ночной разбор выключен — запустить его нельзя';
    case 'wiki_settings_invalid': return 'Настройки ночи не читаются — обновите страницу';
    default: return 'Не удалось запустить разбор';
  }
}

// «отказано N» в строке чисел итога ночи: число отказанных разборов среди analyzed
// (WikiNightlyService.cs:117-119). 0 или отсутствующее поле (старый итог) — строки нет.
export const nightlyRejectedLine = (n: number): string => `отказано ${n}`;

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
  add: 'Добавить утверждение',
  addPlaceholder: 'Новое утверждение',
  addRemove: 'Убрать новое утверждение',
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
  conflictMineRemoved: 'вы убрали это утверждение',
  takeMine: 'Оставить моё',
  takeAi: 'Взять их',
  keepEditing: 'Слить вручную',
  closeNoSave: 'Закрыть без сохранения',
  conflictGone: 'Страницы больше нет — сохранить правку некуда.',
  invalidTitle: 'Страницу нельзя сохранить',
  invalidKeep: 'Ваша правка осталась в редакторе — поправьте и сохраните ещё раз.',
  failTitle: 'Не удалось сохранить',
  failText: 'Ваша правка осталась в редакторе. Проверьте соединение и повторите.',
} as const;

// ─── Плашка «только чтение» / «нельзя править здесь» (F3a-qa, дефект QA a16c6167) ───

// Где правка недоступна по устройству раздела (локальный проект, раздел сферы), формулировка
// про Хранителя неверна: причина другая. Заголовок — смысл, данный в дефекте; причины —
// существующие тексты отказа (SECTION_OFF_TEXT / SPHERE_RESERVED_TEXT), не новая выдумка.
// Формулировка не привязана к флагам (TL-6): причина — устройство раздела, и без флагов
// текст про Хранителя в локальном разделе / разделе сферы был бы так же неверен. Для прочих
// случаев (обычный раздел без флагов, дрейф, сирота, правка выключена) текст плашки
// прежний — он там верен и остаётся в компоненте.
export const READONLY_TEXT = {
  blockedTitle: 'Эту страницу нельзя править здесь',
  localReason: SECTION_OFF_TEXT,
  sphereReason: SPHERE_RESERVED_TEXT,
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
  // «Журнал» (F6b, записка notes-wiki-unified-v1 §«Журнал»): фильтр и короткий вид действия
  // в строке; «Открыть страницу» — действие спора: вернуть нечего, текст человека на месте
  filter: 'ИИ поменял моё',
  kindReplaced: 'заменил',
  kindDisputed: 'оспорил',
  openPage: 'Открыть страницу',
  restored: 'Вернули вашу формулировку. 7 дней ИИ не меняет ваше на этой странице сам.',
  historyCaption: 'Ваша формулировка, заменена ИИ',
  changed: 'Страница изменилась, пока вы возвращали. Показали свежую: проверьте и повторите.',
  failed: 'Не удалось вернуть формулировку. Страница осталась как есть — повторите.',
  dotTitle: 'ИИ заменил ваши формулировки',
} as const;

// ─── Причины «Входящих» (F6a; ADR-038 «Входящие»: причины правила замены») ───
// Цель предложения — утверждение человека, если class операции оканчивается на @human
// (ADR-038, раздел «Входящие»). Причина такой операции приходит текстом бэка; фронт узнаёт
// случай по устойчивому началу строки (таблица ADR — зеркало строк WikiApplyPolicy) и
// показывает короткий текст пункта дословно; нераспознанное начало — нейтральный текст,
// сырая строка в строку списка не выходит (экран пакета показывает причину как есть).
// Причины операций не-человеческих целей показываются как есть (их тексты — не таблица ADR).

// Коды причин — чисто фронтовые: бэк причин-кодов не отдаёт вовсе (ADR-038:424
// «Устойчивого кода нет»), в DTO приходит только текст reason. Фронт узнаёт случай по
// устойчивому началу строки и присваивает свой код; тип живёт здесь, в единственном
// потребителе, а не в wire-контракте (R-F6a).
export type WikiInboxReasonCode =
  | 'source_time_unknown'
  | 'source_older_than_edit'
  | 'target_time_unknown'
  | 'source_not_newer'
  | 'recently_reverted'
  | 'class_disabled'
  | 'no_history_section';

// Две «Неизвестно…» причины: длинный префикс стоит первым, первый записан с двоеточием,
// как в таблице ADR. Каждой из этих мер по отдельности достаточно, чтобы их различить; без обеих
// вторая причина показала бы текст первой — тест-таблица ADR на этом краснеет.
// Третья часть пары — обязательное слово в строке: префикс «класс правки» короткий
// (имя класса в строке бэка динамическое), «выключен» есть и в строке ADR, и в строке
// бэка (WikiApplyPolicy.cs:140); будущая строка с этим началом, но не о выключении
// класса, в этот код не попадёт.
const INBOX_REASON_PREFIXES: Array<[string, WikiInboxReasonCode, string?]> = [
  ['Неизвестно, когда написан источник утверждения', 'target_time_unknown'],
  ['Неизвестно, когда написан источник:', 'source_time_unknown'],
  ['Источник старше вашей правки', 'source_older_than_edit'],
  ['Источник не новее заменяемого утверждения', 'source_not_newer'],
  ['страницу откатили вручную', 'recently_reverted'],
  ['класс правки', 'class_disabled', 'выключен'],
  // Строка бэка фиксирована (TL-3, WikiApplyPolicy.cs:158), динамических частей нет —
  // префикс берётся её началом целиком.
  ['На этой странице нет секции «История»', 'no_history_section'],
];

// Короткие тексты пунктов — правая колонка таблицы ADR-038, дословно.
export const INBOX_REASON_TEXT: Record<WikiInboxReasonCode, string> = {
  source_time_unknown: 'Неизвестно, когда написан источник',
  source_older_than_edit: 'Источник старше вашей правки',
  target_time_unknown: 'Неизвестно, когда написан источник утверждения',
  source_not_newer: 'Источник не новее заменяемого утверждения',
  recently_reverted: 'Вы недавно откатывали эту страницу',
  class_disabled: 'Класс выключен',
  // Текст для человека — правая колонка таблицы ADR-038: «раздела» вместо технического
  // «секции» из строки бэка, суть та же.
  no_history_section: 'На этой странице нет раздела «История», прежняя формулировка не сохранится — решите сами',
};

export const INBOX_TEXT = {
  // Подпись сверху списка — ADR-038 («Что поменять в макете», п. 4), дословно
  caption: 'Остальное, включая замену ваших формулировок новыми источниками, ИИ применил сам — это видно в «Журнале».',
  // Запасной текст для нераспознанного начала причины: нейтральный, без сырой строки
  unknown: 'ИИ не смог применить это сам',
} as const;

export function isHumanTargetClass(classKey: string): boolean {
  return classKey.endsWith('@human');
}

export function wikiInboxReasonCode(reason: string | null | undefined): WikiInboxReasonCode | null {
  if (!reason) return null;
  for (const [prefix, code, mustContain] of INBOX_REASON_PREFIXES) {
    if (reason.startsWith(prefix) && (!mustContain || reason.includes(mustContain))) return code;
  }
  return null;
}

export function wikiInboxReasonText(reason: string | null | undefined): string {
  const code = wikiInboxReasonCode(reason);
  return code ? INBOX_REASON_TEXT[code] : INBOX_TEXT.unknown;
}

// ─── Класс «Замена ваших формулировок» в «Настройках» (F6c; ADR-038 «Класс @human в „Настройках“») ───

// Бэк отдаёт класс замены формулировок человека сырыми ключами `…@human` (по одному на
// origin × supersede/conflict). Строка «Замена ваших формулировок» — агрегат этих записей
// (ADR-038:440); имя и описание — дословно из ADR-038 (решение 4, «Настройки») и макета
// notes-wiki-unified-v1-proposal («Применение»). Случай класса — та же проверка хвоста
// ключа, что во «Входящих» (F6a), а не таблица классов: бэк добавит origin — строка
// останется одной.
export const HUMAN_CLASS_TITLE = 'Замена ваших формулировок';
export const HUMAN_CLASS_DESC = 'ИИ меняет ваше сам, если источник новее вашей правки. Прежняя — в «Истории», на странице — «Вернуть мою».';

// ─── Секция «Заметки» страницы (F5a; подпись — макет и записка notes-wiki-unified-v1, §6) ───

export const NOTES_TEXT = {
  section: 'Заметки',
  caption: 'свободный текст · ИИ его не меняет, но читает',
  captionEdit: 'свободный текст, без сносок · последняя секция',
  emptyTitle: 'Здесь можно оставить заметку для себя и ИИ',
  emptyText: 'Свободный текст: ИИ его не меняет, но читает и может на него сослаться.',
  write: 'Записать заметку',
  placeholder: 'Текст заметки',
  aria: 'Секция «Заметки»',
} as const;

// Типы домена «Детский режим» (методы — ./api.ts).
// Источник правды имён полей: docs/adr/ADR-041-child-mode.md, раздел «Контракт /api/child/*»
// (бэк пишется параллельно по тому же разделу). Имена типов — по плану шага KM-F1,
// поля JSON — буквально как в ADR.

import type { PersonaGender, PersonaVoice } from '../../types';

// Старшая причина состояния по порядку И3: rules_pending → budget_exhausted →
// out_of_window → time_up → warn → ok (ADR-041, «Инвариант И3»).
export type ChildLimitsStateKind = 'ok' | 'warn' | 'time_up' | 'out_of_window' | 'rules_pending' | 'budget_exhausted';

// Ответ GET /api/child/status и POST /api/child/heartbeat — один и тот же DTO.
export interface ChildStatus {
  state: ChildLimitsStateKind; // старшая причина по порядку И3
  space: string; // «lessons» — единственное пространство волны 1
  minutesLeft: number | null; // остаток дневного лимита пространства; null — лимита нет
  windowEndsAt: string | null; // конец текущего окна; null — окон нет или сейчас вне окна
  nextWindowAt: string | null; // начало ближайшего окна при state = out_of_window; иначе null
  warnMinutes: number; // порог предупреждения из профиля (по умолчанию 5)
  maxAttachmentBytes: number; // ChildMode:MaxAttachmentBytes, по умолчанию 10 МБ
  attachmentTypes: string[]; // MIME из allow-list (image/png, image/jpeg, image/webp, application/pdf, text/plain)
  rulesAgreedAt: string | null;
  dailyMinutes: number | null; // дневной лимит из профиля; null — лимита нет
  windows: { from: string; to: string }[]; // окна доступа «HH:mm»; [] — окон нет
  minutesToday: number | null; // минуты с уроками за сегодня; null — журнал не прочитать
}

// Окно времени пространства: from/to — «HH:mm», to < from — окно через полночь;
// weekdays — ISO 1..7, нет поля — каждый день. Время — в таймзоне ребёнка (ADR-041, «Общие правила»).
export interface TimeWindow {
  from: string;
  to: string;
  weekdays?: number[];
}

// Тело PUT /api/users/{id}/child-profile и поле profile ответа (ChildProfileDto в ADR).
export interface ChildProfile {
  enabled: boolean;
  guardianUserIds: string[];
  providerKey: string; // запись пула с ApiKey (И2: ход ребёнка никогда не по подписке)
  model: string;
  spaces: Record<string, { windows: TimeWindow[]; dailyMinutes?: number | null }>;
  // Предметы (Т2a, разрез §2): отсутствие в теле — «предметы не трогать» (живое слияние стора,
  // UsersController.cs:430-434), явный [] — очистить. Порядок списка = порядок плиток на главной,
  // тело уходит как есть.
  subjects?: ChildSubjectAdmin[];
  dailyBudgetUsd?: number | null;
  monthlyBudgetUsd?: number | null;
  warnMinutes?: number; // по умолчанию 5
  resetRules?: boolean; // только во входе: сбросить RulesAgreedAt/RulesSummary
}

// Коды отказа загрузки вложения (413/415/422) — ADR-041, «Ручки» п. 8.
export type ChildAttachmentRejectCode = 'too_large' | 'unsupported_type' | 'pdf_no_text' | 'empty_file';

// Цвет плитки предмета: тот же набор, что ChildSubject.ColorKeys на бэке
// (ClaudeHomeServer.Core/Models/ChildProfile.cs:81). Ключ — в профиль, маппит фронт.
export type ChildSubjectColor = 'info' | 'rose' | 'success' | 'warning' | 'accent';

// Репетитор в плитке предмета (И5: ребёнку — имя, аватар, плитка; не промпт и не маршрут).
// null — висящий ключ персоны (удалена или чужая): имени и аватара нет, проекция их не выдумывает
// (ChildSubjectsController.cs:53-55); интерфейс замещает плитку глифом без репетитора.
export interface ChildSubjectTutor {
  personaId: string;
  name: string;
  hasAvatar: boolean;
}

// Элемент GET /api/child/subjects: только включённые предметы, порядок — порядок профиля (И5).
export interface ChildSubjectView {
  id: string;
  label: string;
  glyph: string;
  color: ChildSubjectColor;
  tutor: ChildSubjectTutor | null;
}

// Привязка базы знаний предмета (К1b, редакция 3 §18): из тела читается только datasetId;
// attachedAt — поле сервера (его ставит живое слияние стора, из тела не читается никогда).
// Отсутствие или null в теле — привязка снимается (UsersController.cs:456-458): при отправке
// списка предметов существующую привязку надо вернуть как есть, иначе она теряется.
export interface ChildSubjectKnowledge {
  datasetId: string;
  attachedAt?: string | null; // только в ответе
}

// Предмет в админском контракте (Т2a/Т4a): поле тела subjects и его же форма в ответе profile
// (record ChildSubjectDto в ChildProfileValidator). Маршрут (providerKey/model) — у предмета,
// не у персоны-репетитора (редакция 2 §12: Persona.Model репетитора пуст).
export interface ChildSubjectAdmin {
  id: string;
  label: string;
  glyph: string;
  color: ChildSubjectColor;
  providerKey: string;
  model: string;
  enabled: boolean;
  // Только в ответе: из тела не читается никогда — слияние предметов профиля (UserStore)
  // берёт его по Id из живого профиля, новый Id — пустая строка. Сервер null не отдаёт
  // никогда: в модели поле — строка с умолчанием "", ToChildProfileDto несёт его как есть.
  // Пустая строка — репетитора у предмета ещё нет (провижн — Т2c).
  tutorPersonaId?: string | null;
  knowledge?: ChildSubjectKnowledge | null;
  // Согласие админа на не-Anthropic маршрут (Т5a/Т6, И2′). Во входе — трёхзначно (Х1,
  // ревью Т5a); в ответе — производное: true, когда у предмета есть живое согласие
  // (RouteAcknowledgedAt != null, UsersController.ToChildProfileDto).
  // true — «соглашаюсь сейчас»: дату и автора штампует контроллер при сборке
  // профиля из тела (UsersController → RouteAcknowledgedAt/RouteAcknowledgedBy), а слияние
  // предметов профиля (UserStore) сохраняет живую пару того же предмета при том же
  // providerKey, иначе берёт этот кандидат или сбрасывает её; поля нет — живое
  // согласие того же предмета при том же providerKey (при смене providerKey оно не переживает);
  // false — отзыв: живое согласие не засчитывается даже при том же providerKey, отказа на
  // сохранении нет — 400 даст запуск хода. Решение — в сторе под замком по ЖИВОМУ предмету
  // (UserStore, слияние предметов профиля), проверка входа — ChildProfileValidator.ValidateSubjects.
  acknowledgeRouteRisk?: boolean;
  routeAcknowledgedAt?: string | null; // только в ответе: момент согласия, поле сервера
}

// Строка описи материалов предмета — GET .../child-subjects/{subjectId}/materials (К5a, разрез §2):
// ровно эти поля, содержимого файла в ответе нет никогда; внутренние имена стора
// (storedName/textName) и автор загрузки наружу не выходят (ChildSubjectMaterialsController.cs:163-168).
// Значения kind — wire-константы стора KindPdf/KindText/KindDocx/KindImage
// (ChildSubjectMaterialsStore.cs:96-99): txt и md дают общий 'text' (сигнатура их не различает),
// 'image' — распознанная страница (решение 24).
export interface ChildSubjectMaterial {
  id: string; // «m_» + 12 hex, серверный id (по нему и только нему обращается тулсет репетитора)
  name: string; // подпись от имени админа: путь из него не строится никогда
  kind: 'pdf' | 'text' | 'docx' | 'image';
  bytes: number; // байты оригинала: метр предмета считается по ним, не по производному тексту
  uploadedAt: string; // ISO, момент загрузки на сервере
}

// Ответ успешной загрузки (решение 24, §9 «preview в ответе загрузки»): та же строка + preview —
// первые 600 символов распознанного текста («так репетитор прочтёт эту страницу»,
// ChildSubjectMaterialsStore.cs:106-108). В описи preview нет никогда: только в ответе загрузки.
export type ChildSubjectMaterialUploaded = ChildSubjectMaterial & { preview: string };

// Элемент GET /api/admin/child-routes (Т2f): сервер отдаёт готовые допустимые пары «поставщик →
// модель» — отбор одной функцией ChildRoute.Evaluate, её же зовут валидатор и сверка запуска,
// поэтому пикер не предлагает то, что не пройдёт при сохранении (ChildRoutesController.cs:10-14).
// Порядок — как с сервера: записи пула в порядке конфига, затем провайдеры (там же, строка 27-28).
export interface ChildRoute {
  providerKey: string;
  kind: 'pool' | 'cloud' | 'local';
  label: string;
  // cloud — только модели с ценой (без неё бюджет И3 не ограничивает); local — priced=false:
  // «бюджет не тратится», ограничивают окна и минуты (редакция 2 §13).
  priced: boolean;
  // Согласие на не-Anthropic маршрут (И2′): нужно для cloud и local, никогда для пула.
  requiresAck: boolean;
  models: { id: string; label: string }[];
}

// Тело PUT /api/users/{id}/child-tutors/{personaId} (редакция 2 §12, редакция 3 §22): отсутствие
// или null поля — «не трогать»; mustDo/mustNot: [] — явная очистка (до 12 пунктов по 300 символов,
// иначе 400 rules_too_long). Прочие слоты персоны (model, tone, outputFormat…) в контракта нет:
// сервер их игнорирует, Persona.Model репетитора остаётся пустым (ChildTutorsController.cs:26-31).
export interface ChildTutorPatch {
  name?: string;
  character?: string;
  greeting?: string;
  voice?: PersonaVoice;
  gender?: PersonaGender;
  mustDo?: string[] | null;
  mustNot?: string[] | null;
}

// Элемент GET /api/child/spaces/{space}/chats (ADR-041, ручка 4). subject — с сервера, не из
// localStorage; у старых (до-предметных) чатов бэк отдаёт null (Session.ChildSubject — additive
// nullable). subjectClosed — предмет выключен или удалён: чат остаётся в списке, только для
// чтения (И1-бис); для closed-чата subject всегда не совпадает с живым предметом.
export interface ChildChatListItem {
  sessionId: string;
  name: string;
  updatedAt: string;
  subject: string | null;
  subjectClosed: boolean;
  // Озвучка ответов чата: оболочка восстанавливает её при открытии (субзадача Т3c)
  voiceMode: boolean;
}

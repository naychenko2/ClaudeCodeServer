// Типы домена «Детский режим» (методы — ./api.ts).
// Источник правды имён полей: docs/adr/ADR-041-child-mode.md, раздел «Контракт /api/child/*»
// (бэк пишется параллельно по тому же разделу). Имена типов — по плану шага KM-F1,
// поля JSON — буквально как в ADR.

import type { PersonaGender, PersonaVoice } from '../../types';
import type { ChildLessonHeaderData } from './ChildLessonHeader';
import type { ChildSubjectsDock } from '../projects/ProjectRail';

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

// Коды отказа загрузки вложения (413/415/422) — ADR-041, «Ручки» п. 8.
export type ChildAttachmentRejectCode = 'too_large' | 'unsupported_type' | 'pdf_no_text' | 'empty_file';

// Цвет плитки предмета: тот же набор, что ChildSubject.ColorKeys на бэке
// (ClaudeHomeServer.Core/Models/ChildProfile.cs:89). Ключ — в профиль, маппит фронт.
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
  // Название предмета из профиля, даже когда предмет выключен (ХВ2-Б1); null — предмета нет или он удалён
  subjectLabel?: string | null;
  // Озвучка ответов чата: оболочка восстанавливает её при открытии (субзадача Т3c)
  voiceMode: boolean;
}

// ── Окно настроек ребёнка (Ф2a) ────────────────────────────────────────────────
// Форма GET/PUT /api/users/{id}/child-mode и /api/users/{id}/child-profile после Б4a
// (ADR-041 ред. 4 решение 30, ред. 6 решение 60, ред. 7 §13; запись ручек — docs/architecture/api.md,
// «Детский режим: окно настроек ребёнка»). Зеркало бэка: record ChildProfileDto / ChildSubjectDto /
// ChildGuardianDto / ChildProfileResponse (Services/ChildMode/ChildProfileValidator.cs), маппинг
// ответа — UsersController.ToChildProfileDto. Имена полей — camelCase сериализации этих записей.
//
// Свойства этой формы (с Б4a):
// - предмет — только плитка: маршрут, репетитор, знания и согласие предмета перешли в
//   проект-предмета (ред. 7 §13), поэтому у предмета нет providerKey/model/tutorPersonaId;
// - согласие — одно на профиле (решение 26), а не на предмете;
// - ответ добавил готовность опекунов и рубильник инстанса общих проектов (решение 60).

// Предмет в контракте окна: id — slug (строчные буквы, цифры и «-», 1..32, без повторов —
// subject_id_invalid), label 1..40, глиф 1..3, цвет — ключ палитры ChildSubjectColor
// (проверки — ChildProfileValidator.ValidateSubjects). Пустой элемент списка ([null] в теле) —
// невалидный ключ: элемент списка всегда целый объект.
export interface ChildSettingsSubject {
  id: string;
  label: string;
  glyph: string;
  color: ChildSubjectColor;
  // Отсутствие поля в теле — «включён»: маппинг бэка берёт true (UsersController.ToChildProfile).
  enabled?: boolean;
  // Только в ответе: у предмета была привязана база знаний — материалы надо перенести в папку
  // проекта-предмета. Из тела не читается никогда: маркер берёт стор из живого предмета
  // (UserStore.SetChildProfile).
  knowledgeMoved?: boolean;
}

// Опекун профиля в ответе (Б4a): sharedProjectsFlag — пользовательский флаг shared-projects
// БЕЗ учёта рубильника инстанса (SharedProjectsGate.FlagOn). Порядок списка — как guardianUserIds.
// Окно показывает флаг; включает его человек (PUT /api/feature-flags/{key}), не сервер.
export interface ChildSettingsGuardian {
  userId: string;
  sharedProjectsFlag: boolean;
}

// Тело PUT child-profile и поле profile ответа всех трёх ручек окна (одна запись профиля с
// валидатором — решение 12/30, новых путей записи маршрута нет).
export interface ChildSettingsProfile {
  enabled: boolean;
  // Опекун — id живого пользователя: несуществующий — отказ guardian_not_found.
  guardianUserIds: string[];
  // Маршрут чата правил — единственный маршрут, оставленный профилю (ред. 7 §13: маршрут урока
  // несёт персона-руководитель проекта-предмета). Пустые строки — «маршрута нет»: профиль
  // «не настроен» законен (валидатор его не отвергает), уроки и чат правил закрыты политикой.
  providerKey: string;
  model: string;
  spaces: Record<string, { windows: TimeWindow[]; dailyMinutes?: number | null }>;
  // Отсутствие поля — «предметы не трогать» (живое слияние стора), явный [] — очистить.
  // Порядок списка = порядок плиток, сохраняется как есть.
  subjects?: ChildSettingsSubject[];
  dailyBudgetUsd?: number | null;
  monthlyBudgetUsd?: number | null;
  warnMinutes?: number; // по умолчанию 5
  resetRules?: boolean; // только во входе: сбросить RulesAgreedAt/RulesSummary
  // Согласие админа на риск маршрута — ОДНО на профиль, покрывает все предметы и чат правил,
  // сменой модели или поставщика не сбрасывается (решение 26). ВХОД трёхзначен: true — поставить,
  // отсутствие поля или null — не трогать, false — отозвать. В ОТВЕТЕ всегда boolean:
  // «согласие стоит» (ToChildProfileDto), момент и автор — следующие поля.
  acknowledgeRouteRisk?: boolean | null;
  // Только в ответе: момент и автор согласия; из тела не читаются никогда, их пишет живое
  // слияние стора под замком. null — согласия нет.
  routeRiskAcknowledgedAt?: string | null;
  routeRiskAcknowledgedBy?: string | null;
  // Только в ответе (Б4a): готовность опекунов к механизму общих проектов (решение 60) и
  // рубильник инстанса SharedProjects:Enabled — без него флаги опекунов ничего не открывают.
  guardians?: ChildSettingsGuardian[];
  sharedProjectsInstanceEnabled?: boolean;
}

// Ответ 200 всех трёх ручек окна. profile — null только когда профиля нет (выключение
// пользователя, который им и не был); при включении — всегда целый.
export interface ChildSettingsResponse {
  profile: ChildSettingsProfile | null;
  tutorPersonaId: string;
}

// ── Детская проводка рабочего экрана (Ф1j, ADR-041 ред. 8, решения 76–84) ──────────
// Один проп-объект, которым владелец экрана несёт детскую поверхность в components
// десктопной раскладки: DesktopWorkspace → ChatPanel (композер, лента, шапка) и
// → ProjectRail (ряд предметов). Сосед ChildSurface (features/account/types.ts):
// features/feedKinds — его composerFeatures/feedKinds как есть; header и dock собирает
// сам владелец (Ф1h, Ф1e). Отсутствие целого объекта = взрослый экран: ни один
// детский проп не передаётся, поведение взрослого неизменно.
export interface ChildWorkspaceProps {
  // = surface.composerFeatures → ChatPanel.childFeatures → Composer.childFeatures (Ф1c)
  features: string[];
  // = surface.feedKinds → ChatPanel.childFeedKinds → filterChildFeed (Ф1d)
  feedKinds: string[];
  // Шапка урока (Ф1h): предмет открытого чата + имена кураторов
  header?: ChildLessonHeaderData;
  // Детский ряд предметов ProjectRail (Ф1e): предметы, активный, переходы
  dock?: ChildSubjectsDock;
}

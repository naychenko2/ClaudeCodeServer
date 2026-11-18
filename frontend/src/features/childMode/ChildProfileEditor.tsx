// KM-F11: форма профиля ребёнка для админа — тело PUT /api/users/{id}/child-profile (ADR-041, ручка №10).
// Вызывающий (редактор в «Пользователях», KM-F12) даёт пользователя и то, что уже знает о его профиле;
// «Сохранить» — единственный путь записи. Компонент не читает профиль сам: отдельной GET-ручки профиля
// в контракте нет (ADR-041, «Ручки» №10 — только PUT).
//
// Черновик привязан к user.id: сменили пользователя — черновик и только-чтение сброшены (сброс в рендере,
// по образцу «derived state» из React: старый вывод не коммитится). Ответ сохранения проверяется тем же
// id: ответ для прежнего пользователя отбрасывается, он не пишет чужие только-чтение и не зовёт onSaved.
// Время — строго «HH:mm» (контракт ADR), «Минут в день» — пусто (лимита нет) или целое 1..1440,
// «Предупредить за» — пусто (по умолчанию 5) или целое 1..60: некорректное — подсветка поля,
// возврат на вкладку «Режим и время» (подсветка живёт только на ней) и ни одного запроса
// (при выключенном режиме эти поля не в деле — не подсвечиваем); остальное решает
// сервер, его 400-код и отказы 403/404 переводятся в человеческий текст (REJECT_TEXT), message
// ответа показываем как есть.
//
// Т4f/Т6: вкладка «Предметы». «Помощник (правила)» — пикер ChildRoutePicker вместо текстовых
// полей (значения те же: providerKey/model профиля), под ним список предметов ChildSubjectsEditor,
// под списком — карточка репетитора открытого предмета. Список маршрутов — один GET на
// пользователя при открытии. Пикер помощника знает только записи пула: маршрут профиля
// валидируется без согласий (ChildProfileValidator.cs:36-41). Пикеры предметов (Т6) знают весь
// список — облачные и локальные с предупреждением и галкой согласия (разрез §3).
// «Сохранить» несёт subjects в порядке редактора; acknowledgeRouteRisk уходит в тело только как
// действие этой сессии — прочитанное из ответа согласие в черновик не пишется (ревью Т4a: иначе
// нетронутый профиль молча согласовал бы свой маршрут заново). Тьюторов и состояние согласия
// берём из ответа; отказ, называющий id предмета, красит его строку (макет «рамка C.danger»)
// и приводит на вкладку «Предметы».
//
// К5d: знания открытого предмета (макет Д2). Под карточкой репетитора — пикер базы знаний
// ChildSubjectKnowledgePicker, затем файлы предмета ChildSubjectMaterials. Привязка живёт в черновике
// предмета (ключ — subject.id) и уходит в тело при «Сохранить» вместе с subjects; ответ сохранения
// обновляет knowledge и тьютора предметов одним слиянием. Отказ знания называет базу, а не предмет
// (ChildProfileValidator.cs:201-204) — предмет находится по отличению привязки от серверной и красит
// свою строку тем же механизмом, что отказ маршрута. Предмета, которого на сервере ещё нет, файлы
// не грузятся (загрузка дала бы 404) — вместо описи строка о сохранении профиля.

import { useEffect, useRef, useState } from 'react';
import { AlertTriangle, Info, Plus, Trash2 } from 'lucide-react';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { Notice } from '../../components/ui/Notice';
import { Button } from '../../components/ui/Button';
import { Checkbox } from '../../components/ui/Checkbox';
import { Field, TextField } from '../../components/ui/Field';
import { Tabs } from '../../components/ui/Tabs';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { useIsMobile } from '../../lib/breakpoints';
import { childApi } from './api';
import { ChildRoutePicker, type ChildRouteValue } from './ChildRoutePicker';
import { ChildSubjectKnowledgePicker } from './ChildSubjectKnowledgePicker';
import { ChildSubjectMaterials } from './ChildSubjectMaterials';
import { ChildSubjectsEditor } from './ChildSubjectsEditor';
import { ChildTutorCard } from './ChildTutorCard';
import type { ChildProfile, ChildRoute, ChildSubjectAdmin, TimeWindow } from './types';

// Пользователь для редактора: id и имя — из списка «Пользователей»; профиль, тьютор и дата согласия —
// то, что знает вызывающий (из данных списка, когда бэк их добавит, или из последнего сохранения здесь).
export interface ChildProfileEditorUser {
  id: string;
  username: string;
  childProfile?: ChildProfile | null;
  tutorPersonaId?: string | null;
  rulesAgreedAt?: string | null;
}

interface Props {
  user: ChildProfileEditorUser;
  onSaved: (profile: ChildProfile) => void;
}

// Единственное пространство волны 1 («Уроки»); ключ — из ADR-041.
const LESSONS = 'lessons';
// Вкладки редактора (макет Д1, «Вкладки редактора»): «Режим и время» — ограничения времени/бюджета,
// «Предметы» — модель помощника правил и (в Т4f) список предметов. Вкладка — только выбор экрана:
// черновик общий, переключение ввода не теряет. По умолчанию — «Режим и время».
type EditorTab = 'mode' | 'subjects';
const TAB_ITEMS = [
  { value: 'mode' as EditorTab, label: 'Режим и время' },
  { value: 'subjects' as EditorTab, label: 'Предметы' },
];
const DEFAULT_WARN_MINUTES = 5;
// Строгий формат времени контракта: две цифры часа 00..23, две цифры минуты 00..59.
const TIME_RE = /^([01]\d|2[0-3]):[0-5]\d$/;
// Дни недели — ISO 1..7 (Пн..Вс); пустой выбор — каждый день (поля в теле нет, ADR-041).
const DAY_LABELS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
// «Минут в день»: пусто — лимита нет; иначе — целое 1..1440. Всё прочее («3O», «0», «12.5», «2000») —
// ошибка поля и ни одного запроса: молчаливое «лимита нет» из нечисловых значений опасно для
// детской границы (R-KM-F11).
const MINUTES_RE = /^\d+$/;
const MINUTES_ERROR = 'Введи число минут, например 30';
const badMinutes = (s: string): boolean => {
  const t = s.trim();
  if (!t) return false;
  if (!MINUTES_RE.test(t)) return true;
  const n = Number(t);
  return n < 1 || n > 1440;
};
// «Предупредить за»: пусто — значение по умолчанию (DEFAULT_WARN_MINUTES); иначе — целое 1..60.
// Проверка та же, что у «Минут в день»: молчаливое «5» из «abc» так же опасно, как «лимита нет»
// из нечисловых минут (R-KM-F11).
const WARN_MAX = 60;
const WARN_ERROR = 'Введи число от 1 до 60, например 5';
const badWarn = (s: string): boolean => {
  const t = s.trim();
  if (!t) return false;
  if (!MINUTES_RE.test(t)) return true;
  const n = Number(t);
  return n < 1 || n > WARN_MAX;
};

// Человеческий текст отказов 400 ручки №10 (ADR-041): код — причина для интерфейса, человеку —
// что сделать. message ответа добавляется строкой ниже, как есть (ADR-041, «Общие правила»).
const REJECT_TEXT: Record<string, string> = {
  not_container: 'Детский режим включается только пользователям в песочнице. Сейчас процессы этого пользователя идут на машине сервера.',
  provider_not_api_key: 'Такого провайдера нет среди записей с API-ключом. Ребёнку нужна отдельная запись пула с ключом, не подписка.',
  invalid_window: 'Окно времени задано неверно: формат ЧЧ:ММ, конец позже начала или через полночь.',
  invalid_space: 'Пространство задано неверно. Сейчас доступно только «Уроки».',
  guardian_not_found: 'Среди опекунов есть id, которого нет среди пользователей. Проверь список через запятую.',
  user_not_found: 'Пользователь не найден — обнови список',
  // Предметы (Т2a): id и что именно не так — в message ответа (ChildProfileValidator.cs:109-120),
  // он показывается строкой ниже; здесь — общая причина, по которой краснеет строка списка.
  subject_id_invalid: 'Ключ предмета задан неверно: строчные буквы, цифры и «-», до 32 знаков, без повторов.',
  subject_label_invalid: 'Название предмета — от 1 до 40 символов.',
  subject_glyph_invalid: 'Глиф — от 1 до 3 символов.',
  subject_color_invalid: 'Такого цвета нет в палитре. Выбери один из пяти.',
  model_not_concrete: 'У предмета не задана конкретная модель. Выбери её из списка.',
  model_unpriced: 'У модели нет цены — бюджет не смог бы ограничить расход. Выбери другую.',
  route_risk_unacknowledged: 'Этот маршрут требует согласия: отметь «Понимаю риск и разрешаю этот маршрут».',
  // Привязка знания (К1b/К5d): отказ называет базу, а не предмет (ChildProfileValidator.cs:192-204,
  // early exit контроллера UsersController.cs:217-229) — строку предмета красит поиск по новой
  // привязке (knowledgeRejectSubjectId); message ответа показывается строкой ниже, как есть.
  dataset_not_attachable: 'Эту базу нельзя привязать к предмету: допустимы только общая или своя самостоятельная. Выбери другую базу или «Без базы».',
  knowledge_unavailable: 'Раздел «Знания» сейчас недоступен — привязать базу нельзя. Сохрани предмет «Без базы», файлы работают и так.',
};
const REJECT_FALLBACK = 'Сохранить не удалось. Попробуй ещё раз.';
// 403 гейта авторизации ручки №10 (политика админа): тело отказа пустое, кода нет — текст по status.
const FORBIDDEN_TEXT = 'Нужны права администратора';
// Отказ загрузки списка маршрутов: message ответа как есть, иначе общий — своего списка
// пикер не выдумывает (пустой список сказал бы «записей пула нет», чего мы не знаем).
const ROUTES_LOAD_ERROR = 'Список моделей не загрузился. Попробуй позже.';
const loadErrorText = (e: unknown): string => {
  const m = (e as { body?: { message?: string } } | null)?.body?.message;
  return typeof m === 'string' && m.trim() ? m.trim() : ROUTES_LOAD_ERROR;
};

// Черновик — строки полей ввода (пусто = поля нет в теле), окна списком, дни — множества ISO.
interface DraftWindow { from: string; to: string; days: number[] }
interface Draft {
  enabled: boolean;
  providerKey: string;
  model: string;
  windows: DraftWindow[];
  dailyMinutes: string;
  warnMinutes: string;
  dailyBudget: string;
  monthlyBudget: string;
  guardians: string;
  resetRules: boolean;
  // Предметы: отсутствие — их не знаем (в теле нет поля, бэк его не трогает); список — правим
  // как есть, пустой список — явная очистка. Порядок списка = порядок плиток у ребёнка (И5).
  subjects?: ChildSubjectAdmin[];
  // Прочие пространства профиля (волна 2) форма не правит — они проходят в тело как есть.
  otherSpaces: Record<string, { windows: TimeWindow[]; dailyMinutes?: number | null }>;
}

// Предмет, известный серверу: id и его живая привязка (null — привязки нет). «Известный» = тот,
// что пришёл из профиля вызывающего или из ответа сохранения: нового предмета на сервере ещё нет,
// его файлы дали бы 404, а «новая привязка» — мера отказа знания (knowledgeRejectSubjectId).
interface ServerSubject { id: string; knowledgeId: string | null }
const serverSubjectsOf = (p: ChildProfile | null | undefined): ServerSubject[] =>
  (p?.subjects ?? []).map((s) => ({ id: s.id, knowledgeId: s.knowledge?.datasetId ?? null }));

// Снимок редактора: черновик + только-чтение + список маршрутов + известные серверу предметы,
// всё под одним id. Смена id — новый снимок: черновик, маршруты и открытые состояния прежнего
// пользователя не переносятся.
interface Snapshot {
  id: string; draft: Draft; tutorPersonaId: string | null; rulesAgreedAt: string | null;
  // Маршруты для пикеров: null — ещё не пришли (или не удалось), список — получены, как с сервера.
  routes: ChildRoute[] | null;
  routesError: string | null;
  serverSubjects: ServerSubject[];
}

function snapshotOf(user: ChildProfileEditorUser): Snapshot {
  return {
    id: user.id, draft: draftOf(user), tutorPersonaId: user.tutorPersonaId ?? null,
    rulesAgreedAt: user.rulesAgreedAt ?? null, routes: null, routesError: null,
    serverSubjects: serverSubjectsOf(user.childProfile),
  };
}

function emptyDraft(): Draft {
  return {
    enabled: false, providerKey: '', model: '', windows: [],
    dailyMinutes: '', warnMinutes: String(DEFAULT_WARN_MINUTES),
    dailyBudget: '', monthlyBudget: '', guardians: '', resetRules: false, otherSpaces: {},
  };
}

function draftOf(user: ChildProfileEditorUser): Draft {
  const d = emptyDraft();
  const p = user.childProfile;
  if (!p) return d;
  d.enabled = p.enabled;
  d.providerKey = p.providerKey ?? '';
  d.model = p.model ?? '';
  d.guardians = (p.guardianUserIds ?? []).join(', ');
  d.dailyBudget = p.dailyBudgetUsd == null ? '' : String(p.dailyBudgetUsd);
  d.monthlyBudget = p.monthlyBudgetUsd == null ? '' : String(p.monthlyBudgetUsd);
  d.warnMinutes = String(p.warnMinutes ?? DEFAULT_WARN_MINUTES);
  // Предметы читаются из профиля как есть: порядок списка, привязка знаний и маршрут
  // существующего предмета не теряются от того, что здесь их никто не правил.
  // Кроме согласия: серверный acknowledgeRouteRisk — информация «согласие стоит», не действие
  // админа, и в черновик оно не пишется (живое согласие показывает его дата, строка редактора).
  // Иначе нетронутый профиль ушёл бы в тело с этим флагом как с «соглашаюсь сейчас» и молча
  // согласовал бы свой маршрут заново (ревью Т4a).
  d.subjects = p.subjects
    ? p.subjects.map((s) => (s.acknowledgeRouteRisk === undefined ? s : { ...s, acknowledgeRouteRisk: undefined }))
    : undefined;
  const { [LESSONS]: lessons, ...other } = p.spaces ?? {};
  d.otherSpaces = other;
  d.windows = (lessons?.windows ?? []).map((w) => ({ from: w.from, to: w.to, days: w.weekdays ?? [] }));
  d.dailyMinutes = lessons?.dailyMinutes == null ? '' : String(lessons.dailyMinutes);
  return d;
}

const numOrNull = (s: string): number | null => {
  const t = s.trim();
  if (!t) return null;
  const n = Number(t);
  return Number.isFinite(n) ? n : null;
};

const splitIds = (s: string): string[] => s.split(',').map((x) => x.trim()).filter(Boolean);

// Предмет в теле (Т2a/Т6): только поля тела. tutorPersonaId и routeAcknowledgedAt — поля ответа
// (ChildProfileValidator.cs:224-226), в тело не идут. acknowledgeRouteRisk — только как действие
// этой сессии (ревью Т4a): true — «соглашаюсь сейчас», false — «отзываю» (Х1, 43b77058a);
// отсутствие поля — «живое согласие не трогать» (бэк держит его при том же providerKey,
// UserStore.SetChildProfile). Серверного флага в черновике нет (draftOf), значит здесь — только
// то, что сделал админ.
function subjectBody(s: ChildSubjectAdmin): ChildSubjectAdmin {
  return {
    id: s.id, label: s.label, glyph: s.glyph, color: s.color,
    providerKey: s.providerKey, model: s.model, enabled: s.enabled,
    ...(s.acknowledgeRouteRisk !== undefined ? { acknowledgeRouteRisk: s.acknowledgeRouteRisk } : {}),
    // Привязку знаний возвращаем как есть: отсутствие или null в теле её снимает (UsersController.cs:456-458).
    // Трёхзначно, как и само поле: поля в черновике нет — мы его не знаем (в теле его нет);
    // явный null (выбор «Без базы» в пикере) — уходит в тело как null.
    ...(s.knowledge !== undefined ? { knowledge: s.knowledge } : {}),
  };
}

// Слияние ответа сохранения в черновик: тьютора — из ответа (провижн создания его новому предмету,
// UsersController.cs:279-305), привязку знания — тоже из ответа: живая она, не черновая (стор держит
// прежнюю пару id/момент, когда id не изменился — UsersController.cs:465-468). Согласие — тем же
// порядком (Т6): дата из ответа (стор записал её на наше «соглашаюсь сейчас» или оставил живую;
// отзыва — null), черновой флаг действия снимается — после сохранения это снова живое согласие,
// а не pending-действие, и следующее сохранение его не перепишет. Слияние — по id; своего
// id, которого в ответе нет, не трогаем; поля, которого в ответе нет, черновика тоже не касаемся.
function subjectApplied(d: Draft, res?: ChildProfile): Draft {
  const subjects = d.subjects;
  if (!subjects) return d;
  const byId = new Map((res?.subjects ?? []).map((s) => [s.id, s]));
  return {
    ...d,
    subjects: subjects.map((s) => {
      const r = byId.get(s.id);
      return r
        ? {
            ...s,
            tutorPersonaId: r.tutorPersonaId ?? null,
            acknowledgeRouteRisk: undefined,
            ...(r.routeAcknowledgedAt !== undefined ? { routeAcknowledgedAt: r.routeAcknowledgedAt ?? null } : {}),
            ...(r.knowledge !== undefined ? { knowledge: r.knowledge } : {}),
          }
        : s;
    }),
  };
}

// Отказ по предмету: бэк называет id в кавычках («Ключ предмета «math» …»,
// ChildProfileValidator.cs:109-115) — на нём краснеет строка списка. Чужой id (его нет в списке)
// не красит ничего: подсветки без строки нет.
const SUBJECT_ID_RE = /«([a-z0-9-]{1,32})»/;
function subjectIdOf(message: string | undefined, subjects?: ChildSubjectAdmin[]): string | undefined {
  const m = message ? SUBJECT_ID_RE.exec(message) : null;
  if (!m) return undefined;
  return subjects?.some((s) => s.id === m[1]) ? m[1] : undefined;
}
// Отказ знания называет в «ёлочках» базу, а не предмет: dataset_not_attachable —
// «База знаний «{datasetId}» не найдена…» или «База знаний «{name}» не может быть привязана…»
// (ChildProfileValidator, код dataset_not_attachable); knowledge_unavailable не называет ничего.
// Если в message есть имя в «ёлочках» — ищем предмет с новой привязкой, чей datasetId ему равен:
// черновик хранит только datasetId (ChildSubjectKnowledge, types.ts), названия базы в черновике
// нет — отказ, называющий название, уходит к общему правилу. Не нашли (или knowledge_unavailable) —
// тот же предмет, что отвергает валидатор: новая привязка против серверной (или предмета нет на
// сервере вовсе), в порядке тела.
const DATASET_REF_RE = /«([^»]+)»/;
function knowledgeRejectSubjectId(d: Draft, known: ServerSubject[], message?: string): string | undefined {
  const withNewBinding = (d.subjects ?? []).filter((s) => {
    const want = s.knowledge?.datasetId;
    if (!want) return false;
    const live = known.find((x) => x.id === s.id);
    return !live || live.knowledgeId !== want;
  });
  const named = message ? DATASET_REF_RE.exec(message)?.[1] : undefined;
  if (named) {
    const hit = withNewBinding.find((s) => s.knowledge?.datasetId === named);
    if (hit) return hit.id;
  }
  return withNewBinding[0]?.id;
}

// Тело PUT: выключенный режим — `{ enabled: false }` (ADR-041, ручка №10); включённый — ChildProfileDto.
// resetRules отправляем только отмеченным: JSON.stringify выбрасывает undefined, как в соседних ручках.
// subjects: нет в черновике — нет и в теле («предметы не трогать»), пустой список — явная очистка.
function bodyOf(d: Draft): ChildProfile | { enabled: false } {
  if (!d.enabled) return { enabled: false };
  return {
    enabled: true,
    guardianUserIds: splitIds(d.guardians),
    providerKey: d.providerKey.trim(),
    model: d.model.trim(),
    spaces: {
      ...d.otherSpaces,
      [LESSONS]: {
        windows: d.windows.map((w) => ({ from: w.from, to: w.to, weekdays: w.days.length ? w.days : undefined })),
        dailyMinutes: numOrNull(d.dailyMinutes),
      },
    },
    ...(d.subjects ? { subjects: d.subjects.map(subjectBody) } : {}),
    dailyBudgetUsd: numOrNull(d.dailyBudget),
    monthlyBudgetUsd: numOrNull(d.monthlyBudget),
    warnMinutes: numOrNull(d.warnMinutes) ?? DEFAULT_WARN_MINUTES,
    resetRules: d.resetRules || undefined,
  };
}

const fmtDate = (iso: string): string =>
  new Date(iso).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });

export function ChildProfileEditor({ user, onSaved }: Props) {
  const isMobile = useIsMobile();
  const [snap, setSnap] = useState<Snapshot>(() => snapshotOf(user));
  // Смена пользователя — новый снимок по новому id (черновик, только-чтение и маршруты сброшены).
  if (snap.id !== user.id) {
    setSnap(snapshotOf(user));
  }
  const d = snap.id === user.id ? snap.draft : draftOf(user);
  const routes = snap.id === user.id ? snap.routes : null;
  const routesError = snap.id === user.id ? snap.routesError : null;
  const serverSubjects = snap.id === user.id ? snap.serverSubjects : serverSubjectsOf(user.childProfile);
  const [saving, setSaving] = useState(false);
  // Пустое поле окна подсвечивается только после попытки сохранить; некорректное — сразу, пока правят.
  const [attempted, setAttempted] = useState(false);
  // subjectId — предмет, названный отказом: его строка в списке краснеет (макет Д1).
  const [reject, setReject] = useState<{ text: string; server?: string; subjectId?: string } | null>(null);
  // Текущая вкладка — только выбор экрана; черновик (snap.draft) от неё не зависит.
  const [tab, setTab] = useState<EditorTab>('mode');
  // Открытый предмет: под списком — карточка его репетитора. Сменой пользователя гасится (см. эффект).
  const [selectedId, setSelectedId] = useState<string | null>(null);
  // Текущий пользователь на момент прихода ответа: по нему отбрасываем устаревший ответ.
  const activeIdRef = useRef(user.id);
  activeIdRef.current = user.id;

  // Список маршрутов — один запрос на пользователя: при смене id или уходе с экрана прежний
  // запрос отменяется (signal), а его ответ не применяется — по id снимка, тем же механизмом,
  // что и отбрасывание ответа сохранения. Открытый предмет чужого пользователя быть не должен.
  useEffect(() => {
    setSelectedId(null);
    const ac = new AbortController();
    childApi.getRoutes({ signal: ac.signal })
      .then((list) => setSnap((s) => (s.id === user.id ? { ...s, routes: list, routesError: null } : s)))
      // Пустой список вместе с текстом отказа: предметы править можно (их данные — данные
      // админа), пикер при пустом списке сам говорит, что выбирать не из чего.
      .catch((e) => setSnap((s) => (s.id === user.id ? { ...s, routes: [], routesError: loadErrorText(e) } : s)));
    return () => ac.abort();
  }, [user.id]);

  const patch = (fn: (prev: Draft) => Draft) => setSnap((s) => (s.id === user.id ? { ...s, draft: fn(s.draft) } : s));
  // Список предметов — контролируемый: редактор отдаёт весь новый массив, порядок его и есть
  // порядок плиток у ребёнка (И5), здесь он не переставляется.
  const patchSubjects = (next: ChildSubjectAdmin[]) =>
    setSnap((s) => (s.id === user.id ? { ...s, draft: { ...s.draft, subjects: next } } : s));
  // Привязка знания открытого предмета (К5d): меняется только её поле, порядок списка остаётся
  // (он и есть порядок плиток у ребёнка, И5). null — явный «Без базы»: уходит в тело как null.
  const patchSubjectKnowledge = (subjectId: string, datasetId: string | null) =>
    patch((prev) => ({
      ...prev,
      subjects: (prev.subjects ?? []).map((s) => (s.id === subjectId ? { ...s, knowledge: datasetId ? { datasetId } : null } : s)),
    }));
  const patchWindow = (i: number, fn: (w: DraftWindow) => DraftWindow) =>
    patch((prev) => ({ ...prev, windows: prev.windows.map((w, j) => (j === i ? fn(w) : w)) }));

  const badTime = (v: string) => !TIME_RE.test(v);
  const showBadTime = (v: string) => badTime(v) && (attempted || v !== '');
  // «Минут в день» и «Предупредить за» краснеют только при включённом режиме: при выключенном
  // эти значения никуда не уходят (тело — { enabled: false }), и красное поле за значение,
  // которое никто не отправит, пугает человека зря (R-KM-F11).
  const showBadDaily = d.enabled && badMinutes(d.dailyMinutes);
  const showBadWarn = d.enabled && badWarn(d.warnMinutes);

  // Пикер помощника — только записи пула (Т6 не меняет): маршрут профиля валидируется без
  // согласий, облачное и локальное для него не открыты (ChildProfileValidator.cs:36-41,
  // forPool: true). Пикеры предметов (Т6) получают весь список: облачные и локальные с
  // предупреждением и галкой согласия (разрез §3). Значение вне списка (прежнее из профиля)
  // пикер держит «недоступным»: его видно, выбрать повторно нельзя, в тело оно уходит как есть.
  const poolRoutes = (routes ?? []).filter((r) => r.kind === 'pool');
  // Маршрут «Помощника (правила)» — он же маршрут нового предмета (проп defaultRoute Т4d).
  const assistantRoute: ChildRouteValue = { providerKey: d.providerKey, model: d.model };
  const selectedSubject = selectedId ? (d.subjects ?? []).find((s) => s.id === selectedId) ?? null : null;

  const save = async () => {
    if (saving) return; // два нажатия — один запрос
    if (d.enabled && (d.windows.some((w) => badTime(w.from) || badTime(w.to)) || badMinutes(d.dailyMinutes) || badWarn(d.warnMinutes))) {
      setAttempted(true); // подсветка полей, запроса нет
      // Подсветка живёт только на вкладке «Режим и время»: с другой вкладки человеку был бы
      // молчаливый no-op — возвращаем на вкладку с полями.
      setTab('mode');
      return;
    }
    const forId = user.id;
    setReject(null);
    setSaving(true);
    try {
      const res = await childApi.putProfile(forId, bodyOf(d));
      // Ответ прежнего пользователя: не применяется и не держит кнопку в загрузке —
      // у нового пользователя своего запроса нет (второй не стартует, пока saving), поэтому
      // снять флаг безопасно.
      if (activeIdRef.current !== forId) { setSaving(false); return; }
      setSaving(false);
      setAttempted(false);
      setSnap((s) => (s.id === forId ? {
        ...s,
        // Тьютора (и тьюторов предметов) и привязки знания берём из ответа: при первом включении
        // тьюторов создаёт сервер, живая привязка — его слияние, не черновик. Согласие сбрасываем
        // только когда сброс просили этим сохранением — дата не теряется сама.
        tutorPersonaId: res.tutorPersonaId ?? s.tutorPersonaId,
        rulesAgreedAt: d.resetRules ? null : s.rulesAgreedAt,
        draft: subjectApplied(s.draft, res.profile),
        // Предметы после сохранения — какие вернул ответ: новый предмет стал известным серверу,
        // его файлы грузятся; удалённого в ответе нет — он больше не «на сервере».
        serverSubjects: serverSubjectsOf(res.profile),
      } : s));
      onSaved(res.profile);
    } catch (e) {
      if (activeIdRef.current !== forId) { setSaving(false); return; }
      const err = e as { status?: number; body?: { code?: string; message?: string } } | null;
      const body = err?.body;
      const server = typeof body?.message === 'string' && body.message.trim() ? body.message.trim() : undefined;
      const code = typeof body?.code === 'string' ? body.code : undefined;
      // Отказ, называющий предмет, красит его строку и приводит человека туда, где эта строка
      // видна, — на вкладке «Режим и времени» красная рамка была бы не видна (тот же смысл,
      // что возврат на вкладку с невалидными полями времени).
      // Отказ знания называет базу, а не предмета — его ищем по новой привязке, а если message
      // называет базу в «ёлочках» — по ней (та же мера, что у валидатора); остальные отказы —
      // по id в кавычках.
      const subjectId = subjectIdOf(server, d.subjects)
        ?? (code === 'dataset_not_attachable' || code === 'knowledge_unavailable' ? knowledgeRejectSubjectId(d, serverSubjects, server) : undefined);
      setReject({ text: err?.status === 403 ? FORBIDDEN_TEXT : (code && REJECT_TEXT[code]) || REJECT_FALLBACK, server, subjectId });
      if (subjectId) setTab('subjects');
      setSaving(false);
    }
  };

  const rowGap = isMobile ? SP.sm : SP.md;
  const fieldStyle = { display: 'flex', flexDirection: 'column' as const, gap: rowGap };
  const panelStyle = {
    display: 'flex', flexDirection: 'column' as const, gap: SP.md,
    padding: isMobile ? `${SP.sm}px ${SP.md}px` : SP.md,
    border: `1px solid ${C.border}`, borderRadius: R.xl, background: C.bgCard, boxShadow: SHADOW.card,
  };
  const sectionTitle = (t: string) => (
    <span style={{ fontSize: FS.xs, fontWeight: 600, color: C.textSecondary, textTransform: 'uppercase', letterSpacing: '0.05em' }}>{t}</span>
  );
  const staticRow = (label: string, value: string, testid: string, mono?: boolean) => (
    <div data-testid={testid} style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
      <span style={{ fontSize: FS.sm, color: C.textMuted, minWidth: 120 }}>{label}</span>
      <span style={{ fontSize: FS.base, color: C.textPrimary, fontFamily: mono ? FONT.mono : 'inherit' }}>{value}</span>
    </div>
  );
  const dayChip = (day: number, i: number) => {
    const on = d.windows[i].days.includes(day);
    return (
      <Button
        key={day}
        variant={on ? 'ghostAccent' : 'ghost'}
        size="md"
        ariaPressed={on}
        ariaLabel={`День недели ${DAY_LABELS[day - 1]}`}
        onClick={() => patchWindow(i, (w) => ({ ...w, days: on ? w.days.filter((x) => x !== day) : [...w.days, day].sort() }))}
      >
        {DAY_LABELS[day - 1]}
      </Button>
    );
  };

  return (
    <div data-testid="child-profile-editor" style={panelStyle}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ fontSize: FS.lg, fontWeight: 600, color: C.textHeading }}>Детский режим</span>
        <span style={{ fontSize: FS.sm, color: C.textMuted }}>{user.username}</span>
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <Checkbox checked={d.enabled} onChange={(v) => patch((prev) => ({ ...prev, enabled: v }))} ariaLabel="Детский режим" />
        <span style={{ fontSize: FS.md, color: C.textPrimary }}>Детский режим</span>
      </div>
      <span style={{ fontSize: FS.sm, color: C.textMuted, marginTop: -SP.sm }}>
        {d.enabled
          ? 'Ограничения действуют с момента сохранения: окна, лимиты и только эта модель по API-ключу.'
          : 'Выключено: пользователь обычный, его ход не ограничен. Настройки ниже сохранятся, когда включишь режим.'}
      </span>

      {reject && (
        <Notice tone="warning" icon={AlertTriangle} title="Сохранить не удалось">
          <div>{reject.text}</div>
          {reject.server && <div style={{ marginTop: SP.xxs, opacity: 0.85 }}>{reject.server}</div>}
        </Notice>
      )}

      <div data-testid="profile-tabs">
        <Tabs
          transparent
          size="md"
          isMobile={isMobile}
          ariaLabel="Разделы профиля"
          value={tab}
          items={TAB_ITEMS}
          onChange={setTab}
        />
      </div>

      {tab === 'mode' && (<>
      <div style={fieldStyle}>
        {sectionTitle('Окно «Уроков»')}
        {d.windows.length === 0 && (
          <span style={{ fontSize: FS.sm, color: C.textMuted }}>Окон нет — время разрешено весь день, если хватит дневных минут.</span>
        )}
        {d.windows.map((w, i) => (
          <div key={i} data-testid="time-window" style={{ ...fieldStyle, padding: SP.sm, border: `1px solid ${C.borderLight}`, borderRadius: R.lg }}>
            <div data-testid="window-row" style={{ display: 'flex', gap: rowGap, flexDirection: isMobile ? 'column' : 'row' }}>
              <div style={{ flex: 1, minWidth: 0 }}>
                <Field label="С" error={showBadTime(w.from) ? 'Формат ЧЧ:ММ' : undefined}>
                  <TextField value={w.from} mono invalid={showBadTime(w.from)} inputMode="numeric" placeholder="09:00"
                    onChange={(v) => patchWindow(i, (prev) => ({ ...prev, from: v }))} />
                </Field>
              </div>
              <div style={{ flex: 1, minWidth: 0 }}>
                <Field label="По" error={showBadTime(w.to) ? 'Формат ЧЧ:ММ' : undefined} hint="Раньше «С» — окно через полночь">
                  <TextField value={w.to} mono invalid={showBadTime(w.to)} inputMode="numeric" placeholder="20:00"
                    onChange={(v) => patchWindow(i, (prev) => ({ ...prev, to: v }))} />
                </Field>
              </div>
              <Button variant="ghost" size="md" ariaLabel="Удалить окно" dataTestId={`window-remove-${i}`}
                onClick={() => patch((prev) => ({ ...prev, windows: prev.windows.filter((_, j) => j !== i) }))}>
                <Trash2 size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
              </Button>
            </div>
            <div style={{ display: 'flex', gap: SP.xs, flexWrap: 'wrap' }}>
              {DAY_LABELS.map((_, dayIdx) => dayChip(dayIdx + 1, i))}
            </div>
            <span style={{ fontSize: FS.sm, color: C.textMuted }}>
              {w.days.length ? `Дни: ${w.days.map((x) => DAY_LABELS[x - 1]).join(', ')}` : 'Дни не выбраны — каждый день'}
            </span>
          </div>
        ))}
        <Button variant="dashed" size="md" dataTestId="add-window" leftIcon={<Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />}
          onClick={() => patch((prev) => ({ ...prev, windows: [...prev.windows, { from: '', to: '', days: [] }] }))}>
          Добавить окно
        </Button>
        <Field label="Минут в день" hint="Пусто — дневного лимита нет" error={showBadDaily ? MINUTES_ERROR : undefined}>
          <TextField value={d.dailyMinutes} inputMode="numeric" invalid={showBadDaily} onChange={(v) => patch((prev) => ({ ...prev, dailyMinutes: v }))} placeholder="30" />
        </Field>
        <Field label="Предупредить за, мин" hint="Сколько остаётся, когда ребёнок видит предупреждение" error={showBadWarn ? WARN_ERROR : undefined}>
          <TextField value={d.warnMinutes} inputMode="numeric" invalid={showBadWarn} onChange={(v) => patch((prev) => ({ ...prev, warnMinutes: v }))} />
        </Field>
      </div>

      <div style={fieldStyle}>
        {sectionTitle('Бюджет')}
        <div style={{ display: 'flex', gap: rowGap, flexDirection: isMobile ? 'column' : 'row' }}>
          <div style={{ flex: 1, minWidth: 0 }}>
            <Field label="День, $" hint="Пусто — дневного бюджета нет">
              <TextField value={d.dailyBudget} inputMode="decimal" onChange={(v) => patch((prev) => ({ ...prev, dailyBudget: v }))} placeholder="2" />
            </Field>
          </div>
          <div style={{ flex: 1, minWidth: 0 }}>
            <Field label="Месяц, $" hint="Пусто — месячного бюджета нет">
              <TextField value={d.monthlyBudget} inputMode="decimal" onChange={(v) => patch((prev) => ({ ...prev, monthlyBudget: v }))} placeholder="40" />
            </Field>
          </div>
        </div>
      </div>

      <Field label="Опекуны (id через запятую)" hint="Уведомления о стоп-сигнале доходят и им">
        <TextField value={d.guardians} mono onChange={(v) => patch((prev) => ({ ...prev, guardians: v }))} placeholder="id-1, id-2" />
      </Field>

      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <Checkbox checked={d.resetRules} onChange={(v) => patch((prev) => ({ ...prev, resetRules: v }))} ariaLabel="Сбросить согласие с правилами" />
        <span style={{ fontSize: FS.md, color: C.textPrimary }}>Сбросить согласие с правилами</span>
      </div>

      <div style={fieldStyle}>
        {sectionTitle('Только чтение')}
        {staticRow('Персона-тьютор', snap.tutorPersonaId ?? 'ещё нет', 'tutor-state', true)}
        {staticRow('Правила договорены', snap.rulesAgreedAt ? fmtDate(snap.rulesAgreedAt) : 'ещё нет', 'rules-state')}
      </div>
      </>)}

      {tab === 'subjects' && (<>
      {/* Пока ответа нет, ни один пикер не может сказать правду о поставщиках: пустой список
          звучал бы «записей пула нет», чего ещё не знаем. */}
      {routes === null && (
        <span data-testid="routes-loading" style={{ fontSize: FS.sm, color: C.textMuted }}>Загружаю список моделей…</span>
      )}
      {routesError && (
        <Notice tone="danger" icon={AlertTriangle} title="Список моделей не загрузился" data-testid="routes-failed">
          {routesError}
          <div style={{ marginTop: SP.xxs, opacity: 0.85 }}>Предметы можно править и сохранить — выбрать модель сейчас нечем.</div>
        </Notice>
      )}
      {routes !== null && !routesError && (
      <div data-testid="assistant-route" style={fieldStyle}>
        {sectionTitle('Помощник (правила)')}
        {/* Подпись выбранного маршрута — у самого пикера (макет Д1, пикер (б)); отдельной
            строки «подключатся позже» больше нет: для предметов они уже подключены (Т6),
            для помощника — никогда (маршрут профиля валидируется без согласий). */}
        <ChildRoutePicker
          routes={poolRoutes}
          value={assistantRoute}
          onChange={(v) => patch((prev) => ({ ...prev, providerKey: v.providerKey, model: v.model }))}
          disabled={saving}
        />
      </div>
      )}

      {routes !== null && (<>
      <div data-testid="subjects-list">
        <ChildSubjectsEditor
          subjects={d.subjects ?? []}
          // Пикеры предметов (Т6) — весь список: облачные и локальные с предупреждением и
          // согласием. При отказе загрузки — пустой список плюс routesError: строки не врут
          // «пула нет», пикеры заглушены, как пикер помощника (ревью Т4f).
          routes={routes ?? []}
          routesError={!!routesError}
          onChange={patchSubjects}
          selectedId={selectedId}
          onSelect={setSelectedId}
          defaultRoute={assistantRoute}
          disabled={saving}
          errorId={reject?.subjectId}
        />
      </div>

      {/* Открытый предмет: карточка его репетитора, под ней — знания предмета (макет Д2):
          «База знаний», затем «Файлы предмета». У несохранённого предмета персоны ещё нет —
          карточка сама говорит «репетитор появится после сохранения». */}
      {selectedSubject && (<>
      <ChildTutorCard userId={user.id} personaId={selectedSubject.tutorPersonaId ?? ''} />
      <div data-testid="subject-knowledge" style={fieldStyle}>
        {sectionTitle('Знания предмета')}
        <ChildSubjectKnowledgePicker
          value={selectedSubject.knowledge?.datasetId ?? null}
          onChange={(datasetId) => patchSubjectKnowledge(selectedSubject.id, datasetId)}
          disabled={saving}
        />
        {/* Предмета, которого на сервере ещё нет: его опись файлов дала бы 404 — вместо блока
            строка о сохранении. База при этом правится: это поле профиля, не запись сервера. */}
        {serverSubjects.some((x) => x.id === selectedSubject.id) ? (
          <ChildSubjectMaterials userId={user.id} subjectId={selectedSubject.id} />
        ) : (
          <Notice tone="info" icon={Info} data-testid="subject-materials-unsaved">
            Файлы можно добавить после сохранения профиля.
          </Notice>
        )}
      </div>
      </>)}
      </>)}
      </>)}

      <Button variant="primary" size="md" fullWidth loading={saving} dataTestId="save-profile" onClick={save}>
        Сохранить
      </Button>
    </div>
  );
}

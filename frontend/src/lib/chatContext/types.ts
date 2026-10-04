// Контракты контекста хода на фронте (ADR-023, КТ-4): DTO сервера и кит видов.
// DTO зеркалит ChatContextDto (docs/adr/ADR-023-contracts.md); кит — ContextKindApi §Д1 с правками
// Дополнения 2 (нет mode, open, defaultAction; в DTO нет executor).

import type { ReactNode } from 'react';
import type { ExecutorRow } from '../../components/generation/ExecutorList';
import { CHAT_CONTEXT_EVENT } from './errors';

// ── DTO сервера ──

export type ContextActor = 'human' | 'agent';

interface ChatContextItemBase {
  // Непрозрачен: смысл знает только сервер
  id: string;
  kind: string;
  // Непрозрачный объект владельца вида ({threadId, versionId}, {slug}, {path}); всегда объект
  ref: Record<string, unknown>;
  by: ContextActor;
  addedAt: string;
  // Подписи считает бэкенд (Describe), фронт их не форматирует
  label: string;
  // Ключ есть всегда, значения может не быть
  version: string | null;
  thumb: string | null;
  missing: boolean;
}

// Основной объект: роли нет никогда
export interface ChatContextPrimary extends ChatContextItemBase { role: null }

// Референс: usedBy — операции основного объекта, которые его берут; пустой — серый чип
export interface ChatContextRef extends ChatContextItemBase {
  role: string | null;
  usedBy: string[];
}

export type ChatContextItem = ChatContextPrimary | ChatContextRef;

export interface ChatContextDto {
  revision: number;
  primary: ChatContextPrimary | null;
  refs: ChatContextRef[];
}

export const EMPTY_CONTEXT: ChatContextDto = { revision: 0, primary: null, refs: [] };

// Тело 409 context_changed
export interface ChatContextConflictDto { error: string; context: ChatContextDto }

// Событие SignalR; sessionId — базовое поле ServerMessage (чат-владелец)
export interface ChatContextChangedEvent {
  type: typeof CHAT_CONTEXT_EVENT;
  sessionId: string;
  context: ChatContextDto;
}

export interface SavedFileDto { path: string; threadKind: string; savedAt: string }

// Тело POST refs / PUT primary
export interface ContextRefInput { kind: string; ref: Record<string, unknown>; role?: string | null }

// ── Кит видов (слот context-kind) ──

export interface ContextKindCtx { projectId: string | null; sessionId: string; isMobile: boolean }

// То, от чего зависит набор действий: DTO основного объекта и локальное состояние вида
// (маска редактора, роли версий звука, снята ли сцена). Состав local вертикаль определяет сама
export interface KindState { primary: ChatContextPrimary; refs: readonly ChatContextRef[]; local?: Readonly<Record<string, unknown>> }

export type ActionKind = 'run' | 'editor' | 'menu';

export interface ContextMenuItem { id: string; label: string; disabledReason?: string; run: () => void }

// Значок чипа действия: закрытый набор имён, рисует хост
export type ContextActionIcon =
  | 'spark' | 'scissors' | 'maximize' | 'expand' | 'brush' | 'image' | 'mic' | 'users' | 'activity' | 'layers'
  | 'music' | 'link' | 'sliders' | 'film';

export interface ContextAction {
  // Стабильный на вид: 'edit', 'removeBg', 'stems', 'shoot', 'build'
  id: string;
  // run — режим (радио); editor — вход в редактор; menu — меню
  kind: ActionKind;
  label: string;
  icon?: ContextActionIcon;
  // Глагол хода: «Изменяем» → «✦ Изменяем hero.png… 40 %»; нет — «✦ Изменить…»
  verb?: string;
  // Тултип чипа
  hint: string;
  // Только у run: уже резолвленная операция вертикали ('inpaint', 'separate', 'shoot')
  op?: string;
  text?: 'required' | 'optional' | 'none';
  placeholder?: string;
  // Один вопрос под чипами, первое значение предвыбрано
  question?: { param: string; title: string; options: readonly { value: string; label: string }[] };
  // Серая кнопка с причиной
  disabledReason?: string;
  // Только у editor
  open?: () => void;
  // Только у menu
  items?: () => readonly ContextMenuItem[];
  // Только у menu: миниатюра выбранного источника на чипе. Поле есть, а адреса нет (null) — пустой квадрат
  thumb?: string | null;
}

// Закрытый набор параметров запуска: новые значения добавляются правкой типа, а не вкладом вертикали
export type LaunchParam =
  | { kind: 'variants'; min: 1; max: number; value: number }
  | { kind: 'duration'; options: readonly number[]; value: number }
  | { kind: 'aspect'; options: readonly string[]; value: string }
  | { kind: 'fromQuestion'; label: string };

export interface ExecutorListModel {
  rows: readonly ExecutorRow[];
  value: string;
  onChange: (id: string) => void;
}

// Роль, под которой объект входит референсом в основной (подпись — для меню «В контекст ▾»)
export interface ContextRole { role: string; label: string }

export interface ContextUpload {
  // Вид референса, под которым файл встаёт в контекст
  kind: string;
  accept: string;
  hint: string;
  roles: readonly ContextRole[];
  send: (file: File) => Promise<Record<string, unknown>>;
}

export interface ContextNote { label: string; hint: string; clear: () => void }

export interface ContextKindApi {
  // Те же строки, что у бэкенд-провайдера
  kinds: readonly string[];
  icon: (kind: string) => ReactNode;
  // Порядок важен: первое run без disabledReason включается само у объекта человека
  actions: (ctx: ContextKindCtx, s: KindState) => readonly ContextAction[];
  // Только картинка, волна, кадр
  preview: (ctx: ContextKindCtx, item: ChatContextItem) => ReactNode;
  // Адрес миниатюры объекта этого вида (версия нити и т.п.); нет — иконка вида
  thumb?: (ctx: ContextKindCtx, item: ChatContextItem) => string | null;
  // Вторая строка карточки «С чем»: «версия 2 из 3 · отмечено: 2»; нет — берётся version из DTO
  sub?: (ctx: ContextKindCtx, item: ChatContextItem) => string | null;
  editor?: (ctx: ContextKindCtx, item: ChatContextItem) => { label: string; hint: string; open: () => void } | null;
  // Листание версий основного объекта (‹ ›) в секции «С чем» панели; null/нет — стрелок нет, версия одна
  step?: (ctx: ContextKindCtx, item: ChatContextItem) => { prev: (() => void) | null; next: (() => void) | null } | null;
  // Строки «Чем» под выбранное run-действие. Хост зовёт на каждый рендер строки и панели: вид обязан
  // отдавать дешёвую чистую модель (строки каталога кэшируются у вертикали), а не строить её заново.
  // answer — выбранный ответ вопроса действия (question): у «Стемов» набор дорожек меняет, кого берёт «Авто»;
  // у действий без вопроса null
  executors?: (ctx: ContextKindCtx, actionId: string, answer?: string | null) => ExecutorListModel | null;
  params?: (ctx: ContextKindCtx, actionId: string) => readonly LaunchParam[];
  // Роли, под которыми объект вида candidateKind входит референсом в основной объект ЭТОГО вида. Зеркало
  // AcceptedRefs провайдера на бэкенде (он и принимает роль: чужая — 400 role_not_accepted). Пусто —
  // основной такой референс не берёт; одна роль — «В контекст» без вопроса
  refRoles?: (ctx: ContextKindCtx, primary: ChatContextPrimary, candidateKind: string) => readonly ContextRole[];
  // «С компьютера» в «Добавить из…» панели: файл с диска ложится в рабочую папку вида и встаёт референсом
  // (`send` возвращает ref). Роли — те же, что у refRoles; null — основной объект файлов не берёт
  upload?: (ctx: ContextKindCtx, primary: ChatContextPrimary) => ContextUpload | null;
  // Метка состояния основного объекта в панели кнопок поля (десктоп): «Отмечено: 2 ✕»
  note?: (ctx: ContextKindCtx, primary: ChatContextPrimary) => ContextNote | null;
  // «Из ленты чата» в «Добавить из…»: объекты этого вида, что есть в ленте и могут встать референсом
  feed?: (ctx: ContextKindCtx) => readonly { id: string; label: string; hint?: string; candidate: { kind: string; ref: Record<string, unknown> } }[];
  // Вход «＋» композера и empty-state ленты
  create?: { title: string; hint?: string; icon: ReactNode; run: (ctx: ContextKindCtx) => void };
  // Запуск действия: op + params + contextRevision; входы бэкенд читает из стора по ревизии
  launch?: (ctx: ContextKindCtx, req: LaunchRequest) => Promise<LaunchHandle>;
  // Состояние вида, которое меняет цену, не меняя op и параметры (отметки на холсте): входит в ключ цены
  priceSalt?: (ctx: ContextKindCtx, actionId: string) => string;
  // Цена по op действия
  quote?: (ctx: ContextKindCtx, req: QuoteRequest) => Promise<ActionQuote>;
}

export type LaunchParams = Readonly<Record<string, string | number | boolean>>;

// salt — состояние вида вне op и параметров, от которого зависит цена (отметки на холсте): входит в ключ цены
export interface QuoteRequest { op: string; text: string; params: LaunchParams; contextRevision: number; salt?: string }
export type LaunchRequest = QuoteRequest;

export interface ActionQuote {
  // Подпись цены: «$0.12», «бесплатно»
  price: string;
  // Расшифровка для тултипа и низа панели
  detail?: string;
}

// Дескриптор запущенной работы: прогресс и результат ведёт вертикаль
export interface LaunchHandle {
  id: string;
  // Подписка на прогресс 0..1 и итог; отписка — возвращаемая функция
  watch: (on: (e: { progress?: number; result?: ActionResult; error?: string; cancelled?: boolean }) => void) => () => void;
  // «Остановить» в низу панели; нет — остановить нельзя (правка без ИИ идёт одним запросом)
  cancel?: () => void | Promise<void>;
}

export interface ActionResult { summary: string; open?: () => void }

// Ссылка «назад» к прежнему основному объекту (замена returnTo, §Д1)
export interface ContextReturn { prev: ChatContextItem; label: string }

// Предвыбор действия (замена preset, §Д1)
export interface ActionPreset { actionId: string; prefill?: string; params?: LaunchParams }

export type RunState = 'idle' | 'running' | 'done' | 'error';

// Результат useActionRun(sessionId): один на кнопку поля и низ панели
export interface ActionRun {
  action: ContextAction | null;
  // Подпись кнопки: «✦ Изменить · 3 вар. · $0.12»
  label: string;
  // Та же подпись двумя кусками: имя ужимается, хвост с ценой — никогда
  labelParts: { name: string; tail: string };
  quote: ActionQuote | null;
  state: RunState;
  progress: number | null;
  result: ActionResult | null;
  run: (text: string) => Promise<void>;
  // Остановить идущий запуск; null — не идёт или остановка недоступна
  stop: (() => void) | null;
  // Текст поля ввода: его публикует поле, панель запускает с ним же
  text: string;
  setText: (text: string) => void;
  // Ответ на вопрос выбранного действия (первое значение предвыбрано); null — вопроса нет
  answer: string | null;
  // Почему кнопка серая («Напишите текст: …»); null — запускать можно
  blocked: string | null;
  // Параметры вида с текущими значениями; правятся через setParam (имя — kind параметра или
  // question.param)
  params: readonly LaunchParam[];
  setParam: (name: string, value: number | string) => void;
}

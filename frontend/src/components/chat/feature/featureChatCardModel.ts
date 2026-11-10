// FS-FCm · карточка фичи в шапке чата (макет feature-page-simplify-v5-chat-header, §2–§3, §6):
// ЧИСТАЯ модель — две строки FeatureChatCard без DOM, запросов и новой семантики. Компонент
// (FS-FC) рисует только по этой модели; подписи «Нужно от тебя / Что мешает», текст вопроса
// и «часы стоят» добавляет FS-FCb.
//
// Вход — строка индекса (FeatureIndexFeature) и run чата (FeatureRun | null): features/{id}
// не читаем (записка §6). Все величины — бэковые (run.stepProgress / фолбэк индекса
// stepProgressFromIndex / epic.note.estimate через featureEstimateView); фронт не пересчитывает
// — как в FeatureBanner. Фраза строится из featureRowModel (общие слова строки, не копия):
// приоритет тот же — тревога → закрыта → «Сейчас» → слово состояния.
//
// Инвариант макета §3: в чате, где ждут или стоит, карточка не имеет кнопки перехода
// в этот же чат: wait → «Ответить здесь» (компонент фокусирует поле ввода),
// stopped → действия нет вовсе («Перезапустить шаг» уже стоит в ленте). «К мержу» ведёт
// на страницу фичи, не в чат, — остаётся и в своём чате (toMerge — маршрут компонента).

import { featurePageStatusTag, type FeatureRunTag } from '../../feature/FeatureStatusTag';
import { formatDuration } from '../../feature/FeatureStatsBits';
import { plural } from '../../../lib/plural';
import { stepProgressFromIndex } from '../../../lib/featureProgress';
import { featureRowModel } from './FeatureBanner';
import { featureEstimateView } from './featureTruthModel';
import type { FeatureIndexFeature } from '../../../types/featureIndex';
import type { FeatureRun } from '../../../types/featureRun';

// Глиф карточки (макет §2 «Элемент → компонент», «info / warning / danger / success по
// состоянию»): warning — ждёт/ждёт согласия, danger — стоит, success — закрыта, info — всё
// остальное (идёт/очередь/пауза/черновик). Компонент рисует соответствующий FeatureGlyph.
export type ChatCardGlyph = 'info' | 'warning' | 'danger' | 'success';

export interface FeatureChatCardPhrase {
  // «Нужно от тебя» / «Что мешает» — FS-FCb (нужен бэковый ask); в этом шаге всегда null.
  label: string | null;
  text: string;
  tone: 'muted' | 'warn' | 'danger';
  // Имя, которое компонент выделает <b> внутри текста («Сейчас {Имя} — …»); null — имени нет.
  name: string | null;
}

export interface FeatureChatCardAction {
  label: string;
  // Чат тревоги (feature.attention.sessionId); null — чата нет (компонент ведёт на страницу).
  sessionId: string | null;
  // true — кнопка ведёт на страницу фичи («К мержу»), не в чат.
  toMerge: boolean;
}

export interface FeatureChatCardModel {
  title: string;
  glyph: ChatCardGlyph;
  // Тег статуса строки 1 (тот же, что в шапке страницы v4); на мобиле тега нет (макет §2:
  // «тон фразы ниже уже говорит о состоянии»).
  tag: FeatureRunTag | null;
  // «6 из 44»; у закрытой с итогом — «12 шагов»; null — чисел нет (старые ответы).
  count: string | null;
  // «≈ 2 д» по оценке оркестратора; у закрытой с итогом — длительность; без run — null.
  eta: string | null;
  phrase: FeatureChatCardPhrase;
  // Единственный источник кнопки тревоги карточки (компонент сам ничего не выводит).
  action: FeatureChatCardAction | null;
  isKeyChat: boolean;
  keySessionId: string | null;
  showTracker: boolean;
}

export function featureChatCardModel(
  feature: FeatureIndexFeature, run: FeatureRun | null,
  sessionId: string, isMobile: boolean, nowMs: number,
): FeatureChatCardModel {
  const closed = feature.state === 'merged' || feature.state === 'closed';
  // Модель строки — существующая (FeatureBanner): слова тревоги/«Сейчас»/состояния общие.
  const row = featureRowModel(feature, run, isMobile);

  const glyph: ChatCardGlyph =
    feature.state === 'stopped' ? 'danger'
      : feature.state === 'waiting' || feature.state === 'merge_pending' ? 'warning'
        : closed ? 'success'
          : 'info';

  // Тег — общий с шапкой страницы (featurePageStatusTag); очередь/пауза — по hold-полям
  // индекса (holdReason/queue), своей копии слов нет.
  const tag = isMobile
    ? null
    : featurePageStatusTag(feature.state, null, { holdReason: feature.holdReason, queue: feature.queue });

  // Счёт и срок. Закрытая с итогом — «N шагов · длительность» (run.summary, как «Итоги»
  // страницы); иначе — корзины бэка (run.stepProgress, фолбэк индекс); нет чисел — строки
  // нет (не нули). Итога у закрытой нет (старые ответы) — честный фолбэк по корзинам.
  const summary = closed ? run?.summary ?? null : null;
  const sp = summary ? null : run?.stepProgress ?? stepProgressFromIndex(feature);
  const count = summary
    ? `${summary.steps} ${plural(summary.steps, 'шаг', 'шага', 'шагов')}`
    : sp ? `${sp.accepted} из ${sp.total}` : null;
  const eta = summary
    ? formatDuration(summary.durationMinutes)
    : run ? featureEstimateView(run, nowMs)?.rowEta ?? null : null;

  // Фраза: приоритет тот же, что у featureRowModel (тревога → закрыта → «Сейчас» → состояние).
  let phrase: FeatureChatCardPhrase;
  if (row.alarm) {
    // Тревога — тоном целиком (имя уже внутри alarmText, отдельного <b> нет).
    phrase = { label: null, text: row.alarmText, tone: row.alarm === 'wait' ? 'warn' : 'danger', name: null };
  } else if (closed) {
    // «Смержена 19 октября» — тот же формат даты, что у «Итогов» страницы (FeatureClosedSummary);
    // даты мержа нет — слово состояния («закрыта без мержа»), выдуманной даты нет.
    const at = summary?.mergedAt ? new Date(summary.mergedAt) : null;
    phrase = {
      label: null,
      text: at && !Number.isNaN(at.getTime())
        ? `Смержена ${at.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' })}`
        : `${row.statusWord ?? ''}${row.statusWordPost}`,
      tone: 'muted', name: null,
    };
  } else if (row.nowName) {
    // «Сейчас {Имя} — {шаг} +N»; на мобиле без шага (как нынешняя строка).
    phrase = {
      label: null,
      text: `Сейчас ${row.nowName}${!isMobile && row.nowWhat ? ` — ${row.nowWhat}` : ''}${row.nowRest > 0 ? ` +${row.nowRest}` : ''}`,
      tone: 'muted', name: row.nowName,
    };
  } else {
    phrase = { label: null, text: `${row.statusWord ?? ''}${row.statusWordPost}`, tone: 'muted', name: null };
  }

  // Действие тревоги: цель — чат тревоги. В этом чате: wait → «Ответить здесь», stopped → null
  // (инвариант §3). «К мержу» — переход на страницу, не в чат: инвариант его не касается.
  let action: FeatureChatCardAction | null = null;
  if (row.alarm) {
    const target = feature.attention?.sessionId ?? null;
    const toMerge = row.actionLabel === 'К мержу';
    if (!toMerge && target === sessionId) {
      action = row.alarm === 'stop' ? null : { label: 'Ответить здесь', sessionId, toMerge: false };
    } else {
      action = { label: row.actionLabel, sessionId: target, toMerge };
    }
  }

  return {
    title: feature.title,
    glyph,
    tag,
    count,
    eta,
    phrase,
    action,
    isKeyChat: feature.keySessionId != null && feature.keySessionId === sessionId,
    keySessionId: feature.keySessionId ?? null,
    // Полосы нет только у закрытой (макет §5: «закрытой — 65/71, полосы нет»).
    showTracker: !closed,
  };
}

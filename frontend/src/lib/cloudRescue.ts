// Общий модуль «Сделать в облаке» (cloud rescue) для трёх редакторов:
// картинка, звук, видео. Выносит сюда то, что три gpuCard.tsx копировали
// по отдельности: guard двойного клика, проверку «стенд лежит», выбор облачного
// исполнителя и честный текст диалога. Редакторо-специфичные части (сборка
// параметров, вызов запуска) остаются в своих файлах.
//
// Источник правды по тексту — docs/mockups/local-model-stats-media-cards.html.

import type { LocalGpuQueueInfo } from '../types/localGpuQueue';

// ── Guard двойного клика ──

// Защита от двойного клика по запуску в облаке: guard выставляется синхронно,
// до первого await, поэтому второй клик в окне микротаска (пока ре-рендер ещё не
// заблокировал кнопку) не запускает задачу повторно. begin() — true, если клик
// принят, false — повторный.
export function makeLaunchGuard() {
  let inFlight = false;
  return {
    begin: () => { if (inFlight) return false; inFlight = true; return true; },
    end: () => { inFlight = false; },
  };
}

// ── Стенд лежит ──

// Стенд лежит: живой снимок очереди несёт downSince (unix-мс, не ISO).
// gpu=null или downSince=null/undefined → стенд не лежит.
export const isStandDown = (gpu: LocalGpuQueueInfo | null): boolean =>
  gpu != null && gpu.downSince != null;

// ── Облачный исполнитель ──

// Интерфейс облачного исполнителя: общий минимум, который нужен для проверки
// видимости кнопки. Каждый редактор имеет свой тип каталога/поставщика, но
// проверка «доступен и не локальные» — общая.
export interface CloudProviderInfo {
  key: string;
  available: boolean;
}

// Проверяет, является ли поставщик облачным (не локальные) и доступным.
// Ключ «local» — единственная метка локальных моделей во всех трёх редакторах.
export function isCloudProvider(p: CloudProviderInfo | null | undefined): p is CloudProviderInfo {
  return p != null && p.available && p.key !== 'local';
}

// ── Общий выбор облачного исполнителя (m4: единая проверка для трёх редакторов) ──

// Предикаты, которые каждый редактор подставляет под свой тип каталога/поставщика:
// available — поставщик доступен и не локальные; supports — поставщик умеет операцию.
// Редакторы различаются типами, поэтому проверку «доступен и умеет» выносят сюда, а сами
// предикаты передают: единая логика, без копирования.
export interface CloudCandidatePredicates<T> {
  available: (p: T) => boolean;
  supports: (p: T, op: string | null) => boolean;
}

// Первый поставщик в заданном порядке, для которого верны оба предиката. Нет подходящего — null.
// Порядок providers задаёт вызывающий (для звука/видео — порядок «Авто», для картинки — умолчание).
export function cloudCandidate<T extends { key: string }>(
  providers: readonly T[], op: string | null, pred: CloudCandidatePredicates<T>,
): T | null {
  for (const p of providers) {
    if (pred.available(p) && pred.supports(p, op)) return p;
  }
  return null;
}

// ── Текст диалога ──

// Честная формулировка: локальная задача НЕ отменяется, она продолжит ждать.
// Слова «отмен…» не должно быть — мутация «вернуть старую строку» красит тест.
export const CLOUD_DIALOG_TITLE = 'Сделать в облаке?';
export function cloudDialogSubtitle(priceLabel: string | null): string {
  return priceLabel
    ? `Стоимость ${priceLabel}. Локальная задача продолжит ждать.`
    : 'Локальная задача продолжит ждать.';
}

// Подпись кнопки: «Сделать в облаке · ≈ $0.04» или «Сделать в облаке · ≈ …»
export function cloudButtonLabel(priceLabel: string | null): string {
  return `Сделать в облаке · ${priceLabel ?? '≈ …'}`;
}

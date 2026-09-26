// Раскладка редактора v2 (макет docs/mockups/image-editor-v2.html): секции левой панели
// с состоянием «свёрнута / раскрыта» на пользователя и потолки авторазмера поля промпта.

export const SECTION_IDS = ['marks', 'samples', 'chars', 'quick', 'adjust', 'model', 'hist'] as const;
export type SectionId = typeof SECTION_IDS[number];
export type SectionState = Record<SectionId, boolean>;

// По умолчанию открыты «Пометки», «Образцы», «Быстрые действия», «История»;
// «Правка без ИИ» (шаг 7) — свёрнута
export const DEFAULT_SECTIONS: SectionState = {
  marks: true, samples: true, chars: false, quick: true, adjust: false, model: false, hist: true,
};

// Ключ хранения — на пользователя: в одном браузере могут работать разные люди
export const sectionsStorageKey = (userId: string | null) => `cc-image-editor-sections:${userId ?? 'anon'}`;

// Сохранённое состояние поверх умолчаний: неизвестные ключи и мусор игнорируются,
// новые секции берут значение по умолчанию
export function parseSections(raw: string | null): SectionState {
  const out = { ...DEFAULT_SECTIONS };
  if (!raw) return out;
  try {
    const saved = JSON.parse(raw) as unknown;
    if (!saved || typeof saved !== 'object') return out;
    for (const id of SECTION_IDS) {
      const v = (saved as Record<string, unknown>)[id];
      if (typeof v === 'boolean') out[id] = v;
    }
  } catch { /* битое значение — умолчания */ }
  return out;
}

export const toggleSection = (s: SectionState, id: SectionId): SectionState => ({ ...s, [id]: !s[id] });

// Поле промпта растёт вместе с текстом до потолка, дальше прокручивается внутри
export const PROMPT_MIN_H = { desktop: 64, mobile: 42 } as const;
export const PROMPT_MAX_H = { desktop: 240, mobile: 132 } as const;

export function promptHeight(scrollHeight: number, mobile: boolean): number {
  const k = mobile ? 'mobile' : 'desktop';
  return Math.min(Math.max(scrollHeight, PROMPT_MIN_H[k]), PROMPT_MAX_H[k]);
}

// Подсказка «прокрутите ↓» — только когда текст не влез под потолок
export const promptOverflows = (scrollHeight: number, mobile: boolean) =>
  scrollHeight > PROMPT_MAX_H[mobile ? 'mobile' : 'desktop'];

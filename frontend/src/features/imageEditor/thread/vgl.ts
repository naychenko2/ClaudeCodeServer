// Доводка версии Fibo 2.0 по JSON-описанию сцены (VGL, Ш8): поля панели и их текущие значения.
// Ключи — те же, что у сервера (Fibo2VglFields): светом, ракурсом, объектом, фоном и стилем
// JSON-описания управляет расширитель, сюда они приходят только для показа «сейчас».

export type VglField = 'light' | 'camera' | 'subject' | 'background' | 'style';

export const VGL_FIELDS: { key: VglField; label: string; placeholder: string }[] = [
  { key: 'light', label: 'Свет', placeholder: 'например: закат, тёплый боковой свет' },
  { key: 'camera', label: 'Ракурс', placeholder: 'например: снизу, широкий угол' },
  { key: 'subject', label: 'Одежда / цвет', placeholder: 'например: синее платье' },
  { key: 'background', label: 'Фон', placeholder: 'например: летний пляж' },
  { key: 'style', label: 'Стиль', placeholder: 'например: акварель' },
];

// Потолок значения — как у сервера (Fibo2VglFields.MaxValueLength)
export const VGL_MAX_VALUE = 300;

type Json = Record<string, unknown>;

const obj = (v: unknown): Json | null => (v && typeof v === 'object' && !Array.isArray(v) ? v as Json : null);
const text = (v: unknown): string => (typeof v === 'string' ? v.trim() : '');
const join = (...parts: string[]) => parts.filter(Boolean).join('; ');

export function parseVgl(json: string): Json | null {
  try {
    return obj(JSON.parse(json));
  } catch {
    return null;
  }
}

// Что сейчас в описании по полю панели; пустая строка — в описании этого нет
export function vglFieldValue(scene: Json | null, field: VglField): string {
  if (!scene) return '';
  switch (field) {
    case 'light': {
      const l = obj(scene.lighting);
      return l ? join(text(l.conditions), text(l.direction)) : '';
    }
    case 'camera': {
      const p = obj(scene.photographic_characteristics);
      return join(text(p?.camera_angle), text(p?.lens_focal_length), text(obj(scene.aesthetics)?.composition));
    }
    case 'subject': {
      const main = Array.isArray(scene.objects) ? obj(scene.objects[0]) : null;
      return main ? text(main.clothing) || text(main.shape_and_color) : '';
    }
    case 'background':
      return text(scene.background_setting);
    case 'style':
      return join(text(scene.style_medium), text(scene.artistic_style));
  }
}

// Подпись запуска для карточки и журнала: «Доводка: свет → закат»
export function refineLabel(field: VglField, value: string): string {
  const label = VGL_FIELDS.find(f => f.key === field)?.label ?? field;
  return `Доводка: ${label.toLowerCase()} → ${value.trim()}`;
}

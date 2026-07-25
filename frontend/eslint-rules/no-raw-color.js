/**
 * Правило дизайн-системы: запрет сырых hex-цветов в коде фронта.
 *
 * Цвета берутся ТОЛЬКО из токенов `C.*` (src/lib/design.ts) — это CSS-переменные,
 * значения обеих тем лежат в src/lib/theme.css. Захардкоженный hex молча ломает
 * тёмную тему: он не переключается вместе с остальным интерфейсом.
 * Полная конвенция — docs/design-guidelines.md.
 *
 * Легальные исключения (темы сторонних редакторов, палитры-данные, canvas-рендер)
 * вынесены списком файлов в eslint.config.js. Разовое отклонение оформляется
 * построчно и С ПРИЧИНОЙ:
 *   // eslint-disable-next-line design/no-raw-color -- подклад под фото, вне темы
 */

// Валидные длины CSS-hex: #RGB, #RGBA, #RRGGBB, #RRGGBBAA.
// Хвостовой lookahead отсекает совпадения внутри более длинных слов (#facebook и т.п.).
const HEX = /#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})(?![0-9a-zA-Z_-])/;

const MESSAGE =
  "Сырой цвет «{{color}}»: используй токен C.* из lib/design.ts (значения тем — в theme.css; " +
  'новый цвет заводится в ОБЕ темы). Осознанное исключение — eslint-disable-next-line ' +
  'design/no-raw-color с причиной.';

export default {
  meta: {
    type: 'problem',
    docs: { description: 'Запрещает сырые hex-цвета вне дизайн-токенов' },
    schema: [],
    messages: { rawColor: MESSAGE },
  },
  create(context) {
    const report = (node, text) => {
      const m = HEX.exec(text);
      if (m) context.report({ node, messageId: 'rawColor', data: { color: m[0] } });
    };
    return {
      // Строковые литералы и JSX-атрибуты: color: '#fff', stroke="#FFF"
      Literal(node) {
        if (typeof node.value === 'string') report(node, node.value);
      },
      // Куски шаблонных строк: `1px solid #E0D7C8`
      TemplateElement(node) {
        report(node, node.value.raw ?? '');
      },
    };
  },
};

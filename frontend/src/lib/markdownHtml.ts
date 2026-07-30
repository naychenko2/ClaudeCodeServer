// Белый список HTML для markdown-рендера (MarkdownViewer).
//
// Сырой HTML в документах исполняется (rehype-raw), но только после санитайза этой схемой.
// Зачем вообще: README проектов почти всегда центрируют логотип и скриншоты через
// <div align="center"><img …></div>, а без rehype-raw это показывалось как текст.
//
// Источник недоверенный ВСЕГДА: тем же рендером идут ответы модели, содержимое чужих
// заметок и файлы репозитория. Поэтому расширяем defaultSchema точечно — тем, что
// встречается в README, — и не трогаем запреты: script, style, iframe, form, обработчики
// on* и javascript:-ссылки остаются вырезанными.
//
// Отдельным модулем, а не внутри компонента: схема — это правило безопасности, и оно
// должно проверяться тестом без запуска React.

import { defaultSchema } from 'rehype-sanitize';

export const HTML_SCHEMA = {
  ...defaultSchema,
  tagNames: [...(defaultSchema.tagNames ?? []), 'details', 'summary', 'sub', 'sup', 'picture', 'source'],
  // Внутренние схемы ссылок продукта (вики-переходы, embed, вложения заметок) —
  // иначе санитайз вырезал бы href/src у всего, что рендерит режим заметок
  protocols: {
    ...defaultSchema.protocols,
    href: [...(defaultSchema.protocols?.href ?? []), 'wikilink', 'noteembed', 'noteatt'],
    src: [...(defaultSchema.protocols?.src ?? []), 'noteatt', 'data'],
  },
  attributes: {
    ...defaultSchema.attributes,
    div: [...(defaultSchema.attributes?.div ?? []), 'align'],
    p: [...(defaultSchema.attributes?.p ?? []), 'align'],
    h1: ['align'], h2: ['align'], h3: ['align'], h4: ['align'],
    img: [...(defaultSchema.attributes?.img ?? []), 'align', 'width', 'height'],
    a: [...(defaultSchema.attributes?.a ?? []), 'target', 'rel'],
    details: ['open'],
    source: ['srcSet', 'media', 'type'],
    // Выравнивание ячеек приходит из markdown-таблиц — его сохраняем
    th: [...(defaultSchema.attributes?.th ?? []), 'align'],
    td: [...(defaultSchema.attributes?.td ?? []), 'align'],
  },
};

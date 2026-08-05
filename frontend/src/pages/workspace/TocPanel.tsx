// Панель «Оглавление»: разделы markdown-документа, открытого в ЦЕНТРАЛЬНОЙ области.
//
// Разграничение с соседями: у «Документации» оглавление своё — поповер над её
// собственным превью, для чтения «по месту» в узкой колонке. Здесь — оглавление того,
// что читают крупно в центре, и оно нужно постоянно, пока документ открыт, а не на
// время клика по кнопке. Строка списка при этом общая (ui/TocRow): роль одна.
//
// Данных панель не грузит вовсе: заголовки и действия над ними приходят готовыми от
// самого просмотрщика (DocToc) — он один знает свой скроллер и свой исходный markdown.
// Отсюда и жизненный цикл: закрыли документ — просмотрщик отдал null, панель исчезла.
import { TableOfContents } from 'lucide-react';
import { C, FONT, FS, SP } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { EmptyState, TocRow } from '../../components/ui';
import { prefillComposer } from '../../lib/ai/startChat';
import type { DocToc } from '../../hooks/useHeadings';

interface Props {
  toc: DocToc;
}

export function TocPanel({ toc }: Props) {
  // Раздел в чат цитатой — правым кликом по строке, как в оглавлении «Документации».
  // Текст ложится в ПУСТОЕ поле композера: набранный черновик важнее
  const quote = (text: string, section: string | null) => {
    if (!section) return;
    prefillComposer(`Вопрос по разделу «${text}» документа ${toc.path}:\n\n${section}\n\n`);
  };

  if (toc.headings.length === 0)
    return (
      <EmptyState
        compact
        icon={<TableOfContents size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
        title="Без заголовков"
        subtitle="В этом документе нет разделов, к которым можно перейти"
      />
    );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      {/* Путь документа над списком: панель живёт у края экрана, вдали от шапки
          просмотрщика, и без него неясно, чьё это оглавление — особенно когда рядом
          открыта «Документация» со своим документом */}
      <div
        title={toc.path}
        style={{
          flexShrink: 0, padding: `${SP.xs}px ${SP.sm}px`,
          fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          // Направление обрезки: у длинного пути важен хвост (имя файла), а не корень
          direction: 'rtl', textAlign: 'left',
        }}
      >
        {toc.path}
      </div>
      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: `0 ${SP.xs}px ${SP.xs}px` }}>
        {toc.headings.map((h, i) => (
          <TocRow
            key={i}
            text={h.text}
            level={h.level}
            onJump={() => toc.jump(h)}
            onQuote={() => quote(h.text, toc.sectionOf(h))}
          />
        ))}
      </div>
    </div>
  );
}

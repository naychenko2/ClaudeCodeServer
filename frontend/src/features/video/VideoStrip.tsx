import { useCallback, useLayoutEffect, useRef, useState } from 'react';
import { MoreHorizontal, Radio, StarOff } from 'lucide-react';
import type { VideoChannel } from '../../types';
import { Button, Menu, MenuItem, MenuSep, usePanelHeaderHold } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { C, FONT, FS, R, SP } from '../../lib/design';
import { toggleFavorite, useFavoriteKeys, useStripChannels } from '../../lib/videoFavorites';
import { fitStrip, STRIP_GAP } from '../../lib/videoStrip';

/**
 * Полоса избранных каналов — переключатель эфира в шапке панели, центрального острова
 * и плавающего окна. Три места показа, один компонент: расходиться им нельзя, иначе
 * канал, переключённый в окне, назывался бы в панели иначе.
 *
 * Показываются ТОЛЬКО избранные. Полный каталог живёт в центральном острове: полоса —
 * быстрый переключатель между своими каналами, а не второй список всего на свете.
 *
 * Что не влезло по ширине, уходит под «⋯» — кнопка появляется только при переполнении.
 * Ширины кнопок меряются по-настоящему, скрытым измерителем: названия каналов разной
 * длины, и прикидка «по числу символов» врёт на «Смотрим 100% Классика» против «ОТР».
 */
export function VideoStrip({ activeId, onPick, onOpenCatalog }: {
  /** Что играет сейчас: этот канал виден в полосе ВСЕГДА, даже если по порядку он в хвосте. */
  activeId: string | null;
  onPick: (c: VideoChannel) => void;
  /** Открыть каталог: единственный путь добавить канал в избранное. undefined — путь занят. */
  onOpenCatalog?: () => void;
}) {
  const channels = useStripChannels();
  const { keys, configured } = useFavoriteKeys();
  const [menuAnchor, setMenuAnchor] = useState<DOMRect | null>(null);
  // Пока попап открыт, контролы шапки не гаснут: курсор уходит на карточку меню
  // (она рисуется порталом в body), и кнопка, которой меню открыли, исчезала бы под ним
  usePanelHeaderHold(!!menuAnchor);

  const boxRef = useRef<HTMLDivElement | null>(null);
  const measureRef = useRef<HTMLDivElement>(null);
  const [boxW, setBoxW] = useState(0);
  const [widths, setWidths] = useState<number[]>([]);
  const [moreW, setMoreW] = useState(28);

  // Доступная ширина полосы. Меряем КОНТЕЙНЕР, а не сам список: список сжимается по
  // содержимому, и его ширина после скрытия кнопок стала бы новым «доступным местом» —
  // раскладка зациклилась бы, показывая то три кнопки, то две.
  //
  // Наблюдение вешается CALLBACK-ССЫЛКОЙ, а не эффектом с пустыми зависимостями:
  // полоса живёт в шапке порталом, её узел пересоздаётся вместе со слотом, и
  // подписанный однажды ResizeObserver остался бы висеть на выброшенном узле —
  // ширина навсегда осталась бы нулевой, а «⋯» не появилась бы никогда.
  const roRef = useRef<ResizeObserver | null>(null);
  const attachBox = useCallback((el: HTMLDivElement | null) => {
    roRef.current?.disconnect();
    roRef.current = null;
    boxRef.current = el;
    if (!el) return;
    const ro = new ResizeObserver(() => setBoxW(el.clientWidth));
    ro.observe(el);
    roRef.current = ro;
    setBoxW(el.clientWidth);
  }, []);

  // Замер на КАЖДЫЙ рендер, без списка зависимостей. Причина та же, что у callback-ссылки:
  // контролы шапки в покое свёрнуты, первый замер приходится на нулевую ширину, а
  // события об их появлении может не прийти вовсе. Расхождение с состоянием — редкость,
  // поэтому лишних рендеров это не даёт: обновляем только при реальном изменении.
  // Цикла это не даёт: состояние пишется только при РАСХОЖДЕНИИ с замером, а нулевой
  // замер (свёрнутая шапка) отбрасывается вовсе.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useLayoutEffect(() => {
    const el = measureRef.current;
    const box = boxRef.current;
    if (box) {
      const w = box.clientWidth;
      if (w > 0) setBoxW(prev => (prev === w ? prev : w));
    }
    if (!el) return;
    const items = [...el.querySelectorAll<HTMLElement>('[data-measure]')];
    const next = items.map(i => Math.ceil(i.getBoundingClientRect().width));
    // Негодный замер (шапка свёрнута) не сохраняем: нулевые ширины «влезают» куда угодно
    if (next.length === 0 || next.some(w => w <= 0)) return;
    setWidths(prev => (prev.length === next.length && prev.every((w, i) => w === next[i]) ? prev : next));
    const more = el.querySelector<HTMLElement>('[data-measure-more]');
    const mw = more ? Math.ceil(more.getBoundingClientRect().width) : 0;
    if (mw > 0) setMoreW(prev => (prev === mw ? prev : mw));
  });

  const pick = useCallback((c: VideoChannel) => {
    setMenuAnchor(null);
    onPick(c);
  }, [onPick]);

  // Набор ещё не приехал с сервера — рисовать нечего: мигнуть чужим составом и тут же
  // сменить его на свой хуже, чем показаться четвертью секунды позже
  if (!channels || !keys) return null;

  // Звёздочки сняты со всех каналов осознанно: полоса пуста, но обязана оставаться
  // путём назад — иначе переключать нечем и непонятно, куда делись каналы
  if (channels.length === 0) {
    if (!configured || !onOpenCatalog) return null;
    return <Button size="sm" variant="ghost" onClick={onOpenCatalog}>Выбрать каналы</Button>;
  }

  const activeIndex = channels.findIndex(c => c.id === activeId);
  const fit = widths.length === channels.length && boxW > 0
    ? fitStrip(widths, boxW, moreW, activeIndex)
    // Пока не измерились — показываем всё: один кадр с переполнением незаметен,
    // а пустая полоса на первом рендере читалась бы как «каналов нет»
    : { visible: channels.map((_, i) => i), hidden: [] };

  return (
    <div ref={attachBox} style={{
      flex: '1 1 auto', minWidth: 0, display: 'flex', alignItems: 'center', gap: STRIP_GAP,
    }}>
      {fit.visible.map(i => (
        <ChannelButton
          key={channels[i].id}
          channel={channels[i]}
          active={channels[i].id === activeId}
          onClick={() => pick(channels[i])}
        />
      ))}

      {fit.hidden.length > 0 && (
        <div style={{ position: 'relative', flex: 'none' }}>
          <button
            title={'Ещё каналы (' + fit.hidden.length + ')'}
            aria-label="Ещё каналы"
            onClick={e => setMenuAnchor(
              menuAnchor ? null : (e.currentTarget as HTMLElement).getBoundingClientRect())}
            style={{ ...stripButtonStyle(false), padding: '0 ' + SP.xs + 'px' }}
          >
            <MoreHorizontal size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          </button>

          {menuAnchor && (
            <Menu anchor={menuAnchor} onClose={() => setMenuAnchor(null)} minWidth={220} maxHeight={360}>
              {fit.hidden.map(i => (
                <MenuItem
                  key={channels[i].id}
                  label={channels[i].title}
                  // Пометка «идёт сейчас» — значок ЭФИРА, а не звезда: звезда в этой же
                  // строке справа означает «убрать из избранного», и один символ в двух
                  // смыслах читается как «этот канал избранный, а соседние — нет»
                  icon={channels[i].id === activeId
                    ? <Radio size={13} strokeWidth={ICON_STROKE} />
                    : undefined}
                  onClick={() => pick(channels[i])}
                  // Снять звёздочку прямо отсюда: перебрать избранное, не открывая
                  // каталог, — ровно то, ради чего в попап заглядывают второй раз
                  action={{
                    icon: <StarOff size={13} strokeWidth={ICON_STROKE} />,
                    title: 'Убрать из избранного',
                    onClick: () => { void toggleFavorite(channels[i]); },
                  }}
                />
              ))}
              {onOpenCatalog && (
                <>
                  <MenuSep />
                  <MenuItem
                    label="Все каналы и лента"
                    onClick={() => { setMenuAnchor(null); onOpenCatalog(); }}
                  />
                </>
              )}
            </Menu>
          )}
        </div>
      )}

      {/* Невидимый измеритель: кнопки в естественную ширину, вне потока и вне доступности */}
      <div
        ref={measureRef}
        aria-hidden
        style={{
          position: 'absolute', left: -9999, top: 0, display: 'flex', gap: STRIP_GAP,
          visibility: 'hidden', pointerEvents: 'none',
        }}
      >
        {channels.map(c => (
          <span key={c.id} data-measure style={{ ...stripButtonStyle(false), display: 'inline-flex' }}>
            {c.title}
          </span>
        ))}
        <span
          data-measure-more
          style={{ ...stripButtonStyle(false), padding: '0 ' + SP.xs + 'px', display: 'inline-flex' }}
        >
          <MoreHorizontal size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </span>
      </div>
    </div>
  );
}

function ChannelButton({ channel, active, onClick }: {
  channel: VideoChannel;
  active: boolean;
  onClick: () => void;
}) {
  return (
    <button
      onClick={onClick}
      title={channel.nowPlaying ? channel.title + ' — ' + channel.nowPlaying : channel.title}
      aria-label={channel.title}
      aria-pressed={active}
      style={stripButtonStyle(active)}
    >
      {channel.title}
    </button>
  );
}

/** Кнопка полосы. Стиль общий с измерителем — иначе замер разойдётся с реальностью. */
function stripButtonStyle(active: boolean): React.CSSProperties {
  return {
    flex: 'none', display: 'flex', alignItems: 'center', height: 22,
    padding: '0 ' + SP.xs + 'px', cursor: 'pointer',
    background: active ? C.bgSelected : 'transparent',
    border: '1px solid ' + (active ? C.accentMuted : 'transparent'),
    borderRadius: R.sm, fontFamily: FONT.sans, fontSize: FS.xs,
    color: active ? C.textHeading : C.textMuted, whiteSpace: 'nowrap',
  };
}

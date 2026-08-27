import { useState, type CSSProperties, type ReactNode } from 'react';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';

/**
 * Карточка сетки раздела: телеканал и ролик ленты устроены одинаково — обложка 16:9,
 * название, подпись под ним. Один компонент на оба случая намеренно: копия карточки
 * успела разъехаться по размеру заголовка, и рядом на экране это читалось как две
 * сетки из разных приложений.
 */
export function VideoCard({ coverUrl, fallbackIcon, badge, title, subtitle, note, hint, onClick }: {
  coverUrl: string | null;
  /** Что показать, когда обложки нет ИЛИ она не открылась. */
  fallbackIcon: ReactNode;
  /** Значок в углу обложки: сорт карточки (играем у себя / уводим наружу). */
  badge?: ReactNode;
  title: string;
  subtitle: string;
  /** Мелкая пометка справа от названия — например, что канал откроется на чужом сайте. */
  note?: string;
  /** Подсказка при наведении. */
  hint: string;
  onClick: () => void;
}) {
  const [hovered, setHovered] = useState(false);
  // Битая ссылка на обложку — обычное дело (превью протухают, до чужого CDN может не
  // быть доступа): показываем тот же запасной значок, а не «сломанную картинку» браузера
  const [broken, setBroken] = useState(false);

  return (
    <button
      // cc-card-shadow ДО cc-card-press: тень задаётся классом через переменную, иначе
      // inline-boxShadow перебивал бы тень нажатия — и в режиме уменьшенной анимации,
      // где сжатие выключено, карточка не отзывалась бы на нажатие вовсе
      className="cc-card-shadow cc-card-press"
      onClick={onClick}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
      // Та же рамка при переходе по Tab: общей подсветки фокуса в продукте нет,
      // и с клавиатуры не было видно, на какой карточке стоишь
      onFocus={() => setHovered(true)}
      onBlur={() => setHovered(false)}
      title={hint}
      // На тач-устройствах всплывающей подсказки нет вовсе, а читалке экрана от title
      // пользы мало — дублируем доступным именем
      aria-label={hint}
      style={{
        display: 'flex', flexDirection: 'column', alignItems: 'stretch',
        background: C.bgWhite, borderRadius: R.lg,
        border: `1px solid ${hovered ? C.accentMuted : C.border}`,
        padding: 0, overflow: 'hidden', cursor: 'pointer', textAlign: 'left',
        fontFamily: FONT.sans,
        '--cc-card-shadow': SHADOW.card,
      } as CSSProperties}
    >
      <div style={{
        position: 'relative', aspectRatio: '16 / 9', background: C.bgPanel,
        display: 'flex', alignItems: 'center', justifyContent: 'center', overflow: 'hidden',
      }}>
        {coverUrl && !broken
          ? <img src={coverUrl} alt="" loading="lazy" onError={() => setBroken(true)}
              style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
          : fallbackIcon}

        {badge && (
          <div style={{
            position: 'absolute', right: SP.sm, bottom: SP.sm,
            width: SP.xxl, height: SP.xxl, borderRadius: R.full,
            // Тёмная плашка одной и той же плотности в обеих темах: под ней логотип
            // канала любого цвета, и темозависимый фон на белом логотипе исчезал
            background: C.mediaScrim, color: C.onDark,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}>
            {badge}
          </div>
        )}
      </div>

      <div style={{
        padding: `${SP.sm}px ${SP.md}px ${SP.md}px`,
        display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0,
      }}>
        <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.xs, minWidth: 0 }}>
          <div style={{
            flex: 1, minWidth: 0,
            fontSize: FS.base, fontWeight: 500, color: C.textHeading,
            display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden',
          }}>
            {title}
          </div>
          {note && (
            <span style={{ flex: 'none', fontSize: FS.xs, color: C.textMuted }}>{note}</span>
          )}
        </div>
        <div style={{
          fontSize: FS.sm, color: C.textMuted, lineHeight: 1.3, minHeight: '1.3em',
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
        }}>
          {subtitle}
        </div>
      </div>
    </button>
  );
}

import { useEffect, useState, type CSSProperties } from 'react';
import { C, FONT, FS, R, SHADOW, SP } from '../../lib/design';
import { api } from '../../lib/api';
import { setImageGenerationSnapshot } from '../../lib/imageGeneration';
import { useIsMobile } from '../../lib/breakpoints';
import type { ImageGenerationSettings, ImageGeneratorInfo, ImagePlaceSettings } from '../../types';

// Секция «Картинки» вкладки «Применение»: чем рисуется каждое МЕСТО — иконка проекта и
// аватар персоны настраиваются отдельно (требования к картинке разные). У места свой
// генератор (Автоматически / glif / fal.ai) и своя модель. Настройка общая для инстанса,
// поэтому правит её только админ; остальным показываем то же в режиме чтения
// (GET /api/image-generation открыт всем).

// Псевдоключ режима «Автоматически» — совпадает с ImageGenerationOptions.Auto на бэке
export const AUTO = 'auto';

// Модель выбираем только у fal: у остальных генератор подбирает её сам
export const MODEL_PICKER_PROVIDER = 'fal';

// Чего не хватает выключенному провайдеру. Бэкенд отдаёт только enabled, поэтому
// подсказку собираем здесь — как для Ollama на этой же вкладке.
const SETUP_HINT: Record<string, string> = {
  fal: 'Не настроен — впишите Fal:ApiKey в appsettings.Local.json',
  glif: 'Не настроен — впишите Glif:McpToken в appsettings.Local.json',
};

export function setupHint(key: string): string {
  return SETUP_HINT[key] ?? 'Не настроен — добавьте ключ доступа в appsettings.Local.json';
}

// Строка места по ключу; null — сервер такого места не прислал
export function placeOf(s: ImageGenerationSettings, key: string): ImagePlaceSettings | null {
  return s.places.find(p => p.key.toLowerCase() === key.toLowerCase()) ?? null;
}

// Эффективная модель провайдера в этом месте (ключи словаря сверяем без учёта регистра)
export function modelOf(place: ImagePlaceSettings, providerKey: string): string | null {
  const hit = Object.entries(place.models).find(([k]) => k.toLowerCase() === providerKey.toLowerCase());
  return hit?.[1] ?? null;
}

// Пикер модели активен только при явно выбранном fal — иначе модель за генератором
export function canPickModel(place: ImagePlaceSettings): boolean {
  return place.provider.toLowerCase() === MODEL_PICKER_PROVIDER;
}

// Оптимистичный снимок «после сохранения режима места»: activeProvider пересчитываем так
// же, как Resolve на бэке (явный выбор — он сам, auto — первый включённый в порядке
// ответа), иначе до ответа сервера подпись и селект модели показывали бы прежнего.
export function withPlaceProvider(
  s: ImageGenerationSettings, placeKey: string, provider: string): ImageGenerationSettings {
  return mapPlace(s, placeKey, place => {
    const active = provider === AUTO
      ? (s.providers.find(p => p.enabled)?.key ?? null)
      : (s.providers.find(p => p.key === provider && p.enabled)?.key ?? null);
    return {
      ...place,
      provider,
      activeProvider: active,
      enabled: active !== null,
      model: active ? modelOf(place, active) : null,
    };
  });
}

// Оптимистичный снимок «после сохранения модели»: пустая строка — сброс к дефолту драйвера
export function withPlaceModel(
  s: ImageGenerationSettings, placeKey: string, providerKey: string, model: string): ImageGenerationSettings {
  return mapPlace(s, placeKey, place => {
    const next = model || null;
    const isActive = !!place.activeProvider
      && place.activeProvider.toLowerCase() === providerKey.toLowerCase();
    return {
      ...place,
      models: { ...place.models, [providerKey]: next },
      model: isActive ? next : place.model,
    };
  });
}

function mapPlace(s: ImageGenerationSettings, placeKey: string,
  fn: (p: ImagePlaceSettings) => ImagePlaceSettings): ImageGenerationSettings {
  return {
    ...s,
    places: s.places.map(p => p.key.toLowerCase() === placeKey.toLowerCase() ? fn(p) : p),
  };
}

interface ImageGenSectionProps {
  isAdmin: boolean;
  titleStyle: CSSProperties;
}

export function ImageGenSection({ isAdmin, titleStyle }: ImageGenSectionProps) {
  const [settings, setSettings] = useState<ImageGenerationSettings | null | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const isMobile = useIsMobile();

  useEffect(() => {
    let alive = true;
    api.imageGeneration.get()
      .then(s => { if (alive) setSettings(s); setImageGenerationSnapshot(s); })
      .catch(() => { if (alive) setSettings(null); });
    return () => { alive = false; };
  }, []);

  async function save(place: string, patch: Parameters<typeof api.imageGeneration.savePlace>[1],
    optimistic: ImageGenerationSettings) {
    const prev = settings;
    setBusy(true); setError(null);
    setSettings(optimistic);
    try {
      const saved = await api.imageGeneration.savePlace(place, patch);
      setSettings(saved);
      // Открытые диалоги иконки/аватара подписаны на общий снимок — подпись обновится сразу
      setImageGenerationSnapshot(saved);
    } catch (e) {
      setSettings(prev);
      setError(e instanceof Error ? e.message : 'Не удалось сохранить');
    } finally {
      setBusy(false);
    }
  }

  if (settings === undefined) {
    return (
      <div>
        <div style={titleStyle}>Картинки</div>
        <div style={{ color: C.textMuted, fontSize: FS.sm, padding: '2px' }}>Загрузка…</div>
      </div>
    );
  }
  if (settings === null) {
    return (
      <div>
        <div style={titleStyle}>Картинки</div>
        <div style={hintBoxStyle}>Не удалось получить настройку генерации картинок.</div>
      </div>
    );
  }

  const falProvider = settings.providers.find(p => p.key === MODEL_PICKER_PROVIDER) ?? null;

  return (
    <div>
      <div style={titleStyle}>Картинки</div>
      <div style={{
        background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.lg,
        padding: `${SP.md}px ${SP.md}px`, display: 'flex', flexDirection: 'column', gap: SP.md,
      }}>
        <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45 }}>
          Иконка проекта и аватар персоны настраиваются отдельно: требования к картинке разные.
          «Автоматически» берёт первый настроенный сервис и подстраховывает следующим;
          явный выбор фолбэка не даёт.
        </div>

        {settings.places.map((place, i) => (
          <div key={place.key} style={{
            display: 'flex', flexDirection: 'column', gap: SP.sm,
            paddingTop: i === 0 ? 0 : SP.md,
            borderTop: i === 0 ? 'none' : `1px solid ${C.borderLight}`,
          }}>
            <div style={{ fontSize: FS.base, fontWeight: 700, color: C.textHeading }}>{place.title}</div>

            <div style={{
              display: 'grid', gap: SP.sm,
              gridTemplateColumns: isMobile ? '1fr' : `repeat(${settings.providers.length + 1}, 1fr)`,
            }}>
              <GenCard
                active={(place.provider || AUTO) === AUTO}
                disabled={!isAdmin || busy}
                title="Автоматически"
                desc={place.activeProvider
                  ? `Сейчас пойдёт ${nameOf(settings, place.activeProvider)}`
                  : 'Ни один сервис не настроен'}
                onClick={() => save(place.key, { provider: AUTO }, withPlaceProvider(settings, place.key, AUTO))}
              />
              {settings.providers.map(p => (
                <GenCard
                  key={p.key}
                  active={place.provider === p.key}
                  disabled={!isAdmin || busy || !p.enabled}
                  title={p.displayName}
                  desc={p.enabled ? providerDesc(p, place) : setupHint(p.key)}
                  onClick={() => save(place.key, { provider: p.key }, withPlaceProvider(settings, place.key, p.key))}
                />
              ))}
            </div>

            {/* Модель: активна только при явно выбранном fal — у остальных её выбирает генератор */}
            <label style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
              <span style={{ fontSize: FS.sm, color: C.textSecondary }}>Модель</span>
              {canPickModel(place) && falProvider ? (
                <select
                  value={modelOf(place, MODEL_PICKER_PROVIDER) ?? ''}
                  disabled={!isAdmin || busy}
                  onChange={e => save(
                    place.key,
                    { models: { [MODEL_PICKER_PROVIDER]: e.target.value || '' } },
                    withPlaceModel(settings, place.key, MODEL_PICKER_PROVIDER, e.target.value))}
                  style={selectStyle(!isAdmin || busy)}
                >
                  <option value="">По умолчанию</option>
                  {falProvider.models.map(m => (
                    <option key={m.id} value={m.id}>{m.displayName}</option>
                  ))}
                </select>
              ) : (
                <select disabled value="" style={selectStyle(true)}>
                  <option value="">Генератор подберёт модель сам</option>
                </select>
              )}
            </label>

            {!place.enabled && (
              <div style={hintBoxStyle}>
                Здесь генерация недоступна: выбранный сервис не настроен. Картинку придётся загрузить вручную.
              </div>
            )}
          </div>
        ))}

        {!isAdmin && (
          <div style={{ fontSize: FS.xs, color: C.textMuted }}>
            Генератор картинок общий для всех — его выбирает администратор.
          </div>
        )}
        {error && (
          <div style={{ padding: '7px 10px', borderRadius: R.sm, fontSize: FS.sm,
            color: C.dangerText, background: C.dangerBg, border: `1px solid ${C.dangerBorder}` }}>{error}</div>
        )}
      </div>
    </div>
  );
}

const hintBoxStyle: CSSProperties = {
  padding: '9px 11px', borderRadius: R.md, fontSize: FS.sm, lineHeight: 1.5,
  color: C.textSecondary, background: C.bgInset, border: `1px solid ${C.border}`,
};

function selectStyle(disabled: boolean): CSSProperties {
  return {
    font: 'inherit', fontFamily: FONT.sans, fontSize: FS.sm, padding: '5px 9px',
    borderRadius: R.md, border: `1px solid ${C.border}`, background: C.bgWhite,
    color: C.textHeading, maxWidth: '100%', cursor: disabled ? 'default' : 'pointer',
  };
}

function nameOf(s: ImageGenerationSettings, key: string): string {
  return s.providers.find(p => p.key.toLowerCase() === key.toLowerCase())?.displayName ?? key;
}

// Подпись карточки провайдера в этом месте: модель показываем только там, где её выбирают
function providerDesc(p: ImageGeneratorInfo, place: ImagePlaceSettings): string {
  if (p.key !== MODEL_PICKER_PROVIDER) return 'Модель подберёт сам';
  const model = modelOf(place, p.key);
  if (!model) return 'Модель по умолчанию';
  return `Модель: ${p.models.find(m => m.id === model)?.displayName ?? model}`;
}

// Карточка выбора — та же форма, что у StrategyCard в ApplyTab, но компактнее:
// выбор генератора стоит рядом со стратегией и не должен спорить с ней по весу.
function GenCard({ active, disabled, title, desc, onClick }: {
  active: boolean; disabled: boolean; title: string; desc: string; onClick: () => void;
}) {
  return (
    <button type="button" onClick={onClick} disabled={disabled}
      style={{
        textAlign: 'left', font: 'inherit', cursor: disabled ? 'default' : 'pointer',
        background: C.bgWhite, border: `1px solid ${active ? C.accent : C.border}`, borderRadius: R.xl,
        padding: '10px 11px', opacity: disabled && !active ? 0.55 : 1,
        boxShadow: active ? SHADOW.focus : 'none',
      }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm }}>
        <span style={{
          width: 14, height: 14, borderRadius: R.full, border: `2px solid ${active ? C.accent : C.dashed}`,
          flexShrink: 0, background: active ? C.accent : 'transparent',
          boxShadow: active ? `inset 0 0 0 2px ${C.bgWhite}` : 'none',
        }} />
        <span style={{ fontSize: FS.base, fontWeight: 700, color: C.textHeading,
          overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{title}</span>
      </div>
      <div style={{ fontSize: FS.xs, color: C.textMuted, lineHeight: 1.45, marginTop: SP.xs }}>{desc}</div>
    </button>
  );
}

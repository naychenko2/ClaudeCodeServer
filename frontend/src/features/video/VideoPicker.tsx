import { useCallback, useEffect, useRef, useState } from 'react';
import { Loader2, MonitorOff, PlugZap, RefreshCw } from 'lucide-react';
import type { VideoChannel, VideoError, VideoItem, VideoProviderInfo } from '../../types';
import { Button, IconButton, PanelHeaderSlot } from '../../components/ui';
import { ICON_SIZE, ICON_STROKE } from '../../components/ui/icons';
import { PillSwitch } from '../../components/Toolbar';
import { C, FONT, FS, SP } from '../../lib/design';
import { api } from '../../lib/api';
import { getVideoStage, setPanelChannel, setVideoStage } from '../../lib/videoStage';
import { ChannelGrid } from './ChannelGrid';
import { FeedGrid } from './FeedGrid';

/**
 * Каталог телеканалов и лента подписок — содержимое ЦЕНТРАЛЬНОГО острова.
 *
 * Сначала это был раздел хаба, потом панель рельсы. И то и другое мимо: раздел
 * уводил из работы, а в узкой панели не разглядеть обложки — а выбирают канал
 * именно глазами. Место каталога там же, где открывается файл: рядом с чатом,
 * во всю ширину острова.
 *
 * Контролы каталог отдаёт наверх штатным PanelHeaderSlot: остров даёт тот же слот,
 * что и панель рельсы, поэтому «Обновить» живёт в ШАПКЕ, а тело остаётся целиком
 * под обложки. Своего механизма «поднять кнопку в шапку» заводить не пришлось.
 */
export function VideoPicker() {
  const [providers, setProviders] = useState<VideoProviderInfo[] | null>(null);
  const [active, setActive] = useState<string | null>(null);
  const [channels, setChannels] = useState<VideoChannel[]>([]);
  const [items, setItems] = useState<VideoItem[]>([]);
  const [error, setError] = useState<VideoError | null>(null);
  const [loading, setLoading] = useState(false);

  const current = providers?.find(p => p.key === active) ?? null;

  useEffect(() => {
    let alive = true;
    void (async () => {
      try {
        const list = await api.video.providers();
        if (!alive) return;
        setProviders(list);
        setActive(list[0]?.key ?? null);
      } catch {
        if (alive) setProviders([]);
      }
    })();
    return () => { alive = false; };
  }, []);

  // Возврат из согласия Google приводит в «Чаты» с меткой в адресе: раздела «Видео»
  // больше нет, и показать результат может только каталог — перечитываем источники,
  // чтобы вкладка перестала просить вход.
  useEffect(() => {
    if (!window.location.hash.includes('connect=ok')) return;
    // Метку убираем СРАЗУ: иначе каталог, открытый позже в той же сессии, снова
    // дёрнет источники и насильно переставит вкладку поверх выбора человека.
    const clean = window.location.hash.replace(/[?&]connect=\w+/, '');
    window.history.replaceState(window.history.state, '', clean || '#/chats');
    void (async () => {
      const list = await api.video.providers().catch(() => null);
      if (list) { setProviders(list); setActive('youtube'); }
    })();
  }, []);

  // Поколение запроса: быстрое переключение вкладок иначе даёт кашу — медленный ответ
  // прежнего источника приезжает вторым и перетирает содержимое нового.
  const generation = useRef(0);

  const load = useCallback(async (key: string, kind: 'live' | 'feed', refresh = false) => {
    const mine = ++generation.current;
    const stale = () => generation.current !== mine;
    setLoading(true);
    setError(null);
    try {
      if (kind === 'live') {
        const res = await api.video.channels(key, refresh);
        if (stale()) return;
        setChannels(res.channels); setItems([]); setError(res.error);
      } else {
        const res = await api.video.feed(key, undefined, refresh);
        if (stale()) return;
        setItems(res.items); setChannels([]); setError(res.error);
      }
    } catch {
      if (stale()) return;
      setError('unreachable'); setChannels([]); setItems([]);
    } finally {
      if (!stale()) setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (current) void load(current.key, current.kind);
  }, [current, load]);

  // Выбранное уходит в БОКОВУЮ ПАНЕЛЬ, а каталог остаётся открытым: выбор канала —
  // не конец дела, а перебор, и закрывать ради него единственное место, где видно
  // весь список, незачем. Исключение — плавающее окно: если смотрят в нём, канал
  // подхватывает оно (окно и есть «где смотрят»).
  const play = (c: VideoChannel) => {
    if (getVideoStage()?.mode === 'float') setVideoStage(c, 'float');
    else setPanelChannel(c);
  };

  const pickChannel = (c: VideoChannel) => {
    if (!c.embeddable || !c.embedUrl) {
      if (c.externalUrl) window.open(c.externalUrl, '_blank', 'noopener,noreferrer');
      return;
    }
    play(c);
  };

  const pickItem = (i: VideoItem) => {
    play({
      id: i.id, provider: i.provider, title: i.title,
      embeddable: true, embedUrl: i.embedUrl, externalUrl: i.externalUrl,
      coverUrl: i.thumbnailUrl, nowPlaying: i.channelTitle,
    });
  };

  const connect = async () => {
    try {
      const { url } = await api.video.youtubeAuthUrl();
      window.location.href = url;
    } catch { setError('not-configured'); }
  };

  return (
    <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', background: C.bgWhite }}>
      {current && (
        <PanelHeaderSlot>
          <IconButton
            size="sm"
            title="Обновить"
            onClick={() => load(current.key, current.kind, true)}
            disabled={loading}
          >
            <RefreshCw size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
          </IconButton>
        </PanelHeaderSlot>
      )}

      {/* Источники — переключатель вида: «что показываем». Ему место у названия,
          поэтому левый слот шапки, а не полоса над сеткой */}
      {providers && providers.length > 1 && (
        <PanelHeaderSlot side="left">
          <PillSwitch<string>
            value={active ?? ''}
            onChange={setActive}
            options={providers.map(p => ({ value: p.key, label: p.title }))}
          />
        </PanelHeaderSlot>
      )}

      <div style={{ flex: 1, minHeight: 0, overflowY: 'auto', padding: SP.sm }}>
        {!providers || (loading && channels.length === 0 && items.length === 0)
          ? <Notice icon={<Loader2 size={ICON_SIZE.lg} strokeWidth={ICON_STROKE}
              style={{ animation: 'cc-spin 0.8s linear infinite' }} />} text="Загружаю…" />
          : current?.needsAuth || error === 'needs-auth'
            ? <Notice
                icon={<PlugZap size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
                text={`Подключите аккаунт ${current?.title ?? ''}`}
                action={<Button size="sm" onClick={connect}>Подключить</Button>}
              />
          : error
            ? <Notice icon={<MonitorOff size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
                text={errorText(error)}
                action={current
                  ? <Button variant="ghost" size="sm" onClick={() => load(current.key, current.kind, true)}>Повторить</Button>
                  : undefined} />
          : current?.kind === 'live'
            ? <ChannelGrid channels={channels} onPlay={pickChannel} />
            : <FeedGrid items={items} onPlay={pickItem} />}
      </div>
    </div>
  );
}

function errorText(e: VideoError): string {
  if (e === 'quota-exceeded') return 'YouTube больше не отдаёт ленту сегодня';
  if (e === 'not-configured') return 'Источник не настроен';
  return 'Сервис не отвечает';
}

function Notice({ icon, text, action }: { icon: React.ReactNode; text: string; action?: React.ReactNode }) {
  return (
    <div style={{
      display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center',
      gap: SP.sm, padding: SP.xl, color: C.textMuted, fontFamily: FONT.sans, fontSize: FS.sm,
      textAlign: 'center',
    }}>
      {icon}
      <span>{text}</span>
      {action}
    </div>
  );
}

import { useState } from 'react';
import { Lock, Unlock } from 'lucide-react';
import type { Project, Session } from '../../types';
import { C, FONT, R, TB } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Menu, Toggle } from '../ui';
import { api, ChatVisibilityError } from '../../lib/api';
import { readStoredUserId } from '../../lib/offline';
import { isSharedProject } from '../../features/projects/members/messageAuthor';
import { showToast } from '../../lib/toast';

// Тумблер «Приватный чат» (S2d-F2): чат общего проекта можно закрыть от участников —
// виден только автору. Тот же паттерн, что у DossierOptOutButton: настройка живёт
// у самого чата (кнопка в ряду шапки + поповер с тумблером), а не только при создании.
//
// Границы — зеркало бэка (ChatsController.SetVisibility, S2d-7a), чтобы человек не
// кликал отказ:
//  • виден только автору чата (createdByUserId === я) в общем проекте; читателю — нет
//    вовсе (бэк 403 reader), в личном проекте — нет (приватность нечего держать);
//  • включить можно только пока в чате нет сообщений — иначе тумблер неактивен с
//    подсказкой (бэк 409 chat_has_messages);
//  • выключить — в любой момент; после возврата в общий приватность уже не вернуть
//    (тот же 409) — об этом сноска в поповере.

export const PRIVATE_CHAT_LABEL = 'Приватный чат — видите только вы';
export const PRIVATE_CHAT_NEEDS_EMPTY = 'Сделать приватным можно только до первого сообщения';

// Есть ли в чате переписка: признак из ленты сильнее списка (messageCount в нём
// может отставать); запасные поля — чтобы не открыть по старому снимку
function hasChatMessages(session: Session, hasMessages: boolean): boolean {
  return hasMessages || (session.messageCount ?? 0) > 0 || !!session.lastMessage;
}

// Чей это чат и можно ли показывать тумблер (та же граница, что у бэкового гейта)
export function canShowPrivateChatToggle(
  session: Pick<Session, 'createdByUserId' | 'privateToUserId'>,
  project: Pick<Project, 'myRole'> | null | undefined,
  myUserId: string | null,
): boolean {
  if (!myUserId || !project) return false;
  if (!isSharedProject(project)) return false;
  if (project.myRole === 'reader') return false;
  return !!session.createdByUserId && session.createdByUserId === myUserId;
}

// Приватен ли чат МНЕ (author'у): privateToUserId — id автора, сравнение с моим
export function isPrivateChatTo(session: Pick<Session, 'privateToUserId'>, myUserId: string | null): boolean {
  return !!myUserId && !!session.privateToUserId && session.privateToUserId === myUserId;
}

export function PrivateChatToggle({ session, project, hasMessages, online, isMobile, onSessionUpdated }: {
  session: Session;
  project: Project | null | undefined;
  // Есть ли переписка (из ленты, как в ChatHeaderBar) — плюс запасные поля чата
  hasMessages: boolean;
  online: boolean;
  isMobile?: boolean;
  onSessionUpdated?: (s: Session) => void;
}) {
  const myUserId = readStoredUserId();
  // Якорь поповера — rect кнопки, снятый в момент клика (общая идиома Menu в проекте)
  const [menu, setMenu] = useState<DOMRect | null>(null);
  const [saving, setSaving] = useState(false);

  if (!online || !canShowPrivateChatToggle(session, project, myUserId)) return null;

  const privateOn = isPrivateChatTo(session, myUserId);
  // Включить нельзя: переписка уже есть (бэк ответил бы 409). Выключить — можно всегда
  const blocked = !privateOn && hasChatMessages(session, hasMessages);

  const persist = async (next: boolean) => {
    if (next === privateOn || saving) return;
    setSaving(true);
    try {
      // Ответ бэка — полный Session с новой видимостью: он и становится истиной шапки
      onSessionUpdated?.(await api.chats.setChatVisibility(session.id, next));
    } catch (e) {
      // 409/403 разобраны в api (ChatVisibilityError) — человек читает текст, не код
      showToast('Приватный чат',
        e instanceof ChatVisibilityError ? e.message : 'Не удалось изменить видимость чата', 'info');
    } finally {
      setSaving(false);
    }
  };

  const title = privateOn
    ? 'Этот чат видите только вы. Нажмите, чтобы изменить.'
    : blocked ? PRIVATE_CHAT_NEEDS_EMPTY : 'Сделать этот чат видимым только вам';

  return (
    <div style={{ position: 'relative', flexShrink: 0 }}>
      <button
        type="button"
        // rect снимаем ДО setMenu: в ленивом апдейтере currentTarget события уже null
        onClick={e => {
          const rect = e.currentTarget.getBoundingClientRect();
          setMenu(m => m ? null : rect);
        }}
        disabled={saving}
        title={title}
        style={{
          display: 'flex', alignItems: 'center', gap: 5, padding: '3px 8px',
          background: privateOn ? C.bgWhite : 'transparent',
          border: `1px solid ${privateOn ? C.border : 'transparent'}`,
          borderRadius: R.lg, flexShrink: 0, cursor: saving ? 'default' : 'pointer',
          fontFamily: FONT.sans, fontSize: 11, fontWeight: 600, whiteSpace: 'nowrap',
          // Тот же тон, что у соседних кнопок шапки: режим читается обводкой и
          // подписью, а не accent'ом (accent-дисциплина гайда, как у DossierOptOut)
          color: TB.iconColor,
          opacity: saving ? 0.6 : 1,
        }}
      >
        {privateOn
          ? <Lock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
          : <Unlock size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />}
        {/* Подпись — только на десктопе у приватного чата: в узкой мобильной шапке
            она распирает ряд, а замок уже сигнализирует режим */}
        {privateOn && !isMobile && <span style={{ color: C.textMuted }}>Приватный</span>}
      </button>
      {menu && (
        <Menu onClose={() => setMenu(null)} anchor={menu} minWidth={isMobile ? 260 : 300} maxHeight={240}>
          {/* Меню не закрываем по переключению: сноска про необратимость должна
              прочитаться ДО решения, а не после */}
          <div style={{ padding: '8px 10px 10px' }}>
            <div>
              <div style={{ display: 'flex', alignItems: 'flex-start', gap: 12 }}>
                {/* Вся подпись кликабельна — на тах удобнее тапнуть по тексту, чем по тумблеру */}
                <div
                  role="button"
                  tabIndex={-1}
                  onClick={() => !blocked && !saving && void persist(!privateOn)}
                  style={{
                    flex: 1, minWidth: 0, fontSize: 13, fontWeight: 600, color: C.textHeading,
                    lineHeight: 1.35, paddingTop: 2, cursor: blocked ? 'default' : 'pointer',
                    fontFamily: FONT.sans, opacity: blocked ? 0.6 : 1,
                  }}
                >
                  {PRIVATE_CHAT_LABEL}
                </div>
                <Toggle checked={privateOn} disabled={blocked || saving} onChange={v => void persist(v)}
                  focusable ariaLabel={PRIVATE_CHAT_LABEL} />
              </div>
              {blocked && (
                <p style={{ margin: '8px 0 0', fontSize: 11.5, color: C.textMuted, lineHeight: 1.45 }}>
                  {PRIVATE_CHAT_NEEDS_EMPTY}
                </p>
              )}
              {privateOn && (
                <p style={{ margin: '10px 0 0', paddingTop: 8, borderTop: `1px solid ${C.borderLight}`, fontSize: 11.5, color: C.textSecondary, lineHeight: 1.45 }}>
                  Пока чат приватный, его видите только вы. После возврата в общий сделать
                  приватным уже не получится — его могли видеть другие участники.
                </p>
              )}
            </div>
          </div>
        </Menu>
      )}
    </div>
  );
}

// Действия чата (QA-P1, бэк f777c8460; доработка QA-P1F по ревью 6e843c37): переименовать,
// архивировать и теги — правка содержимого чата: владелец и редактор (бэковый PUT оставляет
// редактору name/tags), читателю — ничего; удалить — решение Андрея 12.11 (W4-C1): общий чат
// удаляет только владелец проекта, свой приватный — его автор (зеркало SessionsController.Delete);
// срок жизни, мьют уведомлений и
// «не сохранять решения» — настройки владельца: закрыты и редактору, и читателю (зеркало
// бэкового OwnerSettingsGate, 403 owner_settings). Обычный проект (без myRole) и чат вне
// проекта — как раньше. Окружение node без jsdom: логика гейта — чистыми функциями,
// проводка в карточку, список и шапку — сверкой исходников (образец contentRights.test.ts).
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  CHAT_DELETE_DENIED, CHAT_WRITE_DENIED, OWNER_SETTINGS_DENIED,
  chatActionDeniedReason, chatDeleteDeniedReason, chatWriteDeniedReason, isNotChatAuthorError,
  ownerSettingsDeniedReason,
} from '../participantGate';

const ME = 'user-me';
const OTHER = 'user-other';

describe('chatWriteDeniedReason (переименовать/архивировать)', () => {
  it('читателю — отказ с причиной; редактору и владельцу — можно', () => {
    expect(chatWriteDeniedReason({ myRole: 'reader' })).toBe(CHAT_WRITE_DENIED);
    expect(chatWriteDeniedReason({ myRole: 'editor' })).toBeNull();
    expect(chatWriteDeniedReason({ myRole: 'owner' })).toBeNull();
  });
  it('обычный проект и чат вне проекта — можно, как раньше', () => {
    expect(chatWriteDeniedReason({})).toBeNull();
    expect(chatWriteDeniedReason(null)).toBeNull();
    expect(chatWriteDeniedReason(undefined)).toBeNull();
  });
});

describe('chatDeleteDeniedReason (удалить)', () => {
  // Решение Андрея 12.11 (W4-C1): редактору открыт только ЕГО приватный чат (оба поля —
  // privateToUserId и createdByUserId — мои); любой общий чат — даже созданный им самим —
  // только владельцу проекта
  it('редактору: свой приватный можно, свой общий и чужой — нет (и через диспетчер)', () => {
    expect(chatDeleteDeniedReason({ myRole: 'editor' }, { createdByUserId: ME, privateToUserId: ME }, ME)).toBeNull();
    // свой общий: автор я, но чат не приватен — отказ (перевёрнутый старый кейс «editor свой → null»)
    expect(chatDeleteDeniedReason({ myRole: 'editor' }, { createdByUserId: ME, privateToUserId: null }, ME)).toBe(CHAT_DELETE_DENIED);
    expect(chatActionDeniedReason('delete', { myRole: 'editor' }, { createdByUserId: ME, privateToUserId: null }, ME)).toBe(CHAT_DELETE_DENIED);
    // чужой чат — общий и приватный чужому — отказ; приватен мне, но автор другой — тоже не мой чат
    expect(chatDeleteDeniedReason({ myRole: 'editor' }, { createdByUserId: OTHER, privateToUserId: null }, ME)).toBe(CHAT_DELETE_DENIED);
    expect(chatDeleteDeniedReason({ myRole: 'editor' }, { createdByUserId: OTHER, privateToUserId: OTHER }, ME)).toBe(CHAT_DELETE_DENIED);
    expect(chatDeleteDeniedReason({ myRole: 'editor' }, { createdByUserId: OTHER, privateToUserId: ME }, ME)).toBe(CHAT_DELETE_DENIED);
    // чат без автора (старая запись) и мой id без пары — fail-closed, как на бэке (NoAuthorChatId)
    expect(chatDeleteDeniedReason({ myRole: 'editor' }, { createdByUserId: null, privateToUserId: null }, ME)).toBe(CHAT_DELETE_DENIED);
    expect(chatDeleteDeniedReason({ myRole: 'editor' }, null, ME)).toBe(CHAT_DELETE_DENIED);
    expect(chatDeleteDeniedReason({ myRole: 'editor' }, { createdByUserId: ME, privateToUserId: ME }, null)).toBe(CHAT_DELETE_DENIED);
  });
  it('читателю: любой чат, даже свой приватный (и через диспетчер)', () => {
    expect(chatDeleteDeniedReason({ myRole: 'reader' }, { createdByUserId: ME, privateToUserId: ME }, ME)).toBe(CHAT_DELETE_DENIED);
    expect(chatActionDeniedReason('delete', { myRole: 'reader' }, { createdByUserId: ME, privateToUserId: ME }, ME)).toBe(CHAT_DELETE_DENIED);
  });
  it('владельцу и обычному проекту: любой чат, как раньше', () => {
    expect(chatDeleteDeniedReason({ myRole: 'owner' }, { createdByUserId: OTHER }, ME)).toBeNull();
    expect(chatDeleteDeniedReason({}, { createdByUserId: OTHER }, ME)).toBeNull();
    expect(chatDeleteDeniedReason(null, { createdByUserId: OTHER }, null)).toBeNull();
  });
});

describe('ownerSettingsDeniedReason (настройки: срок/мьют/голос/досье)', () => {
  it('редактору и читателю — отказ с причиной (зеркало бэкового OwnerSettingsGate)', () => {
    expect(ownerSettingsDeniedReason({ myRole: 'editor' })).toBe(OWNER_SETTINGS_DENIED);
    expect(ownerSettingsDeniedReason({ myRole: 'reader' })).toBe(OWNER_SETTINGS_DENIED);
  });
  it('владельцу, обычному проекту и чату вне проекта — можно, как раньше', () => {
    expect(ownerSettingsDeniedReason({ myRole: 'owner' })).toBeNull();
    expect(ownerSettingsDeniedReason({})).toBeNull();
    expect(ownerSettingsDeniedReason(null)).toBeNull();
    expect(ownerSettingsDeniedReason(undefined)).toBeNull();
  });
});

describe('chatActionDeniedReason (диспетчер)', () => {
  it('rename/archive — гейт записи: редактору можно, читателю нет', () => {
    for (const key of ['rename', 'archive']) {
      expect(chatActionDeniedReason(key, { myRole: 'editor' }, { createdByUserId: OTHER }, ME)).toBeNull();
      expect(chatActionDeniedReason(key, { myRole: 'reader' }, { createdByUserId: ME }, ME)).toBe(CHAT_WRITE_DENIED);
    }
  });
  it('теги — тоже правка содержимого: редактору можно (в PUT ему открыты name/tags), читателю нет', () => {
    expect(chatActionDeniedReason('tags', { myRole: 'editor' }, { createdByUserId: ME }, ME)).toBeNull();
    expect(chatActionDeniedReason('tags', { myRole: 'reader' }, { createdByUserId: ME }, ME)).toBe(CHAT_WRITE_DENIED);
  });
  it('настройки владельца («Срок», «Мьют», «Досье») — закрыты и редактору, и читателю', () => {
    for (const key of ['expiry', 'notify', 'dossier', 'wiki']) {
      expect(chatActionDeniedReason(key, { myRole: 'editor' }, { createdByUserId: ME }, ME)).toBe(OWNER_SETTINGS_DENIED);
      expect(chatActionDeniedReason(key, { myRole: 'reader' }, { createdByUserId: ME }, ME)).toBe(OWNER_SETTINGS_DENIED);
    }
  });
  it('настройки открыты владельцу, обычному проекту и чату вне проекта — как раньше', () => {
    for (const key of ['tags', 'expiry', 'notify', 'dossier', 'wiki']) {
      expect(chatActionDeniedReason(key, { myRole: 'owner' }, { createdByUserId: ME }, ME)).toBeNull();
      expect(chatActionDeniedReason(key, {}, { createdByUserId: ME }, ME)).toBeNull();
      expect(chatActionDeniedReason(key, null, { createdByUserId: ME }, null)).toBeNull();
    }
  });
  it('«Закрепить» и «На стену» ничего не пишут в чате — гейт их не трогает', () => {
    for (const key of ['pin', 'wall']) {
      expect(chatActionDeniedReason(key, { myRole: 'reader' }, { createdByUserId: OTHER }, ME)).toBeNull();
    }
  });
});

describe('isNotChatAuthorError', () => {
  const err = (status: number, code: string) =>
    Object.assign(new Error('Только автор чата'), { status, body: { error: 'Только автор чата', code } });
  it('403 + not_chat_author', () => {
    expect(isNotChatAuthorError(err(403, 'not_chat_author'))).toBe(true);
  });
  it('другой код или статус — нет', () => {
    expect(isNotChatAuthorError(err(403, 'other'))).toBe(false);
    expect(isNotChatAuthorError(err(409, 'not_chat_author'))).toBe(false);
    expect(isNotChatAuthorError(new Error('x'))).toBe(false);
    expect(isNotChatAuthorError(null)).toBe(false);
  });
});

describe('проводка в карточку и список', () => {
  const card = readFileSync(new URL('../../../../components/ChatCard.tsx', import.meta.url), 'utf8');
  const list = readFileSync(new URL('../../../../components/SessionList.tsx', import.meta.url), 'utf8');
  // Мутация «гейт снят» (ключ выпал из таблицы сопоставления / disabled без причины) —
  // этот кейс красный: без причины человек кликал бы то, что бэк откажет
  it('карточка выключает каждое действие своей причиной (и настройки владельца — те же ключи)', () => {
    for (const key of ['rename', 'archive', 'delete', 'tags', 'notify', 'dossier', 'expiry']) {
      expect(card, `нет сопоставления ключа с причиной: ${key}`).toContain(`${key}: ${key}DeniedReason`);
      expect(card, `пункт меню не выключен причиной: ${key}`).toContain(`disabled={!!deniedReason('${key}')}`);
      expect(card, `пункт меню без подсказки причины: ${key}`).toContain(`hint={deniedReason('${key}') ?? undefined}`);
    }
  });
  it('список передаёт причины из единого гейта и ловит отказ', () => {
    for (const key of ['rename', 'archive', 'delete', 'tags', 'notify', 'dossier', 'expiry']) {
      expect(list, `список не передаёт причину: ${key}`).toContain(`${key}DeniedReason={chatActionDeniedReason('${key}', project, s, myUserId)}`);
    }
    expect(list).toContain('const myUserId = readStoredUserId();');
    expect(list).toContain('isNotChatAuthorError(e)');
  });
});

describe('проводка гейта «Голоса» (QA-P1F, остаток M1)', () => {
  const panel = readFileSync(new URL('../../../../components/ChatPanel.tsx', import.meta.url), 'utf8');
  const composer = readFileSync(new URL('../../../../components/Composer.tsx', import.meta.url), 'utf8');
  // Мутация «гейт снят» (guard убран из sync-эффекта / кнопка без disabled) — красный:
  // участник шлёт PUT, который бэк откажет (403 owner_settings), и кликает то, что не сработает
  it('sync-эффект стиля и переключение режима не шлют voiceStyle/voiceMode участнику', () => {
    expect(panel).toContain('const voiceDeniedReason = ownerSettingsDeniedReason(project);');
    // guard стоит ДО вызова updateChatFields в эффекте и в обработчике тумблера
    const effect = panel.slice(panel.indexOf('const voiceDeniedReason'), panel.indexOf('const handleToggleVoiceMode'));
    expect(effect).toContain('if (voiceDeniedReason) return;');
    expect(effect.indexOf('if (voiceDeniedReason) return;')).toBeLessThan(effect.indexOf('updateChatFields(session, { voiceStyle })'));
    const handler = panel.slice(panel.indexOf('const handleToggleVoiceMode'), panel.indexOf('const handleToggleVoiceMode') + 1500);
    expect(handler).toContain('if (voiceDeniedReason) throw new Error(voiceDeniedReason);');
    expect(handler.indexOf('if (voiceDeniedReason) throw')).toBeLessThan(handler.indexOf('updateChatFields(session, next ?'));
  });
  it('кнопка режима разговора в композере неактивна участнику с причиной', () => {
    expect(composer).toContain('const voiceDeniedReason = ownerSettingsDeniedReason(project);');
    const btn = composer.slice(composer.indexOf('const voiceButton = onToggleVoiceMode'), composer.indexOf('const talkStopButton'));
    expect(btn).toContain('disabled={!!voiceDeniedReason}');
    expect(btn).toContain('`Режим разговора: ${voiceDeniedReason}`');
  });
});

describe('проводка в шапку открытого чата', () => {
  const bar = readFileSync(new URL('../../../../components/chat/ChatHeaderBar.tsx', import.meta.url), 'utf8');
  // Блок JSX/объекта от якоря до ближайшего onClick — внутри обязаны быть и подсказка, и disabled
  const block = (anchor: string): string => {
    const start = bar.indexOf(anchor);
    expect(start, `якорь не найден: ${anchor}`).toBeGreaterThan(-1);
    return bar.slice(start, bar.indexOf('onClick', start));
  };
  it('ряд, «⋯» и runAction идут через единый гейт; отказ бэка ловится', () => {
    expect(bar).toContain('const actionDenied = (key: string): string | null => chatActionDeniedReason(key, project, session, myUserId);');
    expect(bar).toContain('if (actionDenied(key)) return;');
    expect(bar).toContain('const denied = actionDenied(key);');
    expect(bar).toContain('disabled={!!denied}');
    expect(bar).toContain('disabled: !!denied,');
    expect(bar).toContain('isNotChatAuthorError(e)');
  });
  it('AI-входы следуют гейту переименования: слушатель retitle, пункт и «AI-действия»', () => {
    expect(bar).toContain("locked={!!actionDenied('rename')}");
    const b1 = block('label="Переименовать по переписке"');
    expect(b1).toContain("hint={actionDenied('rename') ?? undefined}");
    expect(b1).toContain("disabled={!!actionDenied('rename')}");
    const b2 = block("label: 'AI-действия'");
    expect(b2).toContain("disabled: !!actionDenied('rename'),");
    expect(b2).toContain("sublabel: actionDenied('rename') ?? undefined,");
  });
});

describe('проводка AI-переименования в палитре (chat.retitle)', () => {
  const actions = readFileSync(new URL('../../../../lib/ai/actions.tsx', import.meta.url), 'utf8');
  it('when-гейт действия — тот же гейт записи', () => {
    expect(actions).toContain('!chatWriteDeniedReason(c.nav?.project)');
  });
});

describe('проводка owner-гейта в кнопки настроек шапки (QA-P1F)', () => {
  const expiry = readFileSync(new URL('../../../../components/chat/ExpiryButton.tsx', import.meta.url), 'utf8');
  const notify = readFileSync(new URL('../../../../components/chat/NotifyButton.tsx', import.meta.url), 'utf8');
  const dossier = readFileSync(new URL('../../../../components/chat/DossierOptOutButton.tsx', import.meta.url), 'utf8');
  const bar = readFileSync(new URL('../../../../components/chat/ChatHeaderBar.tsx', import.meta.url), 'utf8');
  // Мутация «настройки не гейтятся» (гейт убран из кнопки / disabled без denied) —
  // этот кейс красный: без причины человек кликал бы то, что бэк откажет
  it('срок, мьют и досье гасятся сами по роли — тот же гейт, что у диспетчера', () => {
    for (const src of [expiry, notify, dossier]) {
      expect(src).toContain('const denied = ownerSettingsDeniedReason(project);');
      expect(src).toContain('disabled={saving || !!denied}');
    }
  });
  it('шапка даёт этим кнопкам проект и держит пункты right-click меню тем же гейтом', () => {
    expect(bar).toContain('<ExpiryButton session={session} project={project}');
    expect(bar).toContain('<NotifyButton session={session} project={project}');
    expect(bar).toContain('<DossierOptOutButton session={session} project={project}');
    // «Вики» участнику скрыта целиком (а не серая): отказ идёт в доступность, не в disabled
    expect(bar).toContain("useWikiChatAvailable(project?.id, !!actionDenied('wiki'))");
    for (const key of ['tags', 'notify', 'dossier', 'expiry']) {
      expect(bar).toContain(`disabled={!!actionDenied('${key}')}`);
      expect(bar).toContain(`hint={actionDenied('${key}') ?? undefined}`);
    }
  });
});

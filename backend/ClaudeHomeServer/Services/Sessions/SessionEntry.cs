using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services.Sessions;

// Запись реестра сессий: Session (персистентная часть) и живые рантайм-поля хода. Вынесена из
// SessionManager целиком (S3-1): локи и поля НЕ дробим по ролям — на ней пять локов, и их
// порядок сейчас верен (docs/research/session-manager-split-2026-11.md, «Чего не делать» п. 3).
// Здесь же контракт содержимого очереди (QueuedMessage / PendingKind, конец файла): на него
// смотрят поле Pending и ExpiredOnRestore этой записи — тип сообщения держат рядом с его
// хранителем (row «SessionEntry.cs (типы)» плана §1). UserTurnSnapshot остаётся вложенным
// типом фасада: его шаг переноса ещё не пройден.
internal sealed class SessionEntry
{
    public required Session Info;
    // Очередь рассылки и наблюдателей сессии (создаётся лениво первым сообщением хода)
    public SessionMessagePipeline? Pipeline;
    public ILlmSessionAdapter? Process;
    public TurnAccumulator? Accumulator;
    // Кэш последних workflow_progress для replay при подключении нового клиента
    public Dictionary<string, WorkflowProgressMessage> WorkflowProgress = new();
    // Последний манифест recall (F3) — как и workflow_progress, транзитное WS-событие;
    // без кэша клиент, открывший чат уже после хода (или переподключившийся), никогда
    // не увидит «использовано сейчас», хотя ход реально опирался на память/команду.
    public RecallManifestMessage? LastRecallManifest;
    // Текст ответа текущего хода — для поиска маркера завершения цикла «до готово».
    // StringBuilder не потокобезопасен: Append идёт из read-loop адаптера, а ToString/Clear —
    // из ContinueWorkLoopAsync (Task.Run) и SetWorkLoopAsync. Все доступы под LoopTurnLock.
    public System.Text.StringBuilder LoopTurnText = new();
    public readonly object LoopTurnLock = new();
    // Текст ответа текущего хода — для маркера эскалации координатора (Э4) и для стрижки
    // маркеров протокола в живой ленте. Копится в ЛЮБОМ чате: маркер молчания
    // `<no-reply/>` живёт вне штаба, да и сохранённая история чистится безусловно.
    // Отдельный буфер от LoopTurnText: у режимов разные потребители, и очистка одного
    // не должна съедать маркер другого (оба режима могут быть включены разом).
    public System.Text.StringBuilder TeamTurnText = new();
    public readonly object TeamTurnLock = new();
    // Сколько символов «очищенного от маркеров» текста текущего хода уже ушло
    // в живую трансляцию (волна 6): считаем от StripTeamProtocolMarkers(TeamTurnText),
    // а не от длины самого TeamTurnText — иначе диффом в дельту попал бы вырезанный
    // маркер. Живёт рядом с TeamTurnText, сбрасывается вместе с ним под TeamTurnLock.
    public int TeamTurnShownLength;
    // В тексте текущего хода уже встречался '<'. Пока не встречался, маркеру взяться
    // неоткуда — дельта транслируется как есть, без склейки и разбора всего хода
    // (стрижка идёт во всех чатах, и платить за неё на каждой дельте обычной прозы
    // незачем). Живёт рядом с TeamTurnText и чистится вместе с ним (под TeamTurnLock).
    public bool TurnSawAngleBracket;
    // В текущем ходу штаба координатор задал вопрос ASK-карточкой (Э8). Гард молчаливого
    // тупика по концу хода смотрит сюда: ход, закончившийся вопросами человеку, — это
    // работающее интервью, а не тупик, и карточка «вопросов не будет» там была бы враньём.
    // Живёт рядом с TeamTurnText и чистится вместе с ним (под TeamTurnLock).
    public bool TeamTurnAsked;
    // Осушенный текст хода штаба с ключом по TurnSeq — хранилище, через которое подписчик
    // turn/completed получит текст, не переносимый событием. Заполняется OnMessageAsync под
    // TeamTurnLock (там же, где осушается TeamTurnText); читается подписчиком по ключу
    // события. Первая запись по ключу выигрывает: повторная не затирает непустую (двойной
    // терминал одного хода, защита от потери маркера эскалации — тот же класс дефекта, что
    // «волна-призрак», ради которого TeamTurnText чистят при прерывании). Потолок
    // MaxLastTurnTextEntries защищает от роста при шторме терминалов; при превышении
    // вытесняется самая старая запись (минимальный TurnSeq), не свежая — иначе бы запись
    // свежего хода вытеснила ещё не прочитанную запись предыдущего. См.
    // docs/research/session-core-split-2026-09.md, «План шага 1 / Слот».
    // LastTurnTexts снят с шагом 1в: текст хода едет в LastTeamTurnEnds вместе с failed/asked.
    // Номер текущего хода, чей текст ляжет в LastTurnTexts по приходу result/error.
    // Обновляется на SessionStartedMessage: к этому моменту ClaudeSession уже инкрементировал
    // SubmittedTurnSeq в SubmitTurn (ClaudeSession.cs:2351), а SessionStartedMessage шлётся
    // ПОСЛЕ (ClaudeSession.cs:4099) — то есть чтение SubmittedTurnSeq через entry.Process
    // в OnMessageAsync даёт уже актуальный номер. volatile: путь SessionStartedMessage
    // (_onMessage → OnMessageAsync) и путь turn/completed (finally FallbackLlmSessionAdapter
    // после сброса _turn, FallbackLlmSessionAdapter.cs:943, 984) идут разными нитями, и без
    // барьера возможна перестановка. 0 — ход синтетический (local voice), turn/completed
    // по нему не публикуется, слот не пополняется.
    public volatile int LastTurnSeq;
    // Планировщик прямо сейчас строит план по вводной (StartTeamWorkAsync обернул
    // CreateTeamPlanAsync). Гард молчаливого тупика по концу хода смотрит сюда: пока
    // планирование живо, «Planning && WaveNumber == 0» — это работающий планировщик,
    // а не тупик координатора, и карточка «Координатор не понял вводную» была бы ложной
    // тревогой (прод 2026-08-04: тревога поднялась на живом планировании, а через 28 с
    // пришёл готовый план). Память, а не стор: рестарт сервера убивает сам планировщик,
    // и «планирование живо» после него неправда по определению.
    public volatile bool TeamPlanningInFlight;
    // Момент, когда гард молчаливого тупика (TeamTurnCompletionService) ВПЕРВЫЕ увидел
    // подавление из-за живого async-субагента для текущего захода в stalledStage
    // (Interview или Planning && WaveNumber == 0). Задача b63fd8ea: без потолка длительности
    // подавление висело бессрочно — фоновый агент с heartbeat'ами мог не доводить
    // координатора до маркера часами. Метка сбрасывается, как только async-агент уходит
    // (HasPendingBg == false): следующий всплеск фоновой активности считается с нуля,
    // а не копит время от НЕсвязанного прошлого агента. Под TeamTurnLock (та же дисциплина,
    // что у TeamTurnText/TeamTurnShownLength/TurnSawAngleBracket/TeamTurnAsked рядом).
    public DateTime? AsyncAgentStallSince;
    // Текущий ход штаба поднят сообщением ЧЕЛОВЕКА (M7): авто-подтверждение добавочного
    // плана опирается на «вводная человека и есть точка контроля», поэтому инициатора
    // хода помечаем при запуске (SendDirectAsync / SendMessageAndWaitAsync) — классификация
    // агентской вводной как работы публикует план неподтверждённым и ждёт человека.
    public bool TeamTurnFromHuman;
    // Сабагент этого хода оборвался на середине (паспорт прогона с Truncated) — по концу
    // хода уходит добивание. Пишет приёмник паспортов (поток ватчера сабагентов), читает
    // обработчик result — отсюда volatile. null — обрывов не было либо уже добили.
    public volatile Llm.SubagentRunPassport? TruncatedSubagent;
    // Пометка координатору: ФОНОВЫЙ агент оборвался на tool_use, а CLI выдал координатору
    // его последнюю реплику за готовый результат. Своего хода на пометку не тратим (второй
    // systemDirective в идущий процесс слать нельзя) — она уезжает префиксом ближайшего
    // хода, см. BuildCliTurnText. null — пометки нет либо она уже уехала.
    public volatile Llm.SubagentRunPassport? TruncatedBgNote;
    // Прошлый ход остановил человек («Стоп» в чате или в трее рук): пометка едет префиксом
    // ближайшего хода (BuildCliTurnText). Транскрипт CLI убитого хода обрывается молча, и
    // без неё модель принимает обрыв за сбой и доделывает прерванное. Одноразовая.
    public volatile bool UserStopNote;
    // Сколько добиваний подряд отправлено ЗА ОДНОГО агента (потолок — MaxSubagentNudges).
    // Обнуляется штатным отчётом ТОГО ЖЕ агента и любым ходом человека: две попытки — на серию.
    public int SubagentNudges;
    // Агент, чью серию добиваний считает SubagentNudges. Без привязки к agentId потолок
    // не достигался вовсе: в ходе с двумя агентами штатный отчёт агента B обнулял счётчик
    // оборвавшегося агента A, и добивание уходило с attempt=1 по кругу.
    public volatile string? NudgeAgentId;
    // Ход завершился ошибкой (result error / error) — цикл не продолжаем
    public bool LoopTurnFailed;
    // Текущий ход нёс протокол цикла «до готово» (выставляет BuildCliTurnText):
    // продолжение цикла решается по result ИМЕННО этого хода, а не по exited —
    // после механики доживания exited приходит лишь со смертью прогона (до 30 мин
    // при живых фоновых агентах). Чужие ходы (REST-канал агентов, /compact,
    // ходы-продолжения CLI) протокола не несут и цикл не двигают.
    // (S3-7d) Все записи маркера — под entry.PendingLock, тем же замком, под которым его
    // читают в решающих гейтах (разбор очереди, начало ContinueWorkLoopAsync).
    public volatile bool LoopTurnInFlight;
    // Момент, когда result/error хода перевёл статус в Active (ход завершён, ждём exited для
    // финального Finished). Источник «зависшей» Active: exited приходит лишь со смертью
    // прогона, а прогон доживает фоновых агентов до BgLingerTimeout (30 мин) или вовсе не
    // выходит — статуса Finished нет, чат висит active. Sweep-terminus в SaveSessions по этой
    // метке отличает «зависший Active» (нужно довести до Finished) от «идущий ход» (Working/
    // Waiting метки не имеют). DateTime? — struct, доступ под PendingLock. null — хода нет.
    public DateTimeOffset? LastTurnEndedAt;
    // Разбор очереди (DrainNextPendingAsync) вне цикла идёт по одному сообщению за раз —
    // следующее уйдёт по result уже этого хода. Параллельные drain (выключение цикла из
    // SetWorkLoopAsync + штатный drain из OnMessageAsync по result) без этого флага
    // вытащили бы ДВА сообщения и послали два хода. Взводится атомарно с извлечением,
    // гасится по концу доставки — второй drain в окне видит его и уступает (сообщение
    // не теряется: по result хода отработает следующий drain).
    public volatile bool DrainInFlight;
    // Ожидающая карточка взаимодействия (разрешение/вопрос/план) — replay при
    // JoinSession: без него клиент после F5 видел бы «Claude печатает…» без
    // возможности ответить, а CLI ждал бы до часового таймаута
    public ServerMessage? PendingInteraction;
    // Карточка пришла ВНЕ пользовательского хода: её поднял фоновый агент или ход-продолжение
    // CLI после result (чат был свободен). Ответ на неё не должен ставить Working — result
    // продолжения ClaudeSession намеренно отбрасывает («между ходами», чтобы не закрыть им
    // будущий ход), и снять Working было бы некому: чат висел занятым до «Стопа».
    // Пишется при каждой карточке, пришедшей не в Waiting, — протухнуть не успевает.
    public volatile bool InteractionOutsideTurn;
    // Контекст адаптера устарел (смена собеседника / правка персоны) — убирается
    // ЛЕНИВО перед следующим ходом, чтобы не рвать активный ход и доживающих агентов
    public volatile bool AdapterStale;
    // Текущий ход идёт в чужом git worktree (session_started с TurnWorktree): правки
    // такого хода живут в другом дереве и пометку «зафиксировано в git»
    // (CommittedFilePaths) с путей КОРНЯ не снимают — зеркало гейта
    // SessionChangedPaths.Extract. Взводится/сбрасывается session_started,
    // сбрасывается сообщением пользователя (начало нового хода в основном дереве).
    public volatile bool TurnInWorktree;
    // Одиночный per-turn ожидатель хода (SendMessageAndWaitAsync): резолвится в
    // OnMessageAsync на result/error/exited и безусловно обнуляется (Interlocked)
    public TaskCompletionSource<TurnResult>? TurnWaiter;
    // Число сообщений истории до хода — чтобы взять реплику ответа именно этого хода
    public int TurnWaiterBaseline;
    // Сериализует ensure/dispose адаптера. Без него два конкурентных входа
    // (хаб + REST-агент/work-loop/compact) проходили бы check-then-act на Process/
    // AdapterStale и создавали два адаптера → два claude --resume на один транскрипт.
    public readonly SemaphoreSlim EnsureLock = new(1, 1);
    // Сообщения (агентов — chats_send, пользователя — «честная очередь»), пришедшие в
    // занятую сессию, и доставляемые по концу текущего хода. Пользовательские ход НЕ
    // прерывают (отправка ≠ остановка, как в claude CLI) — кроме случаев, когда ждать
    // нечего: ход стоит на вопросе человеку (Waiting) или идёт цикл «до готово».
    // Агентские (chats_send) прерывают обычный ход — ждущий их агент упрётся в таймаут, —
    // но ход в цикле «до готово» и в штабе не рушат: там они ждут конца цикла и штатного
    // конца хода соответственно. Явный перебой по кнопке — PreemptForPending.
    // Переживает рестарт: каждая мутация пишется в data/sessions/{ключ}/pending.json
    // (PendingQueue.Changed → PersistPending; смена QueueFrozen — явным PersistPending),
    // на старте очередь поднимается с диска, а чаты
    // без живого хода разбираются штатным DrainNextPendingAsync (DrainRestoredPendingAsync).
    // QueueFrozen — «Стоп» заморозил разбор: автодоставки нет до возобновления пользователем
    // (новое сообщение); заморозка тоже переживает рестарт.
    public readonly PendingQueue Pending = new();
    public readonly Lock PendingLock = new();
    // Замок видимости чата (S2d-7a): под ним И первое сообщение чата «занимает» чат
    // (ClaimFirstMessage), И тумблер видимости (SetChatVisibilityAsync) проверяет «сообщений
    // нет» и ставит PrivateToUserId. Без общего замка отправка и тумблер из соседних вкладок
    // дали бы приватный чат с сообщением, уже разосланным в группу проекта.
    public readonly Lock VisibilityLock = new();
    // Отметка «чат занят» живёт в Session.MessageClaimed (переживает рестарт); читается и пишется
    // ТОЛЬКО под VisibilityLock.
    // Меняется только через PersistPending-места (FreezePending, возобновление человеком)
    public volatile bool QueueFrozen;
    // Сообщения старше срока годности, не оживлённые при загрузке: карточку «не доставлено»
    // в историю пишет DrainRestoredPendingAsync (на загрузке брокера и истории ещё нет).
    public List<QueuedMessage>? ExpiredOnRestore;
    // Очередь поднята с диска — чат кандидат на автодренаж после старта.
    public bool PendingRestored;
    // Делегированность ПРОГОНА: глубина и «ход доклада» подач, чьи фоновые агенты ещё
    // живы. Признак хода у адаптера гаснет вместе с _turnLock, а фоновые агенты
    // делегированного хода (Agent run_in_background, Workflow) живут после result и ходят
    // в MCP с заголовком этого чата — по признаку хода они выглядели бы человеком. Поэтому
    // гейт берёт максимум из хода и прогона. Снимается, что раньше: подачей человека или
    // когда у прогона не осталось живых фоновых агентов (ILlmSessionAdapter.HasLiveBgAgents);
    // без фоновых действует ровно ход. Адаптера нет — живость не узнать, признак держится
    // (fail-closed). SubmitDelegation — собственный признак последней подачи: пока её ход
    // в полёте, он делегирован и тогда, когда адаптер ещё не взвёл свой признак.
    // Под PendingLock: читают его потоки MCP-вызовов.
    public TurnDelegationState RunDelegation;
    public TurnDelegationState SubmitDelegation;
    // Актор хода (S2a-1): 1:1 с делегированием, те же замок и правила жизни. SubmitActor — актор
    // идущей подачи, RunActor — «липкий» актор прогона, пока живы его фоновые агенты. null —
    // не записан; UnknownActor — подача без инициатора (fail-closed). Под PendingLock.
    public string? SubmitActor;
    public string? RunActor;
    // Прогон, в котором идёт сворачивание контекста (/compact). Обычный ход, но обёртка
    // фолбэка его не оркеструет, а exited убитой компакции глотает — поэтому прерывать
    // компакцию нельзя (чат залип бы в Working навсегда). Хранится идентификатор прогона,
    // а не флаг (та же причина, что у DrainOnExitedRun): поздний терминал ЧУЖОГО
    // доживающего прогона иначе снял бы защиту с идущей компакции. 0 — компакции нет.
    public long CompactRun;
    // Идентификатор текущего прогона адаптера: колбэк КАЖДОГО прогона несёт свой,
    // поэтому exited доживающего процесса отличим от exited текущего (см. LoopTurnInFlight —
    // exited опаздывает до ~30 мин). Присваивается вместе с Process.
    public long RunId;
    // Прогон, прерванный ради доставки вставшего в очередь сообщения (enqueue + interrupt):
    // убитый процесс не шлёт result — только exited, поэтому разбор очереди по exited
    // включается этим полем (штатный триггер разбора висит на result/error). Хранится
    // именно идентификатор прогона, а не флаг: поздний exited ЧУЖОГО (доживающего)
    // прогона иначе увёл бы сообщение в SendDirectAsync на умирающий от interrupt
    // адаптер — из видимой очереди изъято, в семафоре адаптера потеряно. 0 — прерывания нет.
    public long DrainOnExitedRun;
    // Снимок последнего ПОЛЬЗОВАТЕЛЬСКОГО сообщения, ушедшего в работу: по «Стоп» его
    // копия возвращается в композер (как в десктопном Claude). null — ход авто/агентский.
    public volatile SessionManager.UserTurnSnapshot? CurrentTurnSnapshot;
    // Длина цепочки автоматических отчётов, приведшей в этот чат: доклад исполнителя
    // ставит сюда «своя глубина + 1». Гасит лавину «отчёт → реакция → отчёт выше»:
    // человек в переписке её не создаёт, поэтому его ход счётчик обнуляет.
    public volatile int ReportChainDepth;
    // Живой локальный голосовой ход (место chat-voice на «Локальная»): отмена для
    // «Стоп». Ставится RunLocalVoiceTurnAsync, гасится в её finally. volatile:
    // Interrupt читает из другого потока. null — локального хода нет.
    public volatile CancellationTokenSource? LocalVoiceCts;
    // Сколько локальных голосовых ходов прошло с последнего CLI-хода: при возврате
    // на CLI BuildCliTurnText допишет сводку разговора (транскрипт CLI этих реплик
    // не знает). Не персистится — рестарт сервера сводку теряет (v1, редкий кейс).
    public int LocalTurnsSinceCli;

    // Этап 4 / шаг 1в: план вызова HandleTeamTurnEndAsync на сессии с ключом по TurnSeq.
// OnMessageAsync кладёт план при терминале (ResultMessage/ErrorMessage) хода штаба,
// подписчик turn/completed забирает план и зовёт HandleTeamTurnEndAsync асинхронно.
// Текст+asked едут в записи, потому что шина их не несёт (публикатор — адаптер, текст
// ему недоступен); failed пишется сюда, чтобы двойной терминал одного хода (ErrorMessage
// + ResultMessage) давал один план по первой записи и дедуп SkipNextTeamTurnEnd в
// OnMessageAsync стал не нужен. Правила симметричны прежнему LastTurnTexts (1а):
// первая запись выигрывает, повторная не затирает непустую, потолок с вытеснением самой
// старой записи. turnSeq <= 0 — невалидный ключ (синтетические ходы local voice),
// turn/completed по ним не публикуется.

    // Этап 4 / шаг 1б: факт вызова старого пути HandleTeamTurnEndAsync — нужен теневому
    // подписчику turn/completed, чтобы свериться, что его симуляция совпадает с реально
    // отработавшим вызовом (тот же TurnSeq, тот же failed, тот же asked). Первая запись
    // выигрывает — повторная не затирает непустую (двойной терминал одного хода), и
    // правило симметрично LastTurnTexts. Потолок — больше, чем у LastTurnTexts: запись
    // живёт до прихода turn/completed, а подписчик может задержаться (fire-and-forget
    // PublishAsync в finally FallbackLlmSessionAdapter). 8 — запас на случай шторма
    // терминалов, после которого шина подтянется не сразу.
    public const int MaxLastTeamTurnEndEntries = 8;

    // Этап 4 / шаг 1в: план вызова HandleTeamTurnEndAsync. OnMessageAsync кладёт план при
    // терминале хода штаба, подписчик turn/completed изымает и асинхронно зовёт
    // HandleTeamTurnEndAsync. Текст+asked едут в записи, потому что шина их не несёт
    // (публикатор — адаптер, текст ему недоступен); failed пишется сюда, чтобы двойной
    // терминал одного хода давал один план по первой записи и дедуп SkipNextTeamTurnEnd
    // в OnMessageAsync стал не нужен. Правила симметричны прежнему LastTurnTexts (1а).
    public readonly struct TeamTurnEndCall
    {
        public string? Text { get; init; }
        public bool Failed { get; init; }
        public bool Asked { get; init; }
    }

    public readonly Dictionary<int, TeamTurnEndCall> LastTeamTurnEnds = new();

    // Положить план вызова HandleTeamTurnEndAsync по ключу turnSeq (text/failed/asked — все три
    // аргумента HandleTeamTurnEndAsync). Первая запись выигрывает, повторная не затирается
    // (двойной терминал одного хода). turnSeq <= 0 — невалидный ключ.
    public void RecordTeamTurnEnd(int turnSeq, string? text, bool failed, bool asked)
    {
        if (turnSeq <= 0) return;
        lock (TeamTurnLock)
        {
            if (LastTeamTurnEnds.ContainsKey(turnSeq)) return;
            while (LastTeamTurnEnds.Count >= MaxLastTeamTurnEndEntries)
            {
                int oldest = int.MaxValue;
                foreach (var k in LastTeamTurnEnds.Keys)
                    if (k < oldest) oldest = k;
                if (oldest == int.MaxValue) break;
                LastTeamTurnEnds.Remove(oldest);
            }
            LastTeamTurnEnds[turnSeq] = new TeamTurnEndCall { Text = text, Failed = failed, Asked = asked };
        }
    }

    // Изъять план по ключу turnSeq. Возвращает true и заполняет text + call (Failed, Asked),
    // если ключ найден (запись при этом удаляется). Чтение и удаление атомарны.
    public bool TryTakeTeamTurnEnd(int turnSeq, out string? text, out TeamTurnEndCall? call)
    {
        lock (TeamTurnLock)
        {
            if (LastTeamTurnEnds.TryGetValue(turnSeq, out var existing))
            {
                LastTeamTurnEnds.Remove(turnSeq);
                text = existing.Text;
                call = existing;
                return true;
            }
        }
        text = null;
        call = null;
        return false;
    }
}

// Контракт содержимого очереди занятого чата. Перенесён из вложенных типов фасада дословно,
// вместе с комментариями (S3-1, план §1, row «SessionEntry.cs (типы)»): публичные сигнатуры
// фасада (SendOrEnqueueAsync, SessionManager.cs:1314; GetPending, SessionManager.cs:1381)
// называют этот тип, поэтому он public — как был. Ссылки ядра
// (PendingQueueService, TurnIntake, TurnProcessHost, TurnRecovery) идут к нему напрямую, без
// alias через фасад. Хранители данных очереди (коллекция PendingQueue и формат pending.json
// PendingQueueFile) — тоже в ядре, Services/Sessions/PendingQueue.cs (хвост S3): пока они
// стояли в корне, на них смотрели allow-list сторожа формы ядра и допуск root-сторожа.
// Снаружи остались только вызывающие контракта: ChatHistoryService (читает и пишет
// pending.json), TaskExecutionService (передаёт фасаду константу PendingKind.Report),
// SessionMessagesController (читает Kind для ответа /pending) — они в allow-list правила 4
// сторожа формы ядра, TaskExecutionService к тому же в точечном допуске RootSubsystemBoundaryTests.

// Ожидающее доставки сообщение. Kind: User — сообщение человека из «честной очереди»
// (доставляется со своими вложениями и режимом, как при обычной отправке); Agent —
// chats_send/серверные отправки; Report — доклад о завершении делегированной задачи
// (TaskExecutionService.ReportToDelegatorAsync): ход-реакция постановщика. Report ждёт
// конца хода, как Agent, НО при активном цикле «до готово» гейт разбора очереди пропускает
// его СРАЗУ (наравне с User) — иначе цикл жжёт итерации, не видя доклада. SenderOrigin
// заполняется, только если отправитель из ДРУГОГО места (иной проект / вне проектов) —
// получателю показываем чип-источник, чтобы было видно, откуда прилетело.
//
// Silent — ход-реакция, чей текст уже виден в ленте отдельной репликой (доклад
// исполнителя): призрак дублировал бы её служебным промптом.
// SuppressTasksExecute — обязателен для доклада: без него постановщик мог бы
// самозапустить новую задачу и закольцевать A↔B.
// SenderChatName — имя чата-отправителя: подпись карточки, когда персоны у него нет
// («Входящее сообщение» ни о чём не говорит, а имя чата отвечает на вопрос «кто пишет»).
// StaffNote — служебный ход механики штаба: в ленте рисуется плашкой-разделителем
// с этой подписью, а не пузырём «Автоматически» (см. StoredUserMessage.StaffNote).
public record QueuedMessage(
    string Id, string Text, string? SenderPersonaId, string? SenderOrigin,
    int AgentDepth, DateTime EnqueuedAt, bool Silent = false, bool SuppressTasksExecute = false,
    string? SenderChatName = null, PendingKind Kind = PendingKind.Agent,
    IReadOnlyList<string>? AttachedPaths = null, string? Mode = null, string? StaffNote = null,
    string? SenderUserId = null);

// Вид ожидающего сообщения. Report отделён от Agent: при активном цикле «до готово» Report
// будит ждущий цикл (как User), а обычные Agent-сообщения посторонних агентов продолжают
// ждать конца ВСЕГО цикла — иначе координатор сбивался бы посреди итерации. Решение
// владельца 2026-09-01: посторонний агент не должен сбивать координатора.
public enum PendingKind { Agent, User, Report }

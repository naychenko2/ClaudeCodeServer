using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Prompts;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Team;
using ClaudeHomeServer.Services.Turn;

namespace ClaudeHomeServer.Services;

public class SessionManager : IDisposable, ITeamNotifier, ISessionDirectory,
    ITeamSessionDirectory, ITeamHistoryStore, ITeamRunState, ITeamTurnIntake
{
    private class SessionEntry
    {
        public required Session Info;
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
        // Только в памяти — при рестарте сессия и так становится Orphaned, доставлять
        // накопленное в умерший контекст незачем. QueueFrozen — «Стоп» заморозил разбор:
        // автодоставки нет до возобновления пользователем (новое сообщение).
        public readonly List<QueuedMessage> Pending = [];
        public readonly Lock PendingLock = new();
        public volatile bool QueueFrozen;
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
        public volatile UserTurnSnapshot? CurrentTurnSnapshot;
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

    // Дальше какой глубины цепочка автоотчётов не идёт. 3 — как у делегирования задач:
    // исполнитель → постановщик → его постановщик, дальше эскалация теряет смысл.
    private const int MaxReportChainDepth = 3;

    // Grace-окно для sweep-terminus Active→Finished (P12/P15): если после result хода exited
    // прогона не пришёл (доживающий прогон при живых фоновых агентах, подавленный SuppressExited,
    // висящий без работы процесс) — по истечении окна sweep доводит статус до Finished сам.
    // Размер — ровно на перечитывание клиентом истории хода по active-статусу (useSession.ts:
    // по active клиент перечитывает, по finished — нет; мгновенный переход закрыл бы окно доставки).
    // Секунды, не минуты: долгий grace лишь растягивает окно ложного active. По истечении решает
    // признак живости (HasLiveTurn/HasPendingBg), а не время — sweep не лезет в прогон с живой
    // фоновой работой (панель экспертов идёт > 10 мин). Калибруется через Session:StuckActiveGraceSeconds.
    private const int DefaultStuckActiveGraceSeconds = 15;
    private readonly int _stuckActiveGraceSeconds;

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
        IReadOnlyList<string>? AttachedPaths = null, string? Mode = null, string? StaffNote = null);

    // Вид ожидающего сообщения. Report отделён от Agent: при активном цикле «до готово» Report
    // будит ждущий цикл (как User), а обычные Agent-сообщения посторонних агентов продолжают
    // ждать конца ВСЕГО цикла — иначе координатор сбивался бы посреди итерации. Решение
    // владельца 2026-09-01: посторонний агент не должен сбивать координатора.
    public enum PendingKind { Agent, User, Report }

    // Атрибуция доставленного хода для лога «Доставка хода» (инцидент 2026-08-10 П3): кто
    // инициировал авто-доставку, когда src=auto/origin пустой. Различает точки, прежде бывшие
    // неразличимыми (drain очереди / work-loop / доклад исполнителя / обход байпаса), и pinpoint'ит
    // источник остаточных повторных доставок после закрытия байпаса (П3 п.3).
    public enum DeliveryCause
    {
        User,           // hub — человек через SignalR
        QueueUser,      // fromQueue — пользовательское сообщение из Pending
        QueueAgent,     // fromQueue — агентское сообщение из Pending (в т.ч. обход байпаса)
        Direct,         // auto — прямая отправка (SendOrEnqueueAsync при свободном чате: доклад исполнителя и пр.)
        WorkLoop,       // auto — цикл «до готово» (верификация/продолжение)
        SubagentNudge,  // auto — добивание сабагента, оборвавшегося на середине
        Unknown,        // auto — точка не помечена (диагностика: проставить cause)
    }

    // Снимок пользовательского сообщения, ушедшего в работу, — для возврата в композер
    // по «Стоп», если пользовательских в очереди не оказалось
    public record UserTurnSnapshot(string Text, IReadOnlyList<string> AttachedPaths, string? Mode);

    // Исход постановки пользовательского сообщения (Hub SendMessage возвращает клиенту):
    // Started — ход запущен сразу; Queued — чат занят/очередь непуста, сообщение встало в
    // серверную очередь и уйдёт по FIFO (оптимистичный баллон рисовать не надо — придёт
    // снимок pending_messages, а доставленное вернётся событием user_message);
    // QueuedPreempted — то же, но идущий ход при этом пришлось прервать (ждал человека либо
    // шёл цикл «до готово»). Клиенту это нужно знать: убитый ход пришлёт голый exited, и без
    // отметки о прерывании лента нарисует ложную аварию «AI завершился неожиданно».
    public enum SendUserOutcome { Started, Queued, QueuedPreempted }

    // Потолок очереди на сессию: агент может ретраить, а занятый чат — стоять долго.
    // Переполнение — честный отказ вызывающему, а не молчаливая потеря.
    private const int MaxPendingPerSession = 10;

    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    // Сквозной счётчик прогонов адаптера (SessionEntry.RunId): один на процесс сервера —
    // сравниваются только прогоны одной сессии, глобальная уникальность лишь упрощает отладку
    private static long _runSeq;
    private readonly ProjectManager _projects;
    // Готовность устройства локального проекта (ADR-016, вариант А плана §5): фоновые и
    // отложенные доставки в чат офлайн-устройства паркуются в Session.DeviceWaitQueue вместо
    // хода, обречённого на отказ. null — проверки нет (тесты без неё): ход идёт как раньше.
    private readonly Execution.IProjectDeviceGate? _deviceGate;
    // Шов Ф4 (Этап 5): заменяет _hub.Clients.Group(...).SendAsync — префиксы
    // собираются внутри SessionHubBroadcaster, а не в вызывающем коде.
    private readonly Composition.ISessionBroadcaster _broadcaster;
    private readonly Llm.ICheapTextRunner? _cheap;
    // Маршруты мест каталога (локаль/слот/модель) и параметры профилей — для ветки
    // локального голосового хода (chat-voice). null — в тестах без локали.
    private readonly Llm.LocalActionRouter? _router;
    // Прямой HTTP-клиент локальной LLM (Ollama или llama-server) для локальных голосовых
    // ходов; null — в тестах.
    private readonly Llm.ILocalLlmClient? _ollama;
    // Планировщик режима «Командная реализация» (Э2); null — режим без планирования
    private readonly TeamPlanningService? _teamPlanning;
    // Координатор режима «Командная реализация» (этап 4, шаг 2г-3а): owning — создаётся
    // в конструкторе SessionManager, DI-регистрация придёт в шаге 2г-4. Owning разрывает
    // цикл «ядро ↔ вертикаль штаба» без Lazy<T> и без нового Func-канала.
    private readonly TeamCoordinator _teamCoordinator;
    // Хранитель состояния режима (этап 4, шаг 2г-3в, волна А): owning по тому же шаблону,
    // что _teamCoordinator. Шесть блоков тела штаба (WithTeamState,
    // BroadcastTeamImplementAsync, NewTeamImplementBudget, TeamImplementSetupError,
    // ResolveTeamPlanRoot, GetTeamPlanAsync) переехали сюда; SessionManager держит тонкие
    // обёртки-делегаты, чтобы не переписывать тесты. Седьмой блок,
    // SaveTeamImplementStateAsync, снят в шаге 2г-4 волны 3 — его тело живёт
    // в ITeamSessionDirectory.PersistAndBroadcastAsync.
    private readonly TeamStateService _teamState;
    // Планирование штаба (этап 4, шаг 2г-3д, волна В): owning по тому же шаблону, что
    // _teamCoordinator/_teamState. Пять блоков тела штаба (RunTeamPlanningAsync,
    // CreateTeamPlanAsync, PublishTeamPlanAsync, SupersedeCurrentPlanCardAsync,
    // ResolveStalePlanCardAsync) переехали сюда; SessionManager держит тонкие
    // обёртки-делегаты, чтобы не переписывать тесты (18 мест вызывают _sut.CreateTeamPlanAsync).
    private readonly TeamPlanService _teamPlan;
    // Платформа внешних модулей: реестр манифестов + выпуск модульных токенов (R7)
    private readonly Modules.ModuleRegistry? _modules;
    private readonly Modules.ModuleTokenService? _moduleTokens;
    private readonly ChatHistoryService _history;
    // Снимки промпта ходов (кнопка «какой промпт ушёл»); null — в тестах
    private readonly PromptSnapshotStore? _promptSnapshots;
    // Паспорта прогонов сабагентов (диагностика обрывов + сигнал для автодобивания); null — в тестах
    private readonly Llm.Claude.SubagentRunLog? _subagentRuns;
    // Паспорта ходов (исход/попытки/подмены), единственный источник записи — шина
    // turn/completed (CLAUDE.md, раздел LLM-провайдеры). null — в тестах.
    private readonly Llm.TurnRunLog? _turnRuns;
    // Шина событий хода (ADR-013): уезжает в LlmSessionContext.Events каждой сессии.
    // null — в тестах без DI: лениво создаётся в TurnEvents, чтобы подписчики могли
    // звать шину и без явной передачи (тесты SessionManagerTests опираются на это).
    private Turn.ITurnEventBus? _turnEvents;
    private Turn.ITurnEventBus TurnEvents => _turnEvents ??= new Turn.TurnEventBus();
    private readonly string _sessionsFilePath;
    private readonly Lock _saveLock = new();
    // Автосохранение сессий каждые 30с
    /// <summary>
    /// Период автосохранения (оно же — единственный фоновый триггер sweep-terminus, см. SaveSessions).
    /// Настраивается ключом <c>Session:AutoSaveSeconds</c>; 0 и меньше — таймер не заводится вовсе.
    ///
    /// Ноль нужен ТЕСТАМ, и не ради скорости: sweep живёт внутри SaveSessions, поэтому фоновый
    /// таймер выполняет его в произвольный момент — в том числе между двумя ассертами теста.
    /// Когда sweep ещё читал статический Session.TaskSourceSessionResolver (конструктор каждого
    /// нового TaskManager перезаписывал его под параллельными классами), это давало плавающее
    /// падение Sweep_ЖивойПотомокВГлубину: иерархия делегирования на миг переставала резолвиться,
    /// и sweep закрывал сессию, которую тест только что проверил живой. Поодиночке ни один из двух
    /// факторов не воспроизводился — падало только на полном прогоне и не каждый раз.
    /// </summary>
    private static readonly TimeSpan DefaultAutoSaveInterval = TimeSpan.FromSeconds(30);
    private Timer? _autoSaveTimer;
    // Тик ожидания по маркеру `<waiting>` (фаза waiting И WaitingReason != null). Тикает
    // ТОЛЬКО ожидание модели — ожидание по живой делегированной задаче НЕ трогаем
    // (доклад придёт сам, смерть исполнителя ловит алерт молчания). Отдельный таймер
    // вместо встраивания в _autoSaveTimer — у них разные требования к гейтам и к
    // зависимости от живого хода: автосейв просто сбрасывает стор, тик шлёт директивы.
    private Timer? _waitingTickTimer;
    // Интервал тика ожидания по маркеру. Дефолт 5 минут (Loop:WaitingTickSeconds). Тестам
    // и особым окружениям позволяют уменьшить (для скорости прогонов). <=0 — таймер не
    // заводится, как у автосейва.
    private readonly TimeSpan _waitingTickInterval;
    // Потолок тиков ожидания по маркеру. По достижении цикл встаёт с reason="waiting_timeout"
    // и причиной из маркера. Дефолт 20 (Loop:MaxWaitingTicks). При 5-минутном интервале это
    // ~1.5 часа чистого ожидания — дольше редко нужно; раньше цикл честно признаёт, что
    // событие не пришло.
    private readonly int _maxWaitingTicks;
    // Сериализует внеходовые операции над историей сессии (lazy-init аккумулятора,
    // публикация fal/glif, правка карточек, эскалации штаба). Раньше семь мест брали
    // этот лок напрямую через Wait/Release — консолидировано через WithFalPersistLockAsync.
    private readonly SemaphoreSlim _falPersistLock = new(1, 1);

    // ЕДИНСТВЕННАЯ точка входа к _falPersistLock: WaitAsync + try/finally Release() под
    // одну обёртку. Все семь операций, которые раньше брали лок напрямую, идут через
    // неё — это держит инвариант «общий лок для внеходовых записей истории» по
    // построению (новая операция не сможет взять лок в обход, не дописав вызов).
    private async Task<T> WithFalPersistLockAsync<T>(Func<Task<T>> work)
    {
        await _falPersistLock.WaitAsync();
        try { return await work(); }
        finally { _falPersistLock.Release(); }
    }

    private async Task WithFalPersistLockAsync(Func<Task> work)
    {
        await _falPersistLock.WaitAsync();
        try { await work(); }
        finally { _falPersistLock.Release(); }
    }

    // Результат AppendIfNotDuplicateStoredNoLockAsync для дисковой ветки публикаций:
    // Added — запись добавлена; Duplicate — предикат уже видел такую запись; NoKey —
    // у чата ещё нет ClaudeSessionId (история не заведена). NoKey отделён от Duplicate
    // специально: при нём дисковой записи нет, но учёт и broadcast должны пройти
    // (раньше оба случая мапились в duplicate=true и аналитика терялась).
    private enum AppendResult { Added, Duplicate, NoKey }

    // Дедуп-then-append: общий шов публикаций fal/glif и AppendStoredAsync на
    // дисковой ветке. КОНТРАКТ: вызывающий ОБЯЗАН держать _falPersistLock — иначе
    // Load+SaveAsync терял бы параллельные записи соседа, а главное — публикация
    // могла бы пройти в обход оживления аккумулятора (EnsureAccumulatorAsync) и
    // дописать запись в историю, которую тут же затрёт свежий SaveSnapshotAsync.
    // Проверка entry.Accumulator в публикациях тоже идёт под этим локом, чтобы
    // EnsureAccumulatorAsync не мог вклиниться между выбором ветки и самой записью.
    private async Task<AppendResult> AppendIfNotDuplicateStoredNoLockAsync(
        SessionEntry entry,
        Func<StoredMessage, bool> isDuplicate,
        Func<StoredMessage> factory)
    {
        if (entry.Info.ClaudeSessionId is not string key) return AppendResult.NoKey;
        var stored = await _history.LoadAsync(key);
        if (stored.Any(isDuplicate)) return AppendResult.Duplicate;
        stored.Add(factory());
        await _history.SaveAsync(key, stored);
        return AppendResult.Added;
    }

    // Enum (в т.ч. ClaudeMode) сериализуем строками — устойчиво к изменению порядка значений.
    // При чтении конвертер принимает и старый числовой формат.
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILlmSessionAdapterFactory _adapters;
    private readonly LlmProviderRegistry _llmProviders;
    private readonly FalCostService _falCost;
    private readonly UsageService _usage;
    private readonly ITierModelResolver _appSettings;
    // Резолвер моделей агентных мест: пустая модель → назначение места → слот тира
    private readonly Llm.ModelAssignmentResolver _assignments;
    private readonly UserStore _users;
    private readonly JwtService _jwt;
    // Токены грани десктопа (ADR-008): кеш по чату поверх _jwt, отдельный от сервисных
    private readonly Desktop.DesktopCapabilityTokenService _desktopTokens;
    private readonly Microsoft.AspNetCore.Hosting.Server.IServer _server;
    private readonly IConfiguration _config;
    // Копии транскриптов заархивированных чатов (data/archived-transcripts) — шаг 0 плана
    // «Архив чатов»: создаётся в конструкторе напрямую, см. там же
    private readonly ArchivedTranscriptStore _archivedTranscripts;
    // Сервисный токен MCP-серверов — ОДИН на владельца (задачи/заметки/память/персоны/…
    // используют один и тот же owner-scoped JWT), с перевыпуском до истечения. См. GetServiceToken.
    private readonly ConcurrentDictionary<string, (string Token, DateTime IssuedAt)> _serviceTokens = new();

    // Зрители сессии: sessionId → множество SignalR-соединений в её группе.
    // Уникальность по connectionId: повторный JoinSession того же соединения (клиент
    // перезаходит перед каждым send и при reconnect) не раздувает счётчик, а обрыв
    // соединения без LeaveSession вычищается RemoveConnectionViewers из OnDisconnectedAsync.
    // Нужен PersonaAutomationService чтобы не слать уведомления, когда пользователь и так смотрит чат.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessionViewers = new();

    // Добавить зрителя сессии (вызывается из JoinSession)
    public void AddViewer(string sessionId, string connectionId) =>
        _sessionViewers.GetOrAdd(sessionId, _ => new())[connectionId] = 1;

    // Убрать зрителя сессии (вызывается из LeaveSession)
    public void RemoveViewer(string sessionId, string connectionId)
    {
        if (!_sessionViewers.TryGetValue(sessionId, out var conns)) return;
        conns.TryRemove(connectionId, out _);
        if (conns.IsEmpty) _sessionViewers.TryRemove(sessionId, out _);
    }

    // Обрыв соединения (OnDisconnectedAsync): хаб не знает, в какие сессии заходило
    // соединение, — убираем его из всех
    public void RemoveConnectionViewers(string connectionId)
    {
        foreach (var (sid, conns) in _sessionViewers)
        {
            conns.TryRemove(connectionId, out _);
            if (conns.IsEmpty) _sessionViewers.TryRemove(sid, out _);
        }
    }

    // Есть ли хотя бы один зритель у сессии?
    public bool HasViewers(string sessionId) =>
        _sessionViewers.TryGetValue(sessionId, out var conns) && !conns.IsEmpty;

    // Есть ли у сессии живой прогон CLI (ход идёт либо вот-вот начнётся — принят адаптером,
    // процесс поднимается секунды). Тот же предикат, что stuck-детект Interrupt, вынесен
    // наружу для пульса волны «Командной реализации»: «штаб заявляет работу (Working/
    // Waiting), а прогона нет» = мёртвый штаб, а не живая волна.
    public bool HasLiveTurnProcess(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry)
        && entry.Process is { HasLiveTurn: true } or { HasQueuedTurn: true };

    // Наблюдатель сообщений сессий (Claude-исполнитель задач слушает result/permission).
    // Вызывается после обновления статуса и broadcast; его ошибки не роняют пайплайн
    public event Func<Session, ServerMessage, Task>? OnSessionMessage;

    // Текст пользовательского сообщения (ввод в чат) — для push-источников автоматизаций
    // (детекция @упоминаний персон). Вызывается из SendMessageAsync после записи в Accumulator;
    // fire-and-forget, ошибки наблюдателя не роняют ход. (session, text, senderPersonaId)
    public event Func<Session, string, string?, Task>? OnUserMessage;

    // Удаление сессии (чат/проектная сессия) — для авто-движков: сбросить ссылки на чат правила.
    public event Action<Session>? OnSessionDeleted;

    private readonly FeatureFlagService _flags;
    private readonly PersonaManager _personas;
    private readonly PersonaBindingsService _bindings;
    private readonly ClaudeSubscriptionPool _subscriptionPool;
    // Время последней фактической активности аккаунта пула (живой ход/пинг) для идл-пинга
    // подписок (SubscriptionUsageWarmupService); null — в тестах, тогда просто не трогаем.
    private readonly SubscriptionActivityTracker? _activity;
    // Учёт лимитов подписок по rate_limit_event хода: та же точка, что у шлюза LLM. Состояние
    // рекордер не держит (оно в _usage и _subscriptionPool), поэтому свой экземпляр тут не
    // второй счётчик.
    private readonly SubscriptionLimitRecorder _limitRecorder;
    private readonly ILogger<SessionManager> _log;
    // Прогрев сборки свежего worktree (SetWorktreeAsync/AttachWorktreeAsync)
    private readonly WorktreeBuildWarmup _warmup;
    // Фабрика логгеров для вертикали (волна В): TeamPlanService получает собственный
    // типизированный ILogger. null — в тестах без DI, TeamPlanService работает на NullLogger.
    private readonly ILoggerFactory? _loggerFactory;
    // Драйверы среды исполнения владельцев (local / docker-песочница)
    private readonly Execution.ILauncherFactory _launchers;
    private readonly Execution.SandboxManager _sandbox;
    // Домашняя папка владельца ({база по среде}/{username} либо override из конфига)
    private readonly UserHomeResolver _homes;

    private readonly PersonaAgentFileSync? _agentSync;
    // Git-операции worktree чата (null — в тестах: worktree-фича выключена)
    private readonly Git.GitService? _git;
    // Учёт glif-генераций (null — в тестах или когда фича не настроена)
    private readonly GlifAccountService? _glif;
    // Аналитика расхода токенов (null — в тестах: сбор выключен)
    private readonly Spend.ISpendCollector? _spend;
    // Граф кода: уборка снимка отдельного дерева чата при его удалении (ADR-003); null — в тестах
    private readonly CodeGraph.CodeGraphService? _codeGraphs;
    // Watcher'ы файлов: снятие watcher'а отдельного дерева чата при его удалении; null — в тестах
    private readonly FileWatcherService? _fileWatchers;
    // Шов «ядро → штаб» (этап 4, шаг 2б плана выноса штаба): четыре крючка уведомления
    // и запрос IsSessionBusy, реализованные этим же классом как обёртка над прежними
    // приватными методами (см. явные реализации ITeamNotifier ниже). В шаге 2г реализация
    // переедет в вертикаль штаба целиком, а сюда будет приходить через DI.
    private readonly ITeamNotifier _teamNotifier;
    // Четыре шва данных «штаб → ядро» (этап 4, шаг 2г-3б; объявления — Services/Team/
    // TeamCoreSeams.cs). Направление обратное ITeamNotifier: здесь вертикаль спрашивает ядро.
    // Реализация — этот же класс явными реализациями ниже (обёртки над прежними приватными
    // методами, поведение один в один). Поля нужны, чтобы штабной блок ходил в ядро уже через
    // контракт: в шаге 2г тело уедет в вертикаль, и вызовы менять не придётся — только
    // источник интерфейсов (DI вместо this). Каталога сессий среди полей нет намеренно: его
    // спрашивает только вертикаль (у тела штаба, пока оно здесь, entry уже на руках).
    private readonly ITeamHistoryStore _teamHistory;
    private readonly ITeamRunState _teamRunState;
    private readonly ITeamTurnIntake _teamIntake;
    // Координатор решений по карточке плана (волна Г): запуск работы, закрытие интервью
    // и реакция на карточку плана. Owning-паттерн (создаётся в конструкторе, в DI
    // переедет на шаге 2г-4).
    private readonly TeamDecisionService _teamDecision;
    // Квоты и бюджет практики (волна Е): гейт запуска исполнителей и пробуждения штаба
    // агентом, подъём от чата исполнения к штабу, карточка «бюджет исрасходован».
    // Owning-паттерн (создаётся в конструкторе, в DI переедет на шаге 2г-4).
    private readonly TeamBudgetService _teamBudget;
    // Шапка разбора хода штаба и доклад о блокере (волна Ж): HandleTeamTurnCompletedShim
    // (подписчик turn/completed), HandleTeamTurnEndAsync (тело разбора маркеров),
    // RestoreWaveWatchdogIfPaused, TryAutoResolveTeamBlockerAsync, ReportBlockerAsync.
    // Owning-паттерн по тому же рецепту, что у _teamDecision/_teamBudget.
    private readonly TeamTurnCompletionService _teamTurnCompletion;
    // Включение и переключение режима «Командная реализация» (волна Ж): SetTeamImplementAsync
    // и SetTeamImplementAutoAsync. Owning-паттерн по тому же рецепту.
    private readonly TeamEnableService _teamEnable;
    // Личный реестр MCP-серверов владельца + значения их секретов (null — в тестах:
    // ход идёт только со встроенными серверами и наследством .mcp.json)
    private readonly Mcp.McpRegistry? _mcpRegistry;
    private readonly Mcp.McpSecretStore? _mcpSecrets;
    // Последний известный статус серверов: пишется из system/init каждого хода; null — в тестах
    private readonly Mcp.McpStatusStore? _mcpStatus;
    // OAuth внешних серверов: обновление протухшего токена перед сборкой конфига хода; null — в тестах
    private readonly Mcp.McpOAuthService? _mcpOAuth;
    // OAuth-сервис Higgsfield (инстансное подключение): null — в тестах
    private readonly Mcp.HiggsfieldOAuthService? _higgsfieldOAuth;
    // Секция Dify (ApiUrl/ApiKey/неймспейс) — для BuildDifyContext (волна 4): единственное
    // потребление тут — проверка настроенности и строки stdio-ветки отката; вся работа с
    // Dify — в KnowledgeService со своей копией IOptions
    private readonly Models.DifyOptions _dify = new();
    // Реестр задач (опционально, в тестах не передаётся): единственная точка вычисления
    // вычисляемых «связей» сессии-чата (SessionTaskLinks.ParentSessionId / IsTaskDone).
    // TaskManager не зависит от SessionManager (его ctor: config/лог/уведомления/персоны),
    // поэтому прямой ссылки DI-цикла не возникает. Храним Core-шов ITaskLookup (адаптер
    // строится один раз), чтобы сам SessionManager не тянул конкретный TaskManager.
    // null — задача не резолвится (та же семантика, что «резолвер не установлен»:
    // ParentSessionId=null, TaskDone=false).
    private ITaskLookup? _taskLookup;
    // Тест-шов: подменить ITaskLookup. TaskManager в конструктор SessionManager не пробрасывают
    // (DI-цикл), поэтому юниты, которым нужен резолв задачи, подменяют lookup явно.
    internal void SetTaskLookupForTests(ITaskLookup? lookup) => _taskLookup = lookup;

    public SessionManager(ProjectManager projects,
        ChatHistoryService history, IConfiguration config, ILlmSessionAdapterFactory adapters,
        FalCostService falCost, UsageService usage,
        AppSettingsService appSettings, UserStore users, JwtService jwt,
        Microsoft.AspNetCore.Hosting.Server.IServer server,
        LlmProviderRegistry llmProviders,
        FeatureFlagService flags, PersonaManager personas,
        PersonaBindingsService bindings,
        ClaudeSubscriptionPool subscriptionPool,
        ILogger<SessionManager> log,
        Execution.ILauncherFactory launchers,
        Execution.SandboxManager sandbox,
        // Шов Ф4 (Этап 5): ISessionBroadcaster нужен TeamCoordinator — его экземпляр
        // ядро держит в owning-обёртке (см. комментарий TeamCoordinator.cs:14). Сам
        // SessionManager пока сидит на IHubContext<SessionHub> — миграция будет
        // отдельным коммитом (это корневой сервис, см. задачу Ф4).
        Composition.ISessionBroadcaster broadcaster = null!,
        // Опционально (в тестах не передаётся): синк файловых сабагентов-персон
        PersonaAgentFileSync? agentSync = null,
        UserHomeResolver? homes = null,
        // Опционально: «дешёвый» раннер для авто-заголовка чата (локальная модель / claude)
        Llm.ICheapTextRunner? cheap = null,
        // Опционально (в тестах не передаётся): платформа внешних модулей —
        // реестр манифестов + выпуск модульных токенов для их MCP-серверов (R7)
        Modules.ModuleRegistry? modules = null,
        Modules.ModuleTokenService? moduleTokens = null,
        // Опционально (в тестах не передаётся): git-операции worktree чата
        Git.GitService? git = null,
        // Опционально (в тестах не передаётся): сбор расхода токенов (Spend Analytics)
        Spend.ISpendCollector? spend = null,
        // Опционально: резолвер моделей агентных мест (назначения + слоты тиров);
        // без него собирается локально от appSettings — слоты работают и в тестах
        Llm.ModelAssignmentResolver? assignments = null,
        // Опционально (в тестах не передаётся): граф кода и watcher'ы файлов — нужны для уборки
        // за отдельным деревом чата (снимок графа + watcher его файлов), ADR-003
        CodeGraph.CodeGraphService? codeGraphs = null,
        FileWatcherService? fileWatchers = null,
        // Опционально: планирование режима «Командная реализация» (Э2). Без него режим
        // включается, но план не строится — CreateTeamPlanAsync отдаёт причину отказа.
        TeamPlanningService? teamPlanning = null,
        // Опционально (в тестах не передаётся): трекер активности аккаунтов пула подписок
        SubscriptionActivityTracker? activity = null,
        // Опционально: учёт glif-генераций; без него детект glif_cost не работает
        GlifAccountService? glif = null,
        // Опционально (в тестах не передаётся): снимки промпта ходов — кнопка «какой промпт
        // ушёл» под постом. Без него ходы идут как раньше, просто без снимков.
        PromptSnapshotStore? promptSnapshots = null,
        // Опционально (в тестах не передаётся): личный реестр MCP-серверов владельца
        // и значения их секретов — состав серверов хода поверх встроенных
        Mcp.McpRegistry? mcpRegistry = null,
        Mcp.McpSecretStore? mcpSecrets = null,
        // Опционально (в тестах не передаётся): последний известный статус MCP-серверов —
        // наблюдение из system/init каждого хода, фонового поллинга нет
        Mcp.McpStatusStore? mcpStatus = null,
        // Опционально (в тестах не передаётся): OAuth внешних серверов — обновление
        // истекающего токена перед ходом, иначе инструменты сервера получали бы 401
        Mcp.McpOAuthService? mcpOAuth = null,
        // Опционально (в тестах не передаётся): OAuth-сервис Higgsfield (инстансное подключение)
        Mcp.HiggsfieldOAuthService? higgsfieldOAuth = null,
        // Опционально (в тестах не передаётся): паспорта прогонов сабагентов. Без него
        // диагностики обрывов нет и автодобивание молчит — ходы идут как раньше.
        Llm.Claude.SubagentRunLog? subagentRuns = null,
        // Опционально (в тестах не передаётся): паспорта ходов. Без него стор не ведётся,
        // но и контракт «ровно один источник записи» соблюдён — без стора запись не идёт
        // ни в finally фолбэк-адаптера, ни в шинный подписчик (нет подписчика → нет события).
        Llm.TurnRunLog? turnRuns = null,
        // Опционально (в тестах не передаётся): маршрутизатор мест и клиент локальной
        // модели — ветка локального голосового хода (chat-voice). Без них разговор
        // идёт через claude CLI как раньше.
        Llm.LocalActionRouter? router = null,
        Llm.ILocalLlmClient? ollama = null,
        // Опционально (в тестах не передаётся): реестр контрибьюторов секций промпта
        // (этап 2 плана «Шина событий хода»). Без DI бак пуст, шина работает как раньше.
        IEnumerable<Turn.IPromptSectionContributor>? promptSectionContributors = null,
        // Опционально (в тестах не передаётся): шина событий хода (ADR-013). Подписчики
        // SessionManager ведут паспорта ходов/сабагентов и снимки промпта (этап 1).
        Turn.ITurnEventBus? turnEvents = null,
        // Опционально (в тестах не передаётся): реестр задач — единая точка вычисления
        // вычисляемых «связей» сессии (SessionTaskLinks.ParentSessionId / IsTaskDone).
        TaskManager? tasks = null,
        // Опционально (в тестах не передаётся): фабрика логгеров — нужна вертикали
        // TeamPlanService с собственным типизированным логгером (волна В). Без неё
        // TeamPlanService работает на NullLogger.
        ILoggerFactory? loggerFactory = null,
        // Опционально (в тестах не передаётся): готовность устройства локального проекта
        Execution.IProjectDeviceGate? deviceGate = null)
    {
        _deviceGate = deviceGate;
        _turnEvents = turnEvents;
        _loggerFactory = loggerFactory;
        _taskLookup = tasks is null ? null : new Composition.TaskLookupAdapter(tasks);
        _subagentRuns = subagentRuns;
        _turnRuns = turnRuns;
        _router = router;
        _ollama = ollama;

        _mcpRegistry = mcpRegistry;
        _mcpSecrets = mcpSecrets;
        _mcpStatus = mcpStatus;
        _mcpOAuth = mcpOAuth;
        _higgsfieldOAuth = higgsfieldOAuth;
        _promptSnapshots = promptSnapshots;
        _teamPlanning = teamPlanning;
        _activity = activity;
        _glif = glif;
        _spend = spend;
        _codeGraphs = codeGraphs;
        _fileWatchers = fileWatchers;
        _agentSync = agentSync;
        _cheap = cheap;
        _modules = modules;
        _moduleTokens = moduleTokens;
        _git = git;
        _homes = homes ?? UserHomeResolver.WithoutOverrides(appSettings, sandbox);
        _launchers = launchers;
        _sandbox = sandbox;
        _projects = projects;
        _broadcaster = broadcaster;
        _teamCoordinator = new TeamCoordinator(broadcaster);
        // Хранитель состояния режима (волна А): создаётся ДО _teamNotifier/teamHistory
        // и до LoadSessions, чтобы восстановление состояния режима после рестарта
        // (через публичные обёртки WithTeamState) могло идти через TeamStateService
        // сразу. Owning по тому же шаблону, что _teamCoordinator. Волна Б: в
        // конструктор добавлен LlmProviderRegistry — вертикаль сама проверяет
        // CapabilitiesFor(model).SupportsPlanMode при входе в план-фазу.
        _teamState = new TeamStateService(this, _teamPlanning, _projects, _config, llmProviders);
        // _personas обязаны присвоить ДО создания TeamPlanService — иначе вертикаль
        // получает null в конструкторе (порядок инициализации полей в классе идёт до
        // тела конструктора, а тело выполняется по тексту). На строгом конструкторе
        // это поймал бы компилятор (CS8618), но поле non-null и присваивается ниже,
        // поэтому компилятор верит, а в runtime — null.
        _personas = personas;
        // Волна В: создаётся ПОСЛЕ _teamState, потому что TeamPlanService зовёт публичные
        // обёртки ядра (RestoreUserMode, SaveSessions, BroadcastTeamImplementAsync,
        // ResolveTeamPlanRoot) и видит швы данных через сам SessionManager. Жизненный цикл
        // симметричен _teamCoordinator/TeamStateService — owning в конструкторе, регистрация
        // через DI придёт в шаге 2г-4.
        _teamPlan = new TeamPlanService(this, this, this, _teamPlanning, _teamCoordinator,
            _personas, _projects, _teamState,
            // Опциональный ILoggerFactory (для вертикали TeamPlanService с собственным
            // типизированным логгером). В тестах SessionManagerTests логгер не передаётся —
            // подменяем на null-логгер, чтобы вертикаль могла логировать не падая.
            loggerFactory?.CreateLogger<TeamPlanService>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamPlanService>.Instance);
        // Волна Г: координатор решений по карточке плана (запуск работы, закрытие
        // интервью, реакция на карточку). Owning-паттерн (создаётся в конструкторе
        // SessionManager — здесь тот же приём, что у _teamCoordinator/_teamState/_teamPlan).
        // В DI переедет на шаге 2г-4, owning-обёртки StartTeamWorkAsync/CloseTeamTalkAsync/
        // RespondTeamPlanAsync в SessionManager будут сняты.
        _teamDecision = new TeamDecisionService(this, this, this, this, this, _personas, _teamState, _teamPlan,
            loggerFactory?.CreateLogger<TeamDecisionService>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamDecisionService>.Instance);
        // Квоты и бюджет практики (волна Е). Owning-паттерн (создаётся в конструкторе,
        // в DI переедет на шаге 2г-4) разрывает цикл «SessionManager хочет TeamBudgetService,
        // TeamBudgetService хочет SessionManager» — вертикаль видит ядро по прямой ссылке,
        // а ядро знает о вертикали через поле _teamBudget.
        _teamBudget = new TeamBudgetService(this, this, this, _teamState,
            loggerFactory?.CreateLogger<TeamBudgetService>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamBudgetService>.Instance);
        // Шапка разбора хода штаба и доклад о блокере (волна Ж): owning-паттерн по тому же
        // рецепту, что у _teamDecision/_teamBudget. Подписчик turn/completed переехал в
        // вертикаль — шинная подписка ниже регистрирует делегат на метод TeamTurnCompletionService.
        _teamTurnCompletion = new TeamTurnCompletionService(this, this, this, this, _teamDecision,
            loggerFactory?.CreateLogger<TeamTurnCompletionService>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamTurnCompletionService>.Instance);
        // Включение и переключение режима «Командная реализация» (волна Ж): SetTeamImplementAsync
        // и SetTeamImplementAutoAsync. Owning-паттерн по тому же рецепту, что и _teamBudget.
        _teamEnable = new TeamEnableService(this, this, this, this, _teamState,
            loggerFactory?.CreateLogger<TeamEnableService>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TeamEnableService>.Instance);
        _history = history;
        _adapters = adapters;
        _llmProviders = llmProviders;
        _falCost = falCost;
        _usage = usage;
        _appSettings = appSettings;
        _assignments = assignments ?? new Llm.ModelAssignmentResolver(appSettings);
        _users = users;
        _jwt = jwt;
        _desktopTokens = new Desktop.DesktopCapabilityTokenService(jwt);
        _server = server;
        _config = config;
        // Копии транскриптов архивных чатов (шаг 0 плана «Архив чатов»): стор файловый и
        // без зависимостей, создаём напрямую — точки вызова (SetArchived/DeleteAsync) живут
        // здесь, отдельная регистрация в Program.cs ничего не добавляет.
        _archivedTranscripts = new ArchivedTranscriptStore(config);
        config.GetSection(Models.DifyOptions.Section).Bind(_dify);
        // Sweep-terminus grace (P12/P15): потолок ожидания exited после result до принудительного
        // Active→Finished. <=0 — выключить sweep (только для тестов/отладки).
        _stuckActiveGraceSeconds = int.TryParse(config["Session:StuckActiveGraceSeconds"], out var grace)
            && grace >= 0 ? grace : DefaultStuckActiveGraceSeconds;
        // Порог свежести хода для гейта перезапуска (этап 3): тот же ключ/дефолт, что у
        // пульса волны — TeamWaveService._quietThreshold
        _freshTurnThreshold = TimeSpan.FromMinutes(
            int.TryParse(config["TeamImplement:QuietMinutes"], out var quietMin) && quietMin > 0 ? quietMin : 15);
        _flags = flags;
        // _personas инициализирован выше (до создания TeamPlanService, чтобы вертикаль
        // не получила null в конструкторе).
        _bindings = bindings;
        _subscriptionPool = subscriptionPool;
        _limitRecorder = new SubscriptionLimitRecorder(_usage, subscriptionPool, _activity);
        _log = log;
        _warmup = new WorktreeBuildWarmup(launchers, config, log);

        // Швы данных «штаб → ядро» (ITeamHistoryStore/ITeamRunState/ITeamTurnIntake)
        // назначаем ДО создания TeamPlanService/TeamDecisionService: они передают `this`
        // в конструкторы вертикальных сервисов, и без присваивания поля upcast'ятся в null.
        // Тот же приём, что у _teamNotifier (волна Б), но вынесен выше — вертикальная
        // команда растёт, и швы нужны раньше создания самих вертикальных сервисов.
        _teamHistory = this;
        _teamRunState = this;
        _teamIntake = this;
        // Найденную стоимость fal.ai публикуем в SignalR + историю
        _falCost.OnCostResolved = PublishFalCostAsync;
        // Изменение персоны (профиль/возможности/привязки) — сбрасываем адаптеры её живых
        // сессий, чтобы Tool-рубильники и MCP-серверы перемонтировались со следующего хода
        _personas.OnPersonaChanged += p => InvalidatePersonaSessions(p.Id);

        // Шов «ядро → штаб» смотрит на this через ITeamNotifier. Назначаем ДО LoadSessions,
        // чтобы TrySweepStuckActive (его зовёт SaveSessions) мог читать TeamPlanningInFlight
        // через этот шов, а SaveSessions может сработать из конструктора через
        // Llm.ChatTopicMigration.Apply.
        _teamNotifier = this;

        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _sessionsFilePath = Path.Combine(dataDir, "sessions.json");

        // Подписки на шину событий хода (ADR-013, этап 1): снимок промпта → PromptSnapshotStore,
        // паспорт сабагента → SubagentRunLog + side-effects, паспорт хода → TurnRunLog.
        // Шина регистрируется через DI как singleton; в тестах без DI лениво создаётся
        // свой экземпляр на SessionManager (TurnEvents), чтобы подписчики могли работать.
        {
            var bus = TurnEvents;
            bus.OnNotification<PromptAssembled>(HandlePromptAssembled,
                "SessionManager.PromptSnapshotStore");
            bus.OnNotification<SubagentRunCompleted>(HandleSubagentRunCompleted,
                "SessionManager.SubagentRunLog");
            bus.OnNotification<TurnCompleted>(HandleTurnCompleted,
                "SessionManager.TurnRunLog");
            // Этап 4 / шаг 1б: теневой подписчик turn/completed для штаба. ИНЕРТЕН — боевую
            // логику HandleTeamTurnEndAsync НЕ зовёт, только сверяет факт вызова старого пути с
            // тем, что диктует Outcome. Старый путь работает как единственный. Переключение
            // (под-шаг 3) снимет прямой вызов из OnMessageAsync и дедуп SkipNextTeamTurnEnd.
            bus.OnNotification<TurnCompleted>(HandleTeamTurnCompletedShim,
                "SessionManager.TeamShadowSubscriber");
            // Этап 2: реестр IPromptSectionContributor (7 контрибьюторов)
            // подключается к шине Filter-событием prompt/assembling. В тестах без DI бак
            // пуст — сборка секций остаётся на инлайне (он закрыт гейтами IsEnabled).
            if (promptSectionContributors is not null)
            {
                Turn.PromptSectionContributorsRegistration.RegisterAll(bus, promptSectionContributors);
            }
        }

        LoadSessions();

        // Автосохранение: периодический сброс in-memory данных на диск
        var autoSave = int.TryParse(config["Session:AutoSaveSeconds"], out var secs)
            ? TimeSpan.FromSeconds(secs)
            : DefaultAutoSaveInterval;
        if (autoSave > TimeSpan.Zero)
            _autoSaveTimer = new Timer(_ => SaveSessions(), null, autoSave, autoSave);

        // Тик ожидания по маркеру `<waiting>`. Тот же гейт `autoSave > Zero` — чтобы тесты
        // с `Session:AutoSaveSeconds = 0` не оставляли тик работающим: фоновые таймеры
        // выполняют произвольный код в произвольный момент и между ассертами теста гонят
        // директиву и сбрасывают счётчики (та же причина, что у _autoSaveTimer). Дефолты
        // подхватываются из Loop:* если ключ не задан или мусор — см. ParsePositiveSeconds/
        // ParsePositiveInt (Loop:MaxIterations ходит через LoopLimitOrDefault со своей
        // семантикой «<=0 = дефолт 20», не трогаем).
        _waitingTickInterval = ParsePositiveSeconds(config["Loop:WaitingTickSeconds"], 300);
        _maxWaitingTicks = ParsePositiveInt(config["Loop:MaxWaitingTicks"], 20);
        if (autoSave > TimeSpan.Zero && _waitingTickInterval > TimeSpan.Zero)
            _waitingTickTimer = new Timer(_ => FireAndForget(TickWaitingLoopsAsync(), "тик ожидания work-loop"),
                null, _waitingTickInterval, _waitingTickInterval);
    }

    // Положительный интервал (секунды) из конфига с дефолтом: <=0 и мусор → defaultValue.
    private static TimeSpan ParsePositiveSeconds(string? raw, int defaultSeconds) =>
        int.TryParse(raw, out var v) && v > 0 ? TimeSpan.FromSeconds(v) : TimeSpan.FromSeconds(defaultSeconds);

    // Положительное целое из конфига с дефолтом: <=0 и мусор → defaultValue.
    private static int ParsePositiveInt(string? raw, int defaultValue) =>
        int.TryParse(raw, out var v) && v > 0 ? v : defaultValue;

    // --- MCP tasks-server ---

    // Базовый URL API для MCP-сервера: среда владельца (из песочницы Kestrel виден
    // как host.docker.internal) → конфиг → адрес Kestrel → дефолт.
    // 0.0.0.0/[::] заменяем на localhost — MCP-сервер ходит с той же машины.
    // Среди адресов Kestrel предпочитаем http: MCP-серверы на node ходят обычным
    // fetch, а боевой серт выписан на внешний домен — по https://localhost они
    // упираются в ERR_TLS_CERT_ALTNAME_INVALID (localhost/127.0.0.1 нет в SAN).
    // Если http-адреса нет вообще, поднимите локальный http-эндпоинт и пропишите
    // McpTasksApiUrl явно — иначе все MCP-прокси (tasks/notes/memory/wsp) отвалятся.
    internal string ResolveTasksApiUrl(string? ownerId = null)
    {
        if (ownerId is not null && _launchers.ForOwner(ownerId).McpApiUrlOverride is { } sandboxUrl)
            return sandboxUrl.TrimEnd('/');

        var fromConfig = _config["McpTasksApiUrl"];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig.TrimEnd('/');

        var addresses = _server.Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?
            .Addresses;
        var addr = addresses?.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                   ?? addresses?.FirstOrDefault();
        if (string.IsNullOrEmpty(addr)) return "http://localhost:5000";
        return addr.Replace("0.0.0.0", "localhost").Replace("[::]", "localhost").TrimEnd('/');
    }

    // tasks-MCP доступен, когда разрешён персоной (Persona.Tools/привязки), ЛИБО сессия
    // является исполнителем задачи — тогда tasks-MCP форсируется: исполнитель обязан
    // управлять задачей через mcp__tasks__* (иначе ограниченная персона не сможет её
    // ни прочитать, ни завершить и свалится в нерабочий встроенный Task-тул).
    // internal — ту же формулу резолвит TasksToolset по живой сессии-вызывателю (http,
    // ADR-012 волна 2): право на сервер проверяется на каждый tools/list и tools/call
    internal bool TasksMcpEnabled(string? ownerId, Session session, Persona? persona) =>
        session.TaskExecution || _bindings.EffectiveToolEnabled(ownerId, persona, "tasks");

    // Единый сервисный токен владельца для MCP-серверов (tasks/notes/memory/personas/…):
    // per-owner JWT с перевыпуском за сутки до истечения (сервер может жить дольше срока токена).
    internal string GetServiceToken(string ownerId) =>
        _serviceTokens.AddOrUpdate(ownerId,
            id => (_jwt.IssueServiceToken(id), DateTime.UtcNow),
            (id, old) => DateTime.UtcNow - old.IssuedAt > JwtService.ServiceTokenLifetime - TimeSpan.FromDays(1)
                ? (_jwt.IssueServiceToken(id), DateTime.UtcNow)
                : old).Token;

    // Контекст MCP-сервера задач для сессии; null — только для чата без владельца.
    // persona — для кросс-проектных ProjectTasks-привязок (доступ к задачам ДРУГИХ проектов).
    // Токен — фабрикой (ADR-012 волна 2, как widgets/memory): захваченный строкой JWT у
    // долгоживущего чата истекал, и задачи пропадали у модели молча.
    private TasksMcpContext? BuildTasksContext(string? ownerId, string? projectId, Persona? persona = null)
    {
        if (ownerId is null) return null;
        var extraScopes = _bindings.BuildExternalTaskScopes(ownerId, persona);
        var extraIds = extraScopes.Select(s => s.ProjectId).Distinct().ToList();
        var extraReadOnly = extraScopes.Where(s => s.ReadOnly).Select(s => s.ProjectId).Distinct().ToList();
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new TasksMcpContext(apiUrl, () => GetServiceToken(ownerId), projectId,
            extraIds.Count > 0 ? extraIds : null, extraReadOnly.Count > 0 ? extraReadOnly : null,
            UseHttp: HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера заметок; null — только для чата без владельца.
    // Модуль комментариев к документам и редких операций — за ключом notes-annotations
    // (дефолт выключен, решение ПО ПЕРСОНЕ: PersonaBindingsService.SectionEnabled).
    private NotesMcpContext? BuildNotesContext(string? ownerId, string? projectId, Persona? persona)
    {
        if (ownerId is null) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new NotesMcpContext(apiUrl, () => GetServiceToken(ownerId), projectId,
            AnnotationsEnabled: _bindings.SectionEnabled(ownerId, persona, "notes-annotations"),
            UseHttp: HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера виджетов чата: адрес API и сервисный токен владельца — сервер
    // переехал в Kestrel (ADR-012), ход зовёт его по http, а не поднимает процесс node.
    // Адрес даёт та же ResolveTasksApiUrl, что и остальным серверам: второго правила про
    // песочницу здесь нет — оно уже внутри резолвера.
    // Фича штатная (без фич-флага), как personas/notifications; персона может выключить
    // сервер Off-привязкой tool:widgets (PersonaBindingsService.ServerToolEnabled).
    private WidgetsMcpContext? BuildWidgetsContext(string? ownerId, Persona? persona)
    {
        if (ownerId is null || !_bindings.ServerToolEnabled(ownerId, persona, "widgets")) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        // Токен — фабрикой, как у памяти: контекст живёт столько же, сколько адаптер, а
        // захваченный строкой JWT у долгоживущего чата истекал (ADR-012, урок фазы 1)
        return new WidgetsMcpContext(apiUrl, () => GetServiceToken(ownerId), HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера сторожей чатов: null — только для чата без владельца.
    // Сессия-вызыватель едет хвостом URL — как у tasks/notes волны 2.
    private WatchMcpContext? BuildWatchContext(string? ownerId)
    {
        if (ownerId is null) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new WatchMcpContext(apiUrl, () => GetServiceToken(ownerId!), HttpEndpointUsable(apiUrl));
    }

    // Контекст MCP-сервера веб-поиска: null — чат без владельца ИЛИ пустой Perplexity:ApiKey
    // (единственный рубильник фичи, как у Dify). Ключ читается ЖИВЬЁМ, но остаётся свойством
    // ИНСТАНСА, а не хода: инвариант стабильности состава (ADR-012) не задет — правка ключа
    // штатно меняет сигнатуру запуска и перезапускает CLI, как любое изменение shapes.
    // Сам ключ в контекст не кладётся: наружу он не уезжает, тулсет берёт его из конфига.
    //
    // Третье условие — возможность персоны «web» (EffectiveToolEnabled, та же, что гасит
    // встроенные WebSearch/WebFetch у CLI): персона с выключенным веб-поиском не должна
    // получать обходной путь в интернет через MCP. Гейт стоит на объявлении сервера, а не
    // только в запретах: так у такой персоны схемы ещё и не занимают окно. Персона — свойство
    // сессии, инвариант стабильности состава не задет (как у widgets).
    private WebSearchMcpContext? BuildWebSearchContext(string? ownerId, Persona? persona)
    {
        if (ownerId is null) return null;
        if (string.IsNullOrWhiteSpace(_config["Perplexity:ApiKey"])) return null;
        if (!_bindings.EffectiveToolEnabled(ownerId, persona, "web")) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new WebSearchMcpContext(apiUrl, () => GetServiceToken(ownerId!), HttpEndpointUsable(apiUrl));
    }

    // MCP-сервер Higgsfield: инстансное OAuth-подключение (единый вход админа, шарится
    // всеми владельцами). Узел присутствует в конфиге хода только когда:
    //   1) инстанс подключён (EnsureFresh() ≠ null) — иначе прокси некому ретранслировать
    //   2) персона НЕ ReadOnly — RO-гейт (аналог «инстанс не подключён → узла нет»)
    // URL — тот же Kestrel, что и websearch: /mcp/higgsfield/{sessionId}.
    // Токен — сервисный JWT владельца (Bearer), как у websearch/dify/wsp.
    internal HiggsfieldMcpContext? BuildHiggsfieldContext(string? ownerId, Persona? persona)
    {
        if (ownerId is null) return null;
        if (_higgsfieldOAuth is null) return null;
        // RO-гейт: ReadOnly-персона не получает higgsfield (аналог «инстанс не подключён»)
        if (persona is { Access: PersonaAccess.ReadOnly }) return null;
        // Инстанс подключён: EnsureFresh() ≠ null
        if (_higgsfieldOAuth.EnsureFresh() is null) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new HiggsfieldMcpContext(apiUrl, () => GetServiceToken(ownerId!), HttpEndpointUsable(apiUrl));
    }

    // MCP-сервер локальной генерации (ComfyUI на своей GPU). Узел есть в конфиге хода, только когда:
    //   1) включён машинный тумблер LocalMedia:Enabled и подсистема images (там живёт движок);
    //   2) чат проекта, чьи файлы на сервере: результат пишется в папку проекта
    //      (локальный проект ADR-016 — отказ через ProjectCapabilities);
    //   3) персона НЕ ReadOnly — сервер пишет файлы (тот же RO-гейт, что у higgsfield).
    // Всё это — свойства инстанса, сессии и персоны; тумблер читается живьём, но его поворот
    // штатно меняет сигнатуру запуска, как правка ключа у websearch. Гейты тулсета на вызове
    // повторяют проверки (defense-in-depth).
    internal LocalMediaMcpContext? BuildLocalMediaContext(string? ownerId, string? projectId, Persona? persona)
    {
        if (ownerId is null || projectId is null) return null;
        if (!Images.LocalMedia.LocalMediaOptions.IsEnabled(_config)) return null;
        if (!Composition.SubsystemGate.IsEnabled(_config, "images")) return null;
        if (persona is { Access: PersonaAccess.ReadOnly }) return null;
        if (_projects.GetById(projectId) is not { } project || !ProjectCapabilities.FilesOnServer(project)) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new LocalMediaMcpContext(apiUrl, () => GetServiceToken(ownerId), HttpEndpointUsable(apiUrl));
    }

    // Допускает ли АДРЕС бэкенда http-транспорт (ADR-012) — СХЕМА и форма строки, без
    // рубильника. Не http — значит https: боевой серт выписан на внешний домен, CLI упрётся
    // в ERR_TLS_CERT_ALTNAME_INVALID и спрячет инструмент от модели МОЛЧА, а *.naychenko.me
    // ещё и редиректится на https в пайплайне. В этом случае ход объявляет прежний
    // stdio-сервер, а причина уходит в лог: тихо терять инструмент нельзя. Схема — свойство
    // адреса контекста и в жизни адаптера не меняется; рубильник Mcp:HttpTransport сюда НЕ
    // входит — он живой, читается провайдером на каждый ход (HttpMcpEnabledProvider).
    private string? _httpMcpWarnedFor;
    private bool HttpEndpointUsable(string apiUrl)
    {
        var http = Services.Mcp.Http.McpHttpTransport.Usable(apiUrl, enabled: true);
        // Предупреждаем один раз на адрес: состояние постоянное, ход идёт каждую минуту.
        // Узнаёт и хозяин выключенного рубильника: включённый транспорт этот адрес всё равно
        // не поднимет — инстанс останется на stdio.
        if (!http && Interlocked.Exchange(ref _httpMcpWarnedFor, apiUrl) != apiUrl)
            _log.LogWarning("MCP-over-HTTP невозможен: адрес бэкенда «{Url}» не http — "
                + "продуктовые серверы объявляются ходу по-старому, через stdio", apiUrl);
        return http;
    }

    // Живое значение рубильника Mcp:HttpTransport для провайдера контекста: IConfiguration
    // перечитывается (appsettings.Local.json подключён с reloadOnChange), поэтому поворот
    // ключа доезжает до уже поднятых чатов следующим ходом — без рестарта бэкенда.
    private bool HttpMcpEnabled() =>
        _config.GetValue(Services.Mcp.Http.McpHttpTransport.EnabledKey, true);

    // Сводный признак «у сессии есть продуктовые MCP-серверы, чей адрес допускает http»
    // (ADR-012): от него (вместе с живым рубильником) зависит NO_PROXY хода — обход прокси
    // нужен ЛЮБОМУ http-серверу, а не только виджетам. Решение стоит на едином гейте
    // HttpEndpointUsable (каждый Build*Context уже прогнал через него свой UseHttp) —
    // отдельного условия «виджеты на http» больше нет; во волне 2 к widgets/memory
    // добавились tasks/notes/personas, в волне 3 — wsp/notifications/codegraph, в волне 4 —
    // dify. pmem-консультанты приезжают списком на каждый ход и уточняют признак на стороне
    // ClaudeSession.
    private static bool HttpMcpActive(WidgetsMcpContext? widgets, MemoryMcpContext? memory,
        TasksMcpContext? tasks = null, NotesMcpContext? notes = null, PersonasMcpContext? personas = null,
        WorkspaceMcpContext? workspace = null, NotificationsMcpContext? notifications = null,
        CodeGraphMcpContext? codeGraph = null, DifyMcpContext? dify = null,
        WatchMcpContext? watch = null, WebSearchMcpContext? webSearch = null,
        HiggsfieldMcpContext? higgsfield = null, LocalMediaMcpContext? localMedia = null) =>
        widgets is { UseHttp: true } || memory is { UseHttp: true }
        || tasks is { UseHttp: true } || notes is { UseHttp: true } || personas is { UseHttp: true }
        || workspace is { UseHttp: true } || notifications is { UseHttp: true }
        || codeGraph is { UseHttp: true } || dify is { UseHttp: true }
        || watch is { UseHttp: true } || webSearch is { UseHttp: true }
        || higgsfield is { UseHttp: true } || localMedia is { UseHttp: true };

    // Браузер (плагин playwright): нужен по роли тестировщику, остальным персонам — нет.
    // Ключ-надстройка «browser» с дефолтом по пресету (SectionEnabled → SpecialtySections),
    // как git/kb; чат без персоны получает браузер как раньше — ручную проверку страницы
    // человек делает из своего чата. Решение зависит только от персоны, поэтому постоянно
    // в рамках сессии (оно входит в сигнатуру прогона — см. ClaudeRuntimeSettings).
    private bool BrowserEnabled(string? ownerId, Persona? persona) =>
        persona is null || _bindings.SectionEnabled(ownerId, persona, "browser");


    // Контекст MCP-сервера баз знаний Dify (ADR-012, волна 4 — последний продуктовый
    // сервер фазы 2). Подключается при настроенной секции Dify (ApiUrl/ApiKey — источник
    // правды ключа с волны 4: внешний dify-узел базового конфига им перекрывается).
    // Проект/дефолтный датасет в контекст не входят: тулсет резолвит их живьём из
    // сессии-вызывателя (датасет появляется у проекта в середине жизни чата).
    // DifyUrl/DifyKey — только stdio-ветке отката (env узла mcp-dify/dist).
    private DifyMcpContext? BuildDifyContext(string? ownerId)
    {
        if (ownerId is null) return null;
        if (string.IsNullOrEmpty(_dify.ApiUrl) || string.IsNullOrEmpty(_dify.ApiKey)) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new DifyMcpContext(apiUrl, _dify.ApiUrl.TrimEnd('/'), _dify.ApiKey,
            () => GetServiceToken(ownerId), UseHttp: HttpEndpointUsable(apiUrl));
    }

    // Работает ли у проекта чата группа «нужен контент на сервере» (ADR-016 §4). Чат вне
    // проекта — да: его папка серверная
    private bool ServerContentFor(string? projectId) =>
        projectId is null || _projects.GetById(projectId) is not { } p || ProjectCapabilities.ServerContentEnabled(p);

    // Лежит ли транскрипт CLI чата на диске сервера (ADR-016). У чата локального проекта — нет:
    // сервер не ищет, не копирует и не удаляет его по своему пути, промах поиска значил бы
    // «транскрипта нет» там, где он просто на другой машине. Чат вне проекта — серверный.
    private bool TranscriptOnServer(Session info) =>
        info.ProjectId is null || _projects.GetById(info.ProjectId) is not { } p || ProjectCapabilities.TranscriptOnServer(p);

    // Контекст MCP-сервера графа кода: инструменты codegraph_* доступны только в чате проекта —
    // граф ключуется проектом (в чате вне проекта искать нечего). Тот же сервисный токен
    // владельца, что у tasks/notes; владение проектом дополнительно проверяет CodeGraphController.
    // rootPath — рабочее дерево сессии (EffectiveRoot): у чата с отдельным worktree свой граф,
    // иначе инструменты смотрели бы в основное дерево, а правки шли в другое (ADR-003).
    // Персона может выключить граф Off-привязкой tool:codegraph — тогда нет ни сервера,
    // ни slice в промпте (CodeGraphContributor).
    private CodeGraphMcpContext? BuildCodeGraphContext(string? ownerId, string? projectId, string sessionId,
        string? rootPath, Persona? persona)
    {
        if (ownerId is null || string.IsNullOrEmpty(projectId)) return null;
        // CodeGraph — группа «контент на сервере»: у локального проекта инструмента нет
        if (!ServerContentFor(projectId)) return null;
        if (!_bindings.ServerToolEnabled(ownerId, persona, "codegraph")) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new CodeGraphMcpContext(apiUrl, () => GetServiceToken(ownerId), projectId, sessionId, rootPath,
            UseHttp: HttpEndpointUsable(apiUrl));
    }

    // Право чата на десктопную грань по СУЩНОСТИ чата — единая точка правды (ADR-008:
    // «Грань не доставляется в ходы исполнения задач, отложенные и регулярные чаты,
    // групповые чаты»), никаких дублей-предикатов рядом. internal static — чистая функция,
    // тестируется напрямую (DesktopTurnEligibleTests).
    internal static bool DesktopTurnEligible(Session session) =>
        // Десктопный ли чат (тип чата «Десктопный») — свойство конфигурации чата, а не хода
        session.DesktopChat
        // Чат-исполнитель задачи (в том числе отложенной и регулярной — их создаёт
        // TaskExecutionService по расписанию, человека у машины в этот момент нет) и чат
        // правила проактивности: Origin выводится из TaskId/AutomationRuleId (Session.Origin)
        && session.Origin == ChatOrigin.Manual
        && !session.TaskExecution
        // Групповой чат: руки одного устройства на несколько собеседников не делятся.
        // Participants заполняется ТОЛЬКО у групповых (ValidateParticipants: 2–8 персон);
        // чат с одной персоной хранит её в PersonaId — поэтому «есть участники» == «групповой».
        // Проверяем Count > 0, а не Count > 1: если валидацию состава когда-нибудь ослабят
        // до одиночных участников, грань не должна молча поехать в чат с чужой персоной.
        && session.Participants is not { Count: > 0 };

    // Контекст MCP-сервера десктопной грани (ADR-008, «Два уровня, которые нельзя смешивать»):
    // состав грани решает КОНФИГУРАЦИЯ на момент запуска CLI — тип чата «Десктопный» плюс
    // включение грани в проекте, — и никогда состояние хода. Право на каждый конкретный вызов
    // проверяет бэкенд (DesktopAccessGate), поэтому здесь нет ни сеанса рук, ни устройства:
    // их появление и исчезновение не должно менять tools/list и перезапускать процесс CLI.
    // Право чата по его сущности — DesktopTurnEligible (единственная точка правды);
    // персона может отказаться от грани Off-привязкой tool:desktop, как от codegraph/widgets.
    private DesktopMcpContext? BuildDesktopContext(string? ownerId, Session session, Persona? persona)
    {
        if (ownerId is null || string.IsNullOrEmpty(session.ProjectId)) return null;
        if (!DesktopTurnEligible(session)) return null;
        if (!_flags.IsEnabled(ownerId, FeatureFlagKeys.DesktopAgent)) return null;
        if (_projects.GetById(session.ProjectId!)?.DesktopAgentEnabled != true) return null;
        if (!_bindings.ServerToolEnabled(ownerId, persona, "desktop")) return null;
        // Capability-токен чата, а не сервисный JWT владельца: /api/devices/* его не принимают
        return new DesktopMcpContext(ResolveTasksApiUrl(ownerId),
            _desktopTokens.TokenFor(ownerId, session.Id), session.Id);
    }

    // Контекст MCP-сервера памяти персоны (та же фабрика сервисного токена, что у tasks/notes).
    // projectId — проект ТЕКУЩЕГО чата (③-3.4: даёт доступ к team_memory_* команды), не scope
    // персоны — см. BuildPersonaLayer: любая персона в проектном чате получает эти инструменты,
    // пишет ли она в команду реально — решает бэкенд-гейт (TeamMemoryService.WriteDeniedFor).
    // DossierToolsEnabled — секция dossier_lookup/dossier_get (этап 2, ADR-004 §5): гейт по
    // флагу ВЛАДЕЛЬЦА change-dossiers-recall. Решение стабильно в рамках сессии (флаг меняется
    // человеком из меню редко) и входит в отпечаток состава сервера (shapes memory) — от
    // СВОЙСТВ ХОДА состав tools/list не зависит (инвариант McpToolsetStabilityTests).
    // UseHttp — сервер памяти переехал в Kestrel (ADR-012, фаза 2): ход зовёт его по http
    // (personaId/projectId едут хвостом URL), процесса node нет; false — откат на stdio.
    private MemoryMcpContext BuildMemoryContext(string ownerId, string personaId, string? projectId)
    {
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new MemoryMcpContext(apiUrl, () => GetServiceToken(ownerId), personaId, projectId,
            DossierToolsEnabled: _flags.IsEnabled(ownerId, FeatureFlagKeys.ChangeDossiersRecall),
            UseHttp: HttpEndpointUsable(apiUrl));
    }

    // Контекст memory-server для проектной сессии БЕЗ персоны: только team_memory_* (③-3.4) —
    // память проекта доступна из ЛЮБОГО чата проекта, не только персонного (personaId пуст →
    // personal-инструменты memory_* не регистрируются, см. mcp/memory-server/index.js).
    private MemoryMcpContext? BuildTeamMemoryContext(string? ownerId, string? projectId) =>
        ownerId is not null && !string.IsNullOrEmpty(projectId)
            ? BuildMemoryContext(ownerId, "", projectId)
            : null;

    // --- Персистентность сессий ---

    private void LoadSessions()
    {
        var list = JsonFileStore.Load<List<Session>>(_sessionsFilePath, _jsonOpts);
        if (list is null) return;
        List<string> abortedMidTurn = [];
        foreach (var session in list)
        {
            // Процесс умер при рестарте — "живые" статусы переводим в orphaned
            var wasLive = session.Status is SessionStatus.Working or SessionStatus.Waiting;
            session.Status = session.Status switch
            {
                SessionStatus.Working or SessionStatus.Starting or SessionStatus.Waiting
                    => SessionStatus.Orphaned,
                SessionStatus.Active => SessionStatus.Finished,
                _ => session.Status,
            };
            if (wasLive && session.ClaudeSessionId is not null)
                abortedMidTurn.Add(session.ClaudeSessionId);
            _sessions[session.Id] = new SessionEntry { Info = session };
        }
        // Значок темы переехал из имени в Session.Topic — переносим старые эмодзи-имена.
        // Идемпотентно: на уже перенесённых сессиях это no-op без записи на диск
        if (Llm.ChatTopicMigration.Apply(list)) SaveSessions();
        // Маркер обрыва в историю оборванных ходов — фоном, чтобы не тормозить старт
        if (abortedMidTurn.Count > 0)
            _ = Task.Run(async () =>
            {
                foreach (var csid in abortedMidTurn)
                    try { await _history.AppendTurnAbortedAsync(csid); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[SessionManager] Маркер обрыва хода ({csid}) не записан: {ex.Message}");
                    }
            });
    }

    // Публичный (волна В): TeamPlanService сохраняет каталог сессий после правки PlanCardId/
    // PlanVersion/Replanning — через тот же канал, что и TeamWaveService.
    public void SaveSessions()
    {
        lock (_saveLock)
        {
            try
            {
                var sessions = _sessions.Values.Select(e => e.Info).ToList();
                JsonFileStore.Save(_sessionsFilePath, sessions, _jsonOpts);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Не удалось сохранить {_sessionsFilePath}: {ex.Message}");
            }

            // P12/P15: sweep-terminus. SaveSessions зовётся из ~50 точек (включая автосохранение
            // по таймеру Session:AutoSaveSeconds) — этого довольно, чтобы без отдельного таймера
            // гарантированно довести зависшую в Active сессию до Finished. Ранний выход, когда
            // кандидатов нет, — O(N) под _saveLock только если хоть одна сессия в «завершённом Active».
            if (_stuckActiveGraceSeconds <= 0) return;
            if (!_sessions.Values.Any(e => e.Info.Status == SessionStatus.Active && e.LastTurnEndedAt.HasValue))
                return;

            // P28: id сессий, в поддереве которых (по иерархии делегирования) есть живой
            // исполнитель. Корень такого поддерева не терминируется, иначе ложный Finished в
            // разгар волны (дефект P28). Рекурсивно в ГЛУБИНУ: цепочка делегирования законна до
            // глубины 3 (координатор → исполнитель → суб-исполнитель), и если жив только нижний
            // узел, а средний уже отдал result и ждёт потомка — правило по прямым детям закрыло
            // бы корень, и дефект просто уехал бы на уровень глубже.
            //
            // Живость узла — HasLiveWork (мёртвый процесс не жив ни при каком статусе: клинч P30,
            // когда Waiting залипает у мёртвого исполнителя, не должен держать предков вечно).
            // Иерархия — по TaskId → SourceSessionId (резолв lock-free через ConcurrentDictionary в
            // TaskManager), без пятого хранилища состояния. От каждого живого узла поднимаемся к
            // корню, отмечая всех предков. Ограничитель цикла — как в IsDescendantOf: данных из
            // бэкапа достаточно для кольца в иерархии, и битая связь не должна завесить SaveSessions.
            var protectedByLiveSubtree = new HashSet<string>();
            foreach (var e in _sessions.Values)
            {
                if (!HasLiveWork(e)) continue;
                var cur = ParentSourceSession(e.Info.Id);
                for (var steps = 0; cur is not null && steps < 256; steps++)
                {
                    if (!protectedByLiveSubtree.Add(cur)) break; // уже отмечен — цикл или общий предок
                    cur = ParentSourceSession(cur);
                }
            }

            foreach (var entry in _sessions.Values)
                TrySweepStuckActive(entry, protectedByLiveSubtree);
        }
    }

    // P12/P15: гарантированный terminus Active→Finished. Штатно Finished выставляет ExitedMessage,
    // но exited приходит лишь со смертью прогона — а прогон доживает фоновых агентов (до
    // BgLingerTimeout, 30 мин) либо вовсе не выходит (висящий без работы процесс, подавленный
    // SuppressExited). Без sweep чат висел active неопределённо долго (формы а/б/в).
    // Условие terminus: ход завершён (LastTurnEndedAt по result→Active) И grace истёк И НЕТ живой
    // фоновой работы — процесс мёртв (!HasLiveTurn) либо доживает впустую без единой фоновой задачи
    // (HasLiveTurn && !HasPendingBg). Живой Workflow (HasPendingBg) НЕ трогаем: Finished при живой
    // работе хуже вечного active — синтез панели прилетел бы в «завершённый» чат. Планировщик
    // штаба (TeamPlanningInFlight, до 300 с без CLI-прогона) и активная итерация цикла
    // (LoopTurnInFlight) гейтятся отдельно — фоновые режимы между ходами, не держащие прогон.
    // ApplyStatusAsync запускаем через Task.Run ВНЕ _saveLock: это асинхронный бродкаст с
    // повторной записью стора — держать его под локом пути сохранения незачем (сам
    // System.Threading.Lock реентерабелен, вложенный SaveSessions не клинил бы, но await
    // под локом невозможен, а удлинять критическую секцию sweep'а нет смысла). Порядок
    // локов _saveLock→PendingLock консистентен, обратного нигде нет.
    private void TrySweepStuckActive(SessionEntry entry, HashSet<string> protectedByLiveSubtree)
    {
        if (entry.Info.Status != SessionStatus.Active) return;
        // Фоновые режимы без прогона между ходами: HasLiveTurn=false, но работа идёт, Finished был
        // бы ложным («Finished при живой работе хуже вечного active»). Планировщик штаба живёт до
        // 300 с без CLI-прогона. Цикл «до готово» между итерациями: после result хода LoopTurnInFlight
        // уже сброшен, статус Active, метка свежая — но цикл ещё включён (WorkLoop != null) и вот-вот
        // поднимет следующую итерацию; пауза при ретрае/фолбэке/тормозах провайдера может превысить
        // grace, и ложный Finished мигнул бы «завершено» посреди цикла. Поэтому гейт — по включённому
        // циклу, а не по маркеру итерации: как только SetWorkLoopAsync обнулил WorkLoop (форма в),
        // сессия становится обычным кандидатом, и sweep её закрывает.
        if (_teamNotifier.IsSessionBusy(entry.Info.Id)) return;
        if (entry.Info.WorkLoop is not null) return;

        // P28: в поддереве этой сессии есть живой исполнитель (прямой потомок или глубже) — sweep
        // не имеет права мигать Finished в разгар волны. Гейт по РЕАЛЬНОЙ живости потомка (набор
        // protectedByLiveSubtree собран в SaveSessions рекурсивно через HasLiveWork), а не по статусу
        // задачи/сессии: зависший исполнитель (мёртвый процесс в Waiting/Working, дефект P30) в набор
        // не попадает и предка не держит — иначе клинч хуже минутного ложного Finished.
        if (protectedByLiveSubtree.Contains(entry.Info.Id)) return;

        // Живая собственная работа (фон/продолжение) — оценка в HasLiveWork, единая точка истины
        // о живости (тот же предикат, что отсеивает живые узлы поддерева выше). Для Active-кандидата
        // это «процесс жив и есть фоновая работа или идёт/готово продолжение» (HasPendingBg — панель
        // экспертов/Workflow, IsContinuationInFlight — ход-ответ на task_notification bg-агента), иначе
        // terminus: процесс мёртв либо доживает впустую, и после ContinuationStartGrace ридер его убьёт.
        if (HasLiveWork(entry)) return;

        // Захват решения под PendingLock — повторный sweep или OnMessageAsync (новый ход) увидят сброс
        // и не запустят дубль. LastTurnEndedAt читается/пишется ТОЛЬКО под PendingLock (контракт поля,
        // см. объявление): DateTimeOffset? неатомарен, чтение вне лока даёт порванное значение
        // (HasValue=true, устаревшие ticks). Перепроверка статуса — гонка с параллельным сменщиком.
        lock (entry.PendingLock)
        {
            if (entry.Info.Status != SessionStatus.Active) return;
            if (entry.LastTurnEndedAt is not { } endedAt) return;
            if ((DateTimeOffset.UtcNow - endedAt).TotalSeconds < _stuckActiveGraceSeconds) return;
            entry.LastTurnEndedAt = null;
        }

        var sid = entry.Info.Id;
        var adapter = entry.Process as ILlmSessionAdapter;
        // Sweep-терминус — штатное завершение хода через grace (P28): не авария, лог не должен
        // подсвечивать её как Warning. На Information она и остаётся в обычном прогоне.
        _log.LogInformation(
            "[SessionManager] Sweep terminus: Active→Finished после result хода ({Sid}: exited прогона не было, alive={Alive}, bg={Bg}, cont={Cont})",
            sid, adapter is { HasLiveTurn: true }, adapter is { HasPendingBg: true }, adapter is { IsContinuationInFlight: true });
        _ = Task.Run(async () =>
        {
            // Доводка: содержимого за ней нет — прочитанный чат не должен стать непрочитанным
            try { await ApplyStatusAsync(sid, entry, SessionStatus.Finished, touchUpdatedAt: false); }
            catch (Exception ex) { _log.LogError(ex, "[SessionManager] Sweep ApplyStatus не удался ({Sid})", sid); }
        });
    }

    // «Сессия прямо сейчас ведёт живую работу» — единая оценка живости для sweep-terminus (гейт
    // собственного хода в TrySweepStuckActive) и для гейта «в поддереве есть живой исполнитель»
    // (P28, сбор protectedByLiveSubtree в SaveSessions). Одна точка истины — чтобы не плодить
    // расходящиеся формулы живости (следующий фиксер не гадал, какая из них верная).
    //
    // Живость ПРОЦЕССА (HasLiveTurn) обязательна при ЛЮБОМ статусе: мёртвый исполнитель не
    // считается работающим ни в Waiting (открытый дефект P30 — статус залипает у мёртвого процесса),
    // ни в Working — иначе он держал бы предков вечно (клинч P28). Active — особый случай: ход уже
    // отдан (result), процесс доживает, и жив лишь пока есть фоновая работа (HasPendingBg) или идёт/
    // готово ход-продолжение (IsContinuationInFlight). Starting/Working/Waiting — ход идёт, процесс
    // поднимается или стоит на permission-запросе: живы при живом процессе. Finished/Error/Orphaned
    // мертвы.
    private static bool HasLiveWork(SessionEntry entry)
    {
        if (entry.Process is not ILlmSessionAdapter adapter) return false;
        if (!adapter.HasLiveTurn) return false;
        return entry.Info.Status switch
        {
            SessionStatus.Active => adapter.HasPendingBg || adapter.IsContinuationInFlight,
            SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting => true,
            _ => false,
        };
    }

    // Источник делегирования «порождён этим прогоном» для sweep-гейта P28: SourceSessionId задачи
    // сессии (резолв lock-free через ConcurrentDictionary в TaskManager). null — обычный чат без
    // задачи либо задача удалена (корень иерархии). Не ParentSessionId: тот учитывает ручную
    // группировку (override/detach), а нас интересует именно «порождён прогоном штаба».
    private string? ParentSourceSession(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        return entry.Info.TaskId is { } tid
            ? _taskLookup?.GetById(tid)?.SourceSessionId
            : null;
    }

    // Эффективный родительский чат сессии (SessionTaskLinks): ручная группировка, иначе чат,
    // в котором была создана её задача. Единственный вход для вертикалей, у которых нет
    // собственного TaskManager (TeamTurnCompletionService/TeamBudgetService), а есть SessionManager.
    internal string? EffectiveParentSessionId(Session s) => SessionTaskLinks.ParentSessionId(s, _taskLookup);

    // Только для тестов: запустить sweep-terminus (P12/P15) вне обычных триггеров SaveSessions,
    // чтобы детерминированно проверить переход Active→Finished по истечению grace. Прод-код
    // триггерит sweep каждым SaveSessions (включая автосохранение) — этого API тестам не нужно.
    internal void RunStuckActiveSweepForTests() => SaveSessions();

    // --- Публичное API ---

    public IReadOnlyCollection<Session> GetByProject(string projectId) =>
        _sessions.Values
            .Where(e => e.Info.ProjectId == projectId)
            .Select(e => e.Info)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

    // Сессии владельца с ЖИВЫМИ фоновыми агентами — снимок для холодного старта списка чатов
    // (событие bg_agents_presence клиент мог пропустить, пока не вошёл в группы). Состояния
    // не держим: источник истины — сам прогон, HasPendingBg берёт свой лок на мгновение.
    // Сначала фильтр по фону (живой фон — редкость), потом резолв владельца.
    public IReadOnlyList<string> GetSessionsWithLiveAgents(string ownerId) =>
        _sessions.Values
            .Where(e => e.Process is { HasTrackedBg: true } && ResolveOwnerId(e.Info) == ownerId)
            .Select(e => e.Info.Id)
            .ToList();

    // Та же выборка для фоновых КОМАНД (Bash с run_in_background): у них свой, тихий значок в
    // списке чатов. Отдельный метод, а не флаг в предыдущем: наборы пересекаются (в чате может
    // идти и агент, и дев-сервер), и клиенту нужны оба списка целиком.
    public IReadOnlyList<string> GetSessionsWithLiveBgCommands(string ownerId) =>
        _sessions.Values
            .Where(e => e.Process is { HasTrackedCommandBg: true } && ResolveOwnerId(e.Info) == ownerId)
            .Select(e => e.Info.Id)
            .ToList();

    // Чаты, подпадающие под автоправило архивации «без сообщений дольше N дней» (план v4,
    // флаг chat-auto-archive) — ЕДИНАЯ точка отбора для счётчика превью
    // (GET /api/chats/archive-preview) и тика правила (ChatArchiveService.TickAsync):
    // превью обязано считать той же функцией, что архивирует, иначе счётчик покажет «3»,
    // а исчезнет 200 (пре-мортем №2). nowUtc — параметром, чтобы превью и тик сходились
    // при одном моменте времени; фронт этот отбор повторить не может в принципе
    // (HasTurnInFlight и живость агентов — серверные).
    //
    // projectId != null — чаты проекта (владение проектом контроллер проверил отдельно,
    // у проектных сессий OwnerId null); null — чаты вне проекта владельца ownerId
    // (личный дефолт правила). Потолок пачки и ArchivedBy="rule" — забота тика, не отбора.
    public IReadOnlyList<Session> GetArchiveRuleCandidates(string ownerId, string? projectId, int days, DateTime nowUtc)
    {
        var cutoff = nowUtc - TimeSpan.FromDays(days);
        var result = new List<Session>();
        foreach (var entry in _sessions.Values)
        {
            var info = entry.Info;
            if (projectId is null)
            {
                if (info.ProjectId is not null || info.OwnerId != ownerId) continue;
            }
            else if (info.ProjectId != projectId) continue;
            if (!MatchesArchiveRule(info, _taskLookup, cutoff)) continue;
            // Живость в чистый предикат не входит: она — свойство entry/адаптера, не Session
            if (HasTurnInFlight(entry) || entry.Process is { HasTrackedBg: true }) continue;
            result.Add(info);
        }
        return result;
    }

    // Чистая часть предиката правила (порог + исключения; живость хода/фоновых агентов —
    // в GetArchiveRuleCandidates). internal static для юнит-тестов, образец — ShouldExpire
    // в ChatExpiryService. Исключения ровно три: закреплённые («чат нужен»), временные
    // (ими управляет свой срок) и чат живой задачи-исполнителя (выполненной — можно).
    //
    // Исключения по онбордингу (OnboardingKind) и по штабу в работе (TeamImplement не в
    // Idle) сняты 28.08.2026: они были бессрочными, и брошенное знакомство или штаб,
    // остывший полгода назад, не уходили в архив никогда — мусор копился (12 «вечных»
    // чатов у владельца на момент решения). Проверки активности в них не было, а сам порог
    // «без активности N дней» её и означает; архив при этом ничего не удаляет, и первая же
    // запись в чат возвращает его из архива автоматически (IsArchived — производный от
    // UpdatedAt <= ArchivedAt).
    internal static bool MatchesArchiveRule(Session s, ITaskLookup? tasks, DateTime cutoff) =>
        !s.IsArchived
        && !s.IsPinned
        && s.ExpiresAfterMinutes is null
        && s.UpdatedAt <= cutoff
        && (s.TaskId is null || SessionTaskLinks.IsTaskDone(s, tasks));

    // Число сессий проекта — для карточки проекта (без аллокации списка)
    public int CountByProject(string projectId) =>
        _sessions.Values.Count(e => e.Info.ProjectId == projectId);

    /// <summary>Всего зарегистрированных сессий (для OTel gauge). ConcurrentDictionary.Count — thread-safe, sub-ms.</summary>
    public int ActiveCount => _sessions.Count;

    // Чаты вне проекта, принадлежащие пользователю (для вкладки «Чаты»)
    public IReadOnlyCollection<Session> GetProjectlessChats(string ownerId) =>
        _sessions.Values
            .Where(e => e.Info.ProjectId == null && e.Info.OwnerId == ownerId)
            .Select(e => e.Info)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

    // Закрепить/открепить чат
    public bool SetPinned(string sessionId, bool pinned)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        entry.Info.IsPinned = pinned;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        return true;
    }

    // Ручная группировка чатов (drag-and-drop в списке): назначить родителя либо вынести
    // в корень (parentId == null). Единственная точка записи ParentOverrideId/ParentDetached —
    // поля взаимоисключающие, снаружи их не трогает никто.
    //
    // Чат не «переезжает»: UpdatedAt намеренно НЕ обновляется. Корни сортируются по активности
    // поддерева (chatTree.ts), и отметка времени от перетаскивания перекидывала бы чат наверх
    // списка, будто в нём был ход.
    public Session? SetParent(string sessionId, string? parentId, string ownerId)
    {
        if (GetOwned(sessionId, ownerId) is not { } chat) return null;
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;

        if (parentId is null)
        {
            entry.Info.ParentOverrideId = null;
            // Гасим авто-связь только у чата, у которого она есть, — иначе обычный чат
            // навсегда унёс бы бессмысленный флаг в sessions.json.
            entry.Info.ParentDetached = chat.TaskId is not null;
            SaveSessions();
            return entry.Info;
        }

        if (parentId == sessionId)
            throw new InvalidOperationException("Чат нельзя вложить в самого себя");
        if (GetOwned(parentId, ownerId) is not { } parent)
            throw new InvalidOperationException("Родительский чат не найден");
        // Разные списки рендерятся на разных экранах: ребёнок из чужого скоупа не нашёл бы
        // родителя в своей выборке и молча всплыл бы в корень (chatTree.ts: byId.has(pid)).
        if (parent.ProjectId != chat.ProjectId)
            throw new InvalidOperationException(
                "Чат можно группировать только внутри одного проекта или списка чатов вне проектов");
        if (IsDescendantOf(parent.Id, sessionId))
            throw new InvalidOperationException("Нельзя вложить чат в его собственный дочерний чат");

        entry.Info.ParentOverrideId = parentId;
        entry.Info.ParentDetached = false;
        SaveSessions();
        return entry.Info;
    }

    // Является ли candidate потомком ancestor по эффективной иерархии (ParentSessionId,
    // т.е. с учётом ручных override). Счётчик шагов — страховка от цикла, уже лежащего
    // в данных: инварианты SetParent новых циклов не создают, но старый sessions.json
    // мог приехать из бэкапа, а обход вверх по кольцу здесь бы завис.
    private bool IsDescendantOf(string candidateId, string ancestorId)
    {
        var cur = GetById(candidateId);
        for (var steps = 0; cur is not null && steps < 256; steps++)
        {
            if (SessionTaskLinks.ParentSessionId(cur, _taskLookup) is not { } pid) return false;
            if (pid == ancestorId) return true;
            cur = GetById(pid);
        }
        return false;
    }

    // Убрать чат в архив (archived = true) / вернуть (false). Единственная точка записи
    // полей архива — как SetParent для группировки. by — "user" (пункт меню) | "rule"
    // (автоправило), batchId — идентификатор прохода правила (откат возвращает ровно одну
    // пачку; у ручной архивации null).
    //
    // UpdatedAt/LastReadAt намеренно НЕ трогает (как SetExpiry): по ним сортируется список
    // и считается непрочитанность, а архивация — не активность; возврат не должен всплывать
    // чат наверх и метить его непрочитанным. Признак архива производный (Session.IsArchived),
    // поэтому «снять архив» — это сброс полей, и повторная активность снимет его и без
    // мутатора. Попутно копируем транскрипт в data/archived-transcripts (архивация) или
    // возвращаем его в профиль (возврат) — best-effort, сбой файловой части не роняет вызов.
    //
    // SaveSessions сознательно НЕ зовём: ручная архивация сохранит стор сразу после вызова,
    // а проход автоправила пишет файл ОДИН раз на всю пачку (до 200 чатов за тик — иначе
    // каждая архивация перезаписывала бы sessions.json целиком и дёргала sweep).
    public Session? SetArchived(string sessionId, bool archived, string by, string? batchId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (archived)
        {
            entry.Info.ArchivedAt = DateTime.UtcNow;
            entry.Info.ArchivedBy = by;
            entry.Info.ArchiveBatchId = batchId;
            ArchiveTranscriptCopy(entry.Info);
        }
        else
        {
            entry.Info.ArchivedAt = null;
            entry.Info.ArchivedBy = null;
            entry.Info.ArchiveBatchId = null;
            RestoreTranscriptCopy(entry.Info);
        }
        return entry.Info;
    }

    // Копия транскрипта при архивации: источники — ВСЕ корни профилей, как у уборки при
    // удалении (DeleteTranscript): за время жизни чат мог мигрировать между профилями и
    // рабочими папками, а миграции исходники не удаляют. Сам стор валидирует csid белым
    // списком и гейтит десктопные чаты; best-effort — сбой не имеет права ронять архивацию.
    private void ArchiveTranscriptCopy(Session info)
    {
        try
        {
            if (info.ClaudeSessionId is not string csid) return;
            // Локальный проект: транскрипт на устройстве, копировать с диска сервера нечего —
            // поиск по своему пути нашёл бы разве что чужой файл
            if (!TranscriptOnServer(info)) return;
            _archivedTranscripts.Archive(csid, info.DesktopChat, TranscriptSearchRoots(info), TryResolveCwd(info));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Копия транскрипта при архивации чата {SessionId} не создана", info.Id);
        }
    }

    // Возврат копии при возврате чата: цель резолвится НА МОМЕНТ возврата — за время в
    // архиве могли смениться профиль провайдера (MigrateProviderAsync) и папка уплощённого
    // cwd (worktree, правка RootPath), поэтому исходный путь не запоминаем.
    private void RestoreTranscriptCopy(Session info)
    {
        try
        {
            if (info.ClaudeSessionId is not string csid) return;
            // Локальный проект копию при архивации не делал — возвращать нечего
            if (!TranscriptOnServer(info)) return;
            var hostCwd = TryResolveCwd(info);
            if (hostCwd is null) return;
            var ownerId = ResolveOwnerId(info);
            _archivedTranscripts.Restore(csid,
                ConfigRootFor(ownerId, info.Provider), CwdForOwner(ownerId, hostCwd));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Копия транскрипта при возврате чата {SessionId} не возвращена", info.Id);
        }
    }

    // Корни профилей CLI для поиска транскрипта чата: все конфиг-корни провайдеров плюс
    // профили песочницы владельца (раскладка RewriteProfileEnv; берём ВСЕ папки владельца,
    // а не один ключ — чат мог мигрировать). Выделено из DeleteTranscript: тот же список
    // нужен и копии при архивации.
    private IEnumerable<string> TranscriptSearchRoots(Session info)
    {
        var roots = new List<string>(_llmProviders.GetAllConfigRoots());
        if (ResolveOwnerId(info) is string ownerId)
        {
            var ownerProfiles = Path.Combine(_sandbox.ProfilesHostDir, ownerId);
            if (Directory.Exists(ownerProfiles))
                roots.AddRange(Directory.GetDirectories(ownerProfiles));
        }
        return roots;
    }

    // Ручная архивация/возврат из архива (PUT /api/chats/{id}/archived, шаг 2 плана
    // «Архив чатов»): обёртка над SetArchived для точки входа из UI — гейт живости,
    // снятие срока временного чата, персист и событие ленты chat_archived. Автоправило
    // (ChatArchiveService) зовёт SetArchived напрямую: у него свой гейт отбора и одна
    // SaveSessions() на всю пачку.
    //
    // Гейт живости — только на архивацию: возврат ничего не рвёт, а «вернуть из архива,
    // пока идёт ход» — ровно то, что происходит при снятии архива активностью. 409 на
    // живом ходе отсюда уезжает InvalidOperationException (конвенция RestartWave и др.).
    public async Task<Session?> SetArchivedAsync(string sessionId, string ownerId, bool archived)
    {
        if (GetOwned(sessionId, ownerId) is not { } info) return null;
        if (archived && _sessions.TryGetValue(sessionId, out var entry))
        {
            if (HasTurnInFlight(entry))
                throw new InvalidOperationException(
                    "В чате идёт ход — дождитесь его завершения или прервите его, затем уберите чат в архив");
            if (entry.Process is { HasTrackedBg: true })
                throw new InvalidOperationException(
                    "В чате работают фоновые агенты — дождитесь их завершения, затем уберите чат в архив");
            // Временный чат: архив бессрочен до возврата, срок снимаем ДО записи признака
            // архива — иначе чат умер бы по таймеру ChatExpiryService уже в архиве
            // (SetExpiry заодно обнуляет ExpiryAnchor). SetExpiry пишет стор сам —
            // промежуточное состояние «срок снят, архива нет» безопасно.
            if (info.ExpiresAfterMinutes is not null) SetExpiry(sessionId, null);
        }
        var updated = SetArchived(sessionId, archived, by: "user");
        if (updated is null) return null;
        SaveSessions();
        await BroadcastChatArchivedAsync(sessionId, updated, archived);
        return updated;
    }

    // Уведомить клиентов об архивации/возврате чата (адресация как у BroadcastChatDeletedAsync):
    // project-группа для проектной сессии, user-группа для чата вне проекта
    private async Task BroadcastChatArchivedAsync(string sessionId, Session info, bool archived)
    {
        var msg = new ChatArchivedMessage(archived) with { SessionId = sessionId };
        var tasks = new List<Task> { _broadcaster.ToSession(sessionId, msg) };
        if (info.ProjectId is string pid)
            tasks.Add(_broadcaster.ToProject(pid, msg));
        else if (info.OwnerId is string oid)
            tasks.Add(_broadcaster.ToOwner(oid, msg));
        await Task.WhenAll(tasks);
    }

    // Проход автоправила архивации (ChatArchiveService, шаг 6 плана v4): заархивировать
    // пачку одним batchId — ОДНОЙ SaveSessions на весь проход (до 200 чатов; иначе каждая
    // архивация переписывала бы sessions.json целиком и дёргала sweep) и событием
    // chat_archived каждому чату. Гейт живости не повторяем: отбор кандидатов
    // (GetArchiveRuleCandidates) уже его отработал; кому-то стать живым между отбором и
    // записью — допустимая гонка, чат вернёт из архива собственная активность.
    public async Task ArchiveBatchAsync(IReadOnlyCollection<string> sessionIds, string batchId)
    {
        var archived = new List<(string Id, Session Info)>();
        foreach (var id in sessionIds)
        {
            var updated = SetArchived(id, archived: true, by: "rule", batchId);
            if (updated is not null) archived.Add((id, updated));
        }
        if (archived.Count == 0) return;
        SaveSessions();
        foreach (var (id, info) in archived)
            await BroadcastChatArchivedAsync(id, info, archived: true);
    }

    // Откат пачки автоправила из уведомления/раздела «Архив»: вернуть РОВНО чаты прохода
    // batchId (ArchivedBy="rule" и чат ещё в архиве), а не всю историю правила. Одна
    // SaveSessions на пачку, событие возврата каждому. Владелец — по GetOwned: батч-id
    // приходит из URL, чужой не должен возвращать чужие чаты (даже угаданный).
    public async Task<int> RestoreArchiveBatchAsync(string ownerId, string batchId)
    {
        var restored = new List<(string Id, Session Info)>();
        foreach (var s in _sessions.Values.Select(e => e.Info)
                     .Where(s => s.ArchiveBatchId == batchId && s.ArchivedBy == "rule" && s.IsArchived)
                     .ToList())
        {
            if (GetOwned(s.Id, ownerId) is null) continue;
            var updated = SetArchived(s.Id, archived: false, by: "user");
            if (updated is not null) restored.Add((s.Id, updated));
        }
        if (restored.Count == 0) return 0;
        SaveSessions();
        foreach (var (id, info) in restored)
            await BroadcastChatArchivedAsync(id, info, archived: false);
        return restored.Count;
    }

    // Включить/выключить временность чата: minutes > 0 — авто-удаление через N минут
    // после последней активности, null — обычный чат.
    //
    // UpdatedAt намеренно НЕ обновляется (как в SetParent): по нему сортируется список и
    // считается непрочитанность, а смена настройки хранения — не активность чата. Отсчёт
    // срока при этом не должен стартовать в прошлом, поэтому включение ставит ExpiryAnchor —
    // дедлайн считается от него, если он позже последней активности.
    public Session? SetExpiry(string sessionId, int? minutes)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.ExpiresAfterMinutes = minutes;
        entry.Info.ExpiryAnchor = minutes is null ? null : DateTime.UtcNow;
        SaveSessions();
        return entry.Info;
    }

    // Заглушить/включить уведомления по чату (браузерные «нужно решение» / «ход завершён»).
    // UpdatedAt не трогаем по той же причине, что в SetExpiry: это настройка, а не активность.
    public Session? SetNotificationsMuted(string sessionId, bool muted)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.NotificationsMuted = muted;
        SaveSessions();
        return entry.Info;
    }

    // Включить/выключить голосовой режим чата и/или сменить стиль озвучки.
    // Один сеттер на оба поля намеренно: каждый дёргает SaveSessions(), то есть перезапись
    // всего списка — два раздельных дали бы две записи файла на один PUT.
    // null-аргумент = поле не трогаем: стиль приезжает и БЕЗ флага (устройство выправляет
    // чужой стиль у чата с уже включённой озвучкой), а такой запрос не должен её гасить.
    // UpdatedAt не трогаем по той же причине, что в SetExpiry: это настройка, а не активность.
    public Session? SetVoiceMode(string sessionId, bool? on, string? style = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (on is bool value) entry.Info.VoiceMode = value;
        if (style is not null) entry.Info.VoiceStyle = VoiceStyles.Normalize(style);
        SaveSessions();
        return entry.Info;
    }

    // Отметить чат прочитанным (синк непрочитанности между устройствами).
    // UpdatedAt намеренно не трогаем (как в SetExpiry): прочтение — не активность чата.
    // false — сессии нет или она не принадлежит owner'у (контроллер отдаёт 404).
    // Идемпотентность: LastReadAt уже >= UpdatedAt → true без перезаписи файла
    // (SaveSessions пишет весь список — незачем гонять диск на повторных отметках).
    public bool MarkRead(string sessionId, string ownerId)
    {
        if (GetOwned(sessionId, ownerId) is not { } s) return false;
        if (s.LastReadAt >= s.UpdatedAt) return true;
        s.LastReadAt = DateTime.UtcNow;
        SaveSessions();
        return true;
    }

    // Opt-out «Истории решений» (ADR-004 §6): тумблер «Не сохранять решения из этого чата».
    // Персистится в sessions.json; DossierCaptureService проверяет его при захвате коммита.
    public Session? SetExcludeFromDossiers(string sessionId, bool value)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.ExcludeFromDossiers = value;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        return entry.Info;
    }

    // Сменить состав контекста чата (фича chat-context): идемпотентная замена списка
    // материалов целиком — «добавить материал» это PUT со старым составом + новая запись.
    // Валидацию записей делает контроллер ДО этого вызова; сюда приезжают уже чистые
    // данные. UpdatedAt не трогаем по той же причине, что в SetExpiry: контекст —
    // настройка чата, а не активность (по UpdatedAt идут сортировка и непрочитанность).
    // Broadcast — в session/project/user-группы (BroadcastSessionMessageAsync): событие
    // внеходовое, вторая вкладка браузера должна увидеть состав без перезагрузки.
    public async Task<Session?> SetContextAsync(string sessionId, IReadOnlyList<SessionContextEntry> entries)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.Context = [.. entries];
        SaveSessions();
        await BroadcastSessionMessageAsync(sessionId, new ContextUpdatedMessage(entry.Info.Context));
        return entry.Info;
    }

    // Контекст чата, принадлежащего пользователю: для GET-эндпоинта и (вслед за ним)
    // тула context_list. null — чужая/несуществующая сессия.
    public IReadOnlyList<SessionContextEntry>? GetContext(string sessionId, string ownerId) =>
        GetOwned(sessionId, ownerId) is { } s ? s.Context : null;

    // Все сессии (для планировщика авто-удаления временных чатов)
    public IReadOnlyCollection<Session> GetAll() =>
        _sessions.Values.Select(e => e.Info).ToList();

    // Разовая переадресация закреплённых моделей (миграция каталога провайдера): id из карты
    // заменяется, всё остальное — включая незнакомые модели и «preset:{id}» — остаётся как есть.
    // Возвращает число изменённых чатов; 0 — стор на диск не переписывается.
    // Идёт через живой реестр, а не файл: иначе первый же SaveSessions вернул бы старые id.
    public int RemapModels(IReadOnlyDictionary<string, string> map)
    {
        var changed = 0;
        foreach (var info in _sessions.Values.Select(e => e.Info))
        {
            if (info.Model is null || !map.TryGetValue(info.Model.Trim(), out var next)) continue;
            info.Model = next;
            changed++;
        }
        if (changed > 0) SaveSessions();
        return changed;
    }

    // Рабочая папка чата, принадлежащего пользователю (для загрузки вложений): у чата вне
    // проекта — {дом}/Chats, у проектного — рабочая папка сессии (worktree, иначе корень
    // проекта), чтобы Claude нашёл вложение по относительному пути из своего cwd.
    // null — чужая/несуществующая сессия либо папку определить не удалось.
    public string? GetChatRoot(string sessionId, string ownerId)
    {
        var s = GetById(sessionId);
        if (s is null || ResolveOwnerId(s) != ownerId) return null;
        if (s.ProjectId is null) return ResolveChatRoot(ownerId);
        var root = _projects.GetById(s.ProjectId)?.RootPath;
        return root is null ? null : EffectiveRoot(s, root);
    }

    // Рабочая папка чата вне проекта: {домашняя папка владельца}/Chats (создаётся при отсутствии)

    // Выбрать подписку Claude для новой сессии: если модель Claude (не сторонний провайдер),
    // выбираем из пула подписок (least-loaded из способных обслужить модель — пин Opus
    // не должен попасть на аккаунт без Opus); для сторонних — по модели.
    private string ResolveSubscriptionProvider(string? model)
    {
        var provider = _llmProviders.ResolveByModel(model);
        if (provider is not null)
            return provider.Key;
        return _subscriptionPool.Pick(model);
    }

    // Модель места по назначению (слоты «сильная/средняя/слабая» + таблица назначений + per-user слоты).
    // Подставляется, только когда модель НЕ задана явно и это НЕ resume: у транскрипта
    // resumed-сессии уже зафиксированы своя модель и провайдер, и подмена здесь сменила бы
    // провайдер и упёрлась в guard смены провайдера (400).
    private string? ResolveDefaultModel(string usageKey, string? model, string? resumeSessionId, string? ownerId) =>
        !string.IsNullOrEmpty(resumeSessionId) ? model : _assignments.Resolve(usageKey, model, ownerId);

    // Место применения по признакам сессии — тот же порядок, что у ClaudeSession.UsageKey:
    // исполнитель задач специфичнее персоны, персона специфичнее обычного чата.
    private static string UsageKeyFor(bool taskExecution, string? taskId, string? personaId) =>
        taskExecution || taskId is not null ? Llm.LocalActionCatalog.TasksExecutor
        : !string.IsNullOrWhiteSpace(personaId) ? Llm.LocalActionCatalog.ChatPersona
        : Llm.LocalActionCatalog.ChatNew;

    private string ResolveChatRoot(string ownerId)
    {
        var user = _users.GetById(ownerId)
            ?? throw new KeyNotFoundException($"Пользователь не найден: {ownerId}");
        // Container-пользователи живут в отдельном корне (Sandbox:ProjectsRoot):
        // только он монтируется в песочницу — данные local-пользователей туда не попадают
        var home = _homes.Resolve(user)
            ?? throw new InvalidOperationException(
                UserHomeResolver.NotConfiguredMessage(user.ExecutionEnvironment));
        var path = Path.Combine(home, "Chats");
        Directory.CreateDirectory(path);
        return path;
    }

    // Корень профиля CLI (CLAUDE_CONFIG_DIR) для ключа провайдера сессии: подписка пула
    // (включая "claude", если задана с токеном) — claude-profiles/sub-{key}; ключ CLI-провайдера
    // — claude-profiles/{key}; иначе (локальный Claude — пул пуст, или provider не задан) —
    // пользовательский ~/.claude без оверрайда. Зеркалит выбор env хода в ClaudeSession
    // (BuildOAuthCliEnv / BuildCliEnv). ХОСТОВАЯ раскладка: у container-пользователя профиль
    // переписывается на песочный, поэтому ходить сюда напрямую нельзя — только через
    // ConfigRootFor(ownerId, key), который знает обе раскладки.
    private string ConfigRootForProvider(string? providerKey)
    {
        if (string.IsNullOrEmpty(providerKey))
            return _llmProviders.UserProfileDir;
        if (_llmProviders.GetByKey(providerKey) is not null)
            return _llmProviders.GetProfileDir(providerKey);
        if (_subscriptionPool.All.Any(s => s.Key == providerKey && s.Enabled))
            return _llmProviders.GetProfileDir("sub-" + providerKey);
        return _llmProviders.UserProfileDir;
    }

    // Корень профиля CLI С УЧЁТОМ СРЕДЫ владельца. У container-пользователя ход идёт в
    // песочнице, и DockerProcessRunner.RewriteProfileEnv подменяет профиль на
    // {ProfilesHostDir}/{ownerId}/{ключ}, где ключ — имя папки хостового профиля, а «без
    // оверрайда» (~/.claude) → "default". Ключ выводится именно из имени папки (та же
    // операция, что в RewriteProfileEnv), а не из providerKey: ConfigRootForProvider("claude")
    // отдаёт sub-claude, если запись «claude» задана в пуле с токеном, и наивный маппинг
    // «primary → default» разошёлся бы с реальной раскладкой хода.
    private string ConfigRootFor(string? ownerId, string? providerKey)
    {
        var hostRoot = ConfigRootForProvider(providerKey);
        if (ownerId is null || _users.GetById(ownerId)?.ExecutionEnvironment != ExecutionEnvironments.Container)
            return hostRoot;
        var key = string.Equals(Path.GetFullPath(hostRoot), Path.GetFullPath(_llmProviders.UserProfileDir),
                StringComparison.OrdinalIgnoreCase)
            ? "default"
            : Path.GetFileName(hostRoot.TrimEnd('\\', '/'));
        return Path.Combine(_sandbox.ProfilesHostDir, ownerId, key);
    }

    // Рабочая папка ГЛАЗАМИ CLI: у container-пользователя процесс видит контейнерный путь
    // (/projects/…), по нему же CLI уплощает имя папки транскрипта. Путь вне монтирований
    // песочницы ToRuntime отвергает исключением (аналог SafeJoin) — вызывающий решает сам,
    // отдать его наружу (явная миграция → 400) или деградировать тихо (авто-фейловер).
    private string CwdForOwner(string? ownerId, string hostCwd)
    {
        var launcher = _launchers.ForOwner(ownerId);
        return launcher.IsSandboxed ? launcher.Paths.ToRuntime(hostCwd) : hostCwd;
    }

    // Подписчики шины событий хода (ADR-013, этап 1): вместо прямых полей sinks в
// LlmSessionContext каждый наблюдатель подписан на событие своего типа. Контракт тот же
// (запись в стор + side-effects на сессии), но заводится ОДИН раз на инстанс SessionManager
// — без привязки к конкретной сессии, потому что id сессии едет В СОБЫТИИ (TurnContext).
//
// Состав:
//
//   PromptAssembled     → PromptSnapshotStore (запись черновика / дозапись tools/mcp)
//                         + McpStatusStore (статусы MCP из system/init, попутно с AttachCliLayer).
//   SubagentRunCompleted → SubagentRunLog (запись паспорта) + side-effects на сессии
//                         (TruncatedSubagent/TruncatedBgNote, сброс счётчика добиваний).
//
//   TurnCompleted       → TurnRunLog (запись паспорта хода; в finally фолбэк-цикла больше
//                         НЕТ прямого вызова Record — это и есть тот самый «ровно один
//                         источник», который задача фиксирует как инвариант).

// Приёмник паспортов сабагента через шину: сюда стекается тестовый код через рефлексию
// (раньше этим путём ходил настоящий sink для ватчера сабагентов; сейчас ватчер публикует
// subagent/completed сам, а этот метод — синхронный мост через шину для unit-тестов,
// которые проверяют side-effects на сессии непосредственно после emit). Синхронный мост
// здесь ОК: подписчик шины пишет в стор и взводит флаги, оба эти действия идемпотентны
// и не могут зациклиться. null — шины нет (тесты без SessionManager), ватчер сам бы
// отказался публиковать.
internal Action<Llm.SubagentRunPassport>? SubagentRunSinkFor(string sessionId)
{
    return passport =>
    {
        // Синхронный мост: тесты берут делегат через рефлексию и зовут его из тестового
        // метода, который потом немедленно проверяет side-effects на entry. В проде никто
        // этим методом не пользуется — ватчер публикует subagent/completed в свой ITurnEventBus.
        TurnEvents.PublishAsync(new SubagentRunCompleted(
            Turn: new TurnContext(SessionId: sessionId, OwnerId: null, TurnSeq: 0,
                AgentDepth: 0, ProjectId: null),
            Passport: passport)).GetAwaiter().GetResult();
    };
}

// Ошибочные ветки возвращают Task.CompletedTask: подписчик Notification не должен
//   TurnCompleted       → TurnRunLog (запись паспорта хода; в finally фолбэк-цикла больше
//                         НЕТ прямого вызова Record — это и есть тот самый «ровно один
//                         источник», который задача фиксирует как инвариант).

private Task HandlePromptAssembled(PromptAssembled e)
{
    var snap = e.Snapshot;
    if (snap is null) return Task.CompletedTask; // событие не наш — большинство подписчиков его не носят
    switch (snap.Phase)
    {
        case PromptSnapshotPhase.Draft:
            if (_promptSnapshots is null || snap.Draft is null) return Task.CompletedTask;
            // ЧЕРНОВИК идёт в стор с id, который ClaudeSession уже сгенерировал и положил
            // в payload — тот же id едет в UI-кнопку «какой промпт ушёл» через _onMessage.
            // Счётчик общий с PromptSnapshotStore.NewPublicId, и без записи в стор id
            // всё равно бесполезен.
            SafePromptSnapshotDraft(e.Turn.SessionId, snap.SnapshotId, snap.Draft);
            break;
        case PromptSnapshotPhase.Tools:
            if ((_promptSnapshots is null && _mcpStatus is null) || snap.SnapshotId is null) return Task.CompletedTask;
            SafePromptSnapshotAttach(e.Turn.SessionId, snap.SnapshotId,
                snap.ToolNames ?? [], snap.McpServers ?? []);
            break;
    }
    return Task.CompletedTask;
}

// Все ошибочные ветки возвращают Task.CompletedTask: подписчик Notification не должен
// бросать наружу — шина гасит исключения, но мы и сами не плодим трейс ради диагностики.
private void SafePromptSnapshotDraft(string sessionId, string? snapshotId, PromptSnapshotDraft draft)
{
    if (snapshotId is null) { _promptSnapshots?.Save(sessionId, draft); return; }
    try { _promptSnapshots?.Save(sessionId, snapshotId, draft); }
    catch (Exception ex) { Console.Error.WriteLine($"[SessionManager] Снимок промпта не записан: {ex.Message}"); }
}

private void SafePromptSnapshotAttach(string sessionId, string snapshotId,
    IReadOnlyList<string> tools, IReadOnlyList<McpServerInfo> servers)
{
    try
    {
        _promptSnapshots?.AttachCliLayer(sessionId, snapshotId, tools, servers);
        if (_mcpStatus is null || servers.Count == 0) return;
        if (_sessions.TryGetValue(sessionId, out var entry)
            && ResolveOwnerId(entry.Info) is { } ownerId)
            _mcpStatus.RecordFromInit(ownerId, sessionId, servers);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SessionManager] Дозапись снимка промпта не удалась: {ex.Message}");
    }
}

private Task HandleSubagentRunCompleted(SubagentRunCompleted e)
{
    var sessionId = e.Turn.SessionId;
    var passport = e.Passport;
    if (_subagentRuns is not null) _subagentRuns.Record(passport);
    if (!_sessions.TryGetValue(sessionId, out var entry)) return Task.CompletedTask;
    if (passport.Truncated && passport.FinishedBy != "interrupted")
    {
        entry.TruncatedSubagent = passport;
        // Фоновый агент: продукт ТОЛЬКО ЧТО объявил его результат готовым посреди хода
        // координатора (bg_agent_done), и координатор принял обрывок последней реплики
        // за итог. Ждать result здесь нельзя: ход координатора не заканчивается, а сам
        // фоновый агент часто дозавершается уже ПОСЛЕ конца хода — тогда отметку не
        // разбирает никто и чат стоит до сообщения человека (ровно то, что видно в логе:
        // у исполнителей задач добивание срабатывало, в обычном чате — ни разу).
        if (passport.FinishedInBackground) NoteTruncatedBgAgent(sessionId, entry, passport);
    }
    else
    {
        // Опровержение обрыва: сигнал bg_agent_done обгоняет дозапись финального отчёта
        // в транскрипт, и пометка могла взвеститься по хвосту tool_use агента, который
        // на деле дописал end_turn. Штатный отчёт гасит ТОЛЬКО СВОЮ пометку — иначе
        // в чат уходит ложная директива добивания давно завершившегося агента, а чужая
        // пометка (другой AgentId) ждёт отчёта своего агента.
        if (RefutesTruncation(entry.TruncatedSubagent?.AgentId, passport.AgentId))
            entry.TruncatedSubagent = null;
        if (RefutesTruncation(entry.TruncatedBgNote?.AgentId, passport.AgentId))
            entry.TruncatedBgNote = null;
        // Агент, доложившийся штатно, снимает счётчик добиваний: потолок в две попытки —
        // на серию подряд, а не на всю жизнь чата. Но снимает ТОЛЬКО СВОЙ счётчик: в ходе
        // работают несколько агентов, и штатный отчёт соседа не значит, что оборвавшегося
        // добили — иначе потолок не достигается никогда (добивание уходит с attempt=1 по кругу).
        if (ResetsNudgeSeries(entry.NudgeAgentId, passport.AgentId))
        {
            entry.SubagentNudges = 0;
            entry.NudgeAgentId = null;
        }
    }
    return Task.CompletedTask;
}

private Task HandleTurnCompleted(TurnCompleted e)
{
    // Запись TurnRunLog — РОВНО ОДИН источник (CLAUDE.md, раздел LLM-провайдеры). До
    // переезда на шину источником был finally-блок FallbackLlmSessionAdapter; теперь
    // подписчик здесь, а finally публикует событие. Подписчик шины не должен бросать,
    // но try всё равно — запись НЕ бросает даже при сбое файла (см. TurnRunLog), try тут
    // для понятного журнала, если в сторе что-то поломается.
    if (e.Passport is null) return Task.CompletedTask;
    try { _turnRuns?.Record(e.Passport); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[SessionManager] Паспорт хода не записан ({e.Turn.SessionId}): {ex.Message}");
    }
    return Task.CompletedTask;
}

// Этап 4 / шаг 1в (боевой с 1в, идейно с 1б): подписчик turn/completed для штаба.
// Замещает прямой вызов HandleTeamTurnEndAsync из OnMessageAsync: изымает план
// (text/failed/asked) из LastTeamTurnEnds и асинхронно зовёт HandleTeamTurnEndAsync.
//
// Контракт фильтра по Outcome (сверка исходов живёт в коммите f049a593 и его переносе
// в docs/research/session-core-split-2026-09.md, §4, шаг 2в):
// - success | failed | egress_down | local_down — изымаем план и зовём штаб;
// - crashed — решает НЕ исход, а факт «терминал хода дошёл downstream», материализованный
//   планом в слоте (см. развилку ниже);
// - interrupted — штаб НЕ зовём, но восстанавливаем отсечки сторожа волн
//   (RestoreWaveWatchdogIfPaused) — здесь. На штатном ходе это no-op: HandleTeamTurnEndAsync
//   вернёт отсечки сам (см. 7593), вызов здесь идемпотентен.
// - cancelled — downstream ничего не получает (return до SettleAsync), сторож не трогаем.
// Если бы на interrupted сработал штаб, воспроизвёлся бы продовый дефект «фантомная
// эскалация»: обе точки прерывания чистят буфер маркеров (PreemptTurnForQueue, «Стоп»), но
// НЕ слот — пришедший следом терминал положил бы план с пустым текстом, и разбор поднял бы
// карточку молчаливого тупика поверх прерванного хода.
//
// Почему crashed разведён на два случая. Под одним исходом живут два разных пути финала
// в FallbackLlmSessionAdapter, и различает их не Outcome, а то, увидела ли лента конец хода:
// - сбой оркестрации ПОСЛЕ провальной попытки доставки → FailClosedAsync отдаёт downstream
//   ErrorMessage(ExpectResultFollows=true) + ResultMessage("error"). OnMessageAsync видит
//   терминал, осушает буфер маркеров и кладёт план — ход для человека состоялся, маркеры
//   обязаны быть разобраны (до перевода на подписку это делал прямой вызов из OnMessageAsync);
// - сбой ДО первой попытки (lastEnd=null, в hold'е пусто) → SettleAsync, downstream не
//   получает ничего, плана нет — разбирать нечего.
// Отсюда правило: на crashed пробуем ЗАБРАТЬ план и решаем по нему, а не по исходу. Отсутствие
// плана здесь — штатный случай (не WARN), в отличие от success/failed/egress_down/local_down,
// где терминал downstream гарантирован и его пропажа означает сбой проводки.
//
// Двойной терминал одного хода (ErrorMessage{ExpectResultFollows=true} + ResultMessage) в
// OnMessageAsync обе попытки кладут план по тому же TurnSeq; первая запись выигрывает
// (RecordTeamTurnEnd внутри ContainsKey → return), вторая — no-op. Шина публикует
// turn/completed строго ОДИН раз на ход, и подписчик забирает план ровно один раз —
// поэтому дедуп SkipNextTeamTurnEnd в OnMessageAsync ушёл вместе с переключением.
private Task HandleTeamTurnCompletedShim(TurnCompleted e) =>
        _teamTurnCompletion.HandleTeamTurnCompletedAsync(e);

    // Рабочая папка сессии: отдельное worktree чата приоритетнее корня проекта.
    // Единая точка подмены cwd — через неё идут обе funnel-точки LlmSessionContext.
    // internal — той же формулой DifyToolset резолвит дефолтный датасет проекта чата
    // (волна 4): расхождение формул означало бы разный состав tools/list и shape.
    internal static string EffectiveRoot(Session session, string fallbackRoot) =>
        session.WorktreePath ?? fallbackRoot;

    // Доступ к ChatHistoryService для вертикали Services.Team через шов: TeamStateService
    // читает историю неактивного чата в GetTeamPlanFromHistoryAsync, но идёт через этот
    // метод, а не через прямую ссылку на ChatHistoryService — иначе сторож границ краснеет
    // (ChatHistoryService живёт в корне Services, это «спинка», а не Team-вертикаль).
    // Тот же шаблон, что и у прочих internal-обёрток: минимум публичной поверхности при
    // максимуме гибкости реализации.
    internal async Task<TeamImplementPlan?> ReadStoredTeamPlanAsync(string claudeSessionId, string planId,
        bool onlyUnresolved = false)
    {
        if (claudeSessionId is null) return null;
        try
        {
            var stored = await _history.LoadAsync(claudeSessionId);
            return stored.OfType<StoredTeamPlanMessage>()
                .LastOrDefault(m => m.PlanId == planId && (!onlyUnresolved || !m.Resolved))?.Plan;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Чтение карточки плана {PlanId} с диска ({SessionId}) не удалось",
                planId, claudeSessionId);
            return null;
        }
    }

    // Корень, куда «Командная реализация» пишет файл полного плана (Э8-доп., 2026-08-02):
    // worktree штаба, если он в нём работает, иначе корень проекта. null — чат вне проекта,
    // писать план некуда (глобальный чат — раздел «Состав команды» продуктового плана).
    // Тело переехало в TeamStateService (волна А); обёртка снята (волна 3, шаг 2г-4):
    // TeamPlanService зовёт TeamStateService.ResolveTeamPlanRoot напрямую.
    internal string? ResolveTeamPlanRoot(Session session) => _teamState.ResolveTeamPlanRoot(session);

    // Уборка за удалённым деревом чата (ADR-003): снимаем watcher его файлов и выбрасываем
    // снимок графа из data/code-graphs — иначе он остался бы сиротой на диске, а watcher
    // держал бы handle на исчезнувшую папку. Best-effort: уборка не должна ронять удаление.
    private void ReleaseWorktreeGraph(string sessionId, string worktreePath)
    {
        try { _fileWatchers?.UnwatchPath("worktree:" + sessionId); } catch { /* уборка best-effort */ }
        try { _codeGraphs?.Invalidate(worktreePath); } catch { /* уборка best-effort */ }
    }

    // Рабочая папка сессии (для поиска транскрипта по уплощённому cwd);
    // null — папку определить не удалось (миграцию в этом случае не делаем)
    private string? TryResolveCwd(Session s)
    {
        try
        {
            if (s.ProjectId is not null)
            {
                var root = _projects.GetById(s.ProjectId)?.RootPath;
                return root is null ? null : EffectiveRoot(s, root);
            }
            return s.OwnerId is null ? null : ResolveChatRoot(s.OwnerId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Авто-фейловер пула подписок: аккаунт чата исчерпан, а в пуле есть здоровая
    // альтернатива — тихо перевозим транскрипт в её профиль и меняем Session.Provider.
    // Для пользователя незаметно: та же модель, тот же эндпоинт, та же предоплаченная
    // подписка. Сторонние провайдеры сюда не входят — другая модель/качество/оплата,
    // это только явная миграция (MigrateProviderAsync + карточка-предложение).
    private void TryPoolFailover(string sessionId, SessionEntry entry)
    {
        if (_llmProviders.ResolveByModel(entry.Info.Model) is not null) return; // сторонний — не пул
        if (!_subscriptionPool.HasExtra) return;
        var current = entry.Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey;
        if (!_subscriptionPool.IsExhausted(current)) return;
        var pick = _subscriptionPool.Pick(entry.Info.Model);
        if (pick == current || _subscriptionPool.IsExhausted(pick)) return; // переключаться некуда

        var ownerId = ResolveOwnerId(entry.Info);
        // Локальный проект: транскрипт на устройстве, профиль CLI там один — переносить нечего
        if (entry.Info.ClaudeSessionId is not null && TranscriptOnServer(entry.Info))
        {
            var hostCwd = TryResolveCwd(entry.Info);
            if (hostCwd is null) return;
            // Транскрипт container-пользователя лежит под КОНТЕЙНЕРНЫМ cwd в песочном
            // профиле. Путь вне монтирований (проект переехал наружу) — не повод ронять
            // ход: фейловер здесь и так деградирует тихо, чат просто ждёт сброса окна
            string cwd;
            try { cwd = CwdForOwner(ownerId, hostCwd); }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"[SessionManager] Фейловер пула отменён ({sessionId}): {ex.Message}");
                return;
            }
            if (!TranscriptMigrator.TryMigrate(ConfigRootFor(ownerId, current),
                    ConfigRootFor(ownerId, pick), cwd, entry.Info.ClaudeSessionId, out var error))
            {
                Console.Error.WriteLine($"[SessionManager] Фейловер пула отменён ({sessionId}): {error}");
                return;
            }
        }

        entry.Info.Provider = pick;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        Console.WriteLine($"[SessionManager] Чат {sessionId} переключён на подписку «{pick}» (лимит «{current}»)");
        FireAndForget(BroadcastAsync(sessionId, new ProviderSwitchedMessage(pick, Auto: true)),
            $"broadcast provider_switched ({sessionId})");
    }

    // Карточка «Продолжить на …»: чат родного Claude упёрся в лимит, внутри пула
    // автофейловер (TryPoolFailover) не переключил. Предлагаем варианты: здоровые
    // аккаунты ТОГО ЖЕ пула (пользователь выбирает сам — TryPoolFailover либо уже
    // проверил их все на исчерпание, либо переключаться было некуда) и настроенные
    // сторонние провайдеры. Эфемерно (в history не пишется): после сброса окна
    // предложение неактуально. internal — тестируется без розыгрыша целого хода
    // (см. OfferProviderFallbackAsync_*Tests).
    internal async Task OfferProviderFallbackAsync(string sessionId, string? resetsAt)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (_llmProviders.ResolveByModel(entry.Info.Model) is not null) return; // уже сторонний
        var current = entry.Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey;
        if (!_subscriptionPool.IsExhausted(current)) return;

        var subscriptionOptions = _subscriptionPool.All
            .Where(s => s.Key != current && !_subscriptionPool.IsExhausted(s.Key)
                && _subscriptionPool.SupportsModel(s.Key, entry.Info.Model))
            .Select(s => new ProviderFallbackOption(
                s.Key,
                string.IsNullOrWhiteSpace(s.DisplayName) ? s.Key : s.DisplayName,
                entry.Info.Model ?? "",
                Kind: "subscription",
                TierLabel: _subscriptionPool.TierLabel(s.Key),
                Utilization: _subscriptionPool.EffectiveUtilization(s.Key)))
            .Where(o => !string.IsNullOrEmpty(o.Model));

        var providerOptions = _llmProviders.Enabled
            .Select(p => new ProviderFallbackOption(p.Key,
                string.IsNullOrWhiteSpace(p.DisplayName) ? p.Key : p.DisplayName,
                p.Models.FirstOrDefault()?.Id ?? ""))
            .Where(o => !string.IsNullOrEmpty(o.Model));

        var options = subscriptionOptions.Concat(providerOptions).ToList();
        if (options.Count > 0)
            await BroadcastAsync(sessionId, new ProviderLimitMessage(resetsAt, options));
    }

    // «Переезжать некуда»: по СЫРЫМ полям чата (Info.Model → Info.Provider) цель совпала с
    // текущим провайдером. Для эндпоинта migrate-provider это отказ (просили перенести — не
    // перенесли, 400), для UpdateAsync — штатный случай: он сравнивает провайдеров по
    // ЭФФЕКТИВНЫМ моделям, и после переставленного назначения места две картины расходятся.
    // Отдельный ТИП, а не сравнение текста исключения: текст пишется человеку и меняется, а
    // ловля по нему молча пропускала бы наружу любую другую форму «переезжать не нужно»
    // ложным 400 на весь PATCH.
    private sealed class ProviderUnchangedException()
        : InvalidOperationException("Чат уже на этом провайдере");

    // Единственная точка смены провайдера у чата: и кнопка «Продолжить на …» (исчерпан
    // лимит), и обычная смена модели в настройках (UpdateAsync). Транскрипт CLI локальный —
    // переносим его в профиль целевого провайдера и продолжаем разговор через --resume без
    // потери контекста.
    // subscriptionKey — явный выбор аккаунта ТОГО ЖЕ пула подписок (кнопка карточки с
    // Kind="subscription"): вместо автовыбора Pick пользователь указывает конкретный ключ.
    // model = null — «По умолчанию» из настроек чата, когда назначение места модель не даёт:
    // переезжаем на родной Claude, ничего не закрепляя. Эндпоинт migrate-provider пустую
    // модель по-прежнему не принимает — проверка живёт у него.
    public async Task<Session> MigrateProviderAsync(string sessionId, string ownerId, string? model,
        string? subscriptionKey = null)
    {
        if (GetOwned(sessionId, ownerId) is null || !_sessions.TryGetValue(sessionId, out var entry))
            throw new KeyNotFoundException("Чат не найден");

        var newModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();

        var target = _llmProviders.ResolveByModel(newModel);
        // Десктопный чат стороннему вендору не отдаём (ADR-008): в его транскрипте оседают
        // кадры рабочего стола (desktop_screen пишет base64 в .jsonl), а миграция — это копия
        // файла в чужой профиль плюс --resume с чужим ANTHROPIC_BASE_URL. Автоматический
        // фолбэк то же правило держит обрезкой цепочки (TrimChainForDesktop); здесь — второй
        // шлюз, на единственной точке РУЧНОЙ смены провайдера: и настройки чата (UpdateAsync),
        // и кнопка «Продолжить на …» карточки provider_limit. Ротация внутри пула подписок
        // Claude (target is null) правилом не затронута — эндпоинт и владелец данных те же.
        if (entry.Info.DesktopChat && target is not null)
            throw new InvalidOperationException(
                "Десктопный чат нельзя перевести на стороннего провайдера: в его истории есть "
                + "кадры рабочего стола. Останьтесь на Claude или заведите обычный чат");
        if (target is { Enabled: false })
            throw new InvalidOperationException(
                $"Провайдер «{target.DisplayName}» не настроен: задай LlmProviders:{target.Key}:ApiKey");

        var currentKey = _llmProviders.ResolveByModel(entry.Info.Model)?.Key
            ?? entry.Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey;

        ClaudeSubscriptionConfig? pickedSub = null;
        string targetKey;
        if (!string.IsNullOrWhiteSpace(subscriptionKey))
        {
            if (target is not null)
                throw new InvalidOperationException("Ключ подписки задан вместе со сторонним провайдером");
            var sub = _subscriptionPool.All.FirstOrDefault(s => s.Key == subscriptionKey);
            if (sub is null)
                throw new InvalidOperationException($"Подписка «{subscriptionKey}» не настроена");
            if (!_subscriptionPool.SupportsModel(sub.Key, newModel))
                throw new InvalidOperationException($"Подписка «{sub.Key}» не поддерживает модель «{newModel}»");
            pickedSub = sub;
            targetKey = sub.Key;
        }
        else
        {
            // Цель: сторонний провайдер — его ключ; родной Claude — доступный аккаунт пула.
            if (target is null)
            {
                // null от ResolveByModel = родной Claude (подписка, без env-оверрайдов). Но тот же
                // null получается и для неизвестной модели: её id не нашёлся ни в Models, ни по
                // префиксу ни одного провайдера — фронт и реестр рассинхронизированы (каталог
                // /api/models отдал id, которого текущий LlmProviderRegistry не знает). Молчаливый
                // фолбэк на Pick давал ложное «Чат уже на этом провайдере» (Pick выбирал тот же
                // аккаунт пула, что текущий), поэтому неизвестную модель называем вслух.
                // Два случая различает ФОРМА id (IsNativeClaudeModel), а не сравнение с
                // Info.Model: у чата на «По умолчанию» она null, и по ней opus при живом пуле
                // выглядел как неизвестная модель — PATCH настроек падал с ложным «модель не
                // найдена» (дефект 3-й итерации). Проверка безусловная: форма id от наличия
                // пула подписок не зависит, а на коробке БЕЗ ClaudeSubscriptions мусорный id
                // иначе доезжал бы до Pick → PrimaryKey → «уже на этом провайдере», молча
                // ложился в Info.Model и валил каждый следующий ход.
                // Родная модель идёт дальше в Pick: тот вернёт либо текущий аккаунт (переезд
                // вырождается в «просто закрепить модель» — ProviderUnchangedException ниже),
                // либо здоровый другой — это штатная ротация пула кнопкой «Продолжить на …».
                if (!LlmProviderRegistry.IsNativeClaudeModel(newModel))
                    throw new InvalidOperationException(
                        $"Модель «{newModel}» не найдена среди настроенных провайдеров");
                targetKey = _subscriptionPool.Pick(newModel);
            }
            else
            {
                targetKey = target.Key;
            }
        }

        if (string.Equals(targetKey, currentKey, StringComparison.OrdinalIgnoreCase))
            throw new ProviderUnchangedException();

        // Разделитель «Продолжено на …» ставим только по факту переноса: в чате, где
        // переносить было нечего, продолжать тоже нечего — карточка врала бы.
        var transcriptMoved = false;
        // Локальный проект: транскрипт живёт на устройстве в единственном профиле CLI,
        // провайдера выбирает шлюз — разговор продолжится без переноса
        if (entry.Info.ClaudeSessionId is not null && !TranscriptOnServer(entry.Info))
            transcriptMoved = true;
        else if (entry.Info.ClaudeSessionId is not null)
        {
            var hostCwd = TryResolveCwd(entry.Info)
                ?? throw new InvalidOperationException("Не удалось определить рабочую папку чата");
            // У container-пользователя и корни (песочные профили), и cwd (контейнерный путь)
            // другие. Исключение ToRuntime (путь вне монтирований) намеренно не глушим:
            // операция явная, пользователь должен увидеть причину отказа (400)
            var cwd = CwdForOwner(ownerId, hostCwd);
            var srcRoot = ConfigRootFor(ownerId, currentKey);
            // Различаем «переносить нечего» и «перенос сорвался» — TryMigrate отдаёт false в
            // обоих случаях, а последствия разные. Транскрипта может не быть вовсе:
            // ClaudeSessionId выставляется на ЛЮБОМ первом ходе (включая служебный kickoff
            // онбординга), а файл к этому моменту мог не появиться, чат мог быть создан из
            // чужого resumeSessionId, либо его убрала плановая уборка CLI. Терять нечего —
            // меняем провайдера и пишем строку в лог. Сорвавшийся же перенос НАЙДЕННОГО файла
            // остаётся жёстким отказом: TryMigrate возвращает false и по таймауту копирования
            // живого файла (CopyFileShared, дедлайн 8 с), а это молчаливая потеря контекста.
            if (TranscriptMigrator.FindTranscript(srcRoot, cwd, entry.Info.ClaudeSessionId) is null)
                Console.Error.WriteLine(
                    $"[SessionManager] Чат {sessionId}: транскрипт {entry.Info.ClaudeSessionId} "
                    + $"не найден в {srcRoot} — переносить нечего, меняем провайдера");
            else if (!TranscriptMigrator.TryMigrate(srcRoot, ConfigRootFor(ownerId, targetKey),
                         cwd, entry.Info.ClaudeSessionId, out var error))
                throw new InvalidOperationException($"Не удалось перенести транскрипт: {error}");
            else
                transcriptMoved = true;
        }

        entry.Info.Model = newModel;
        entry.Info.Provider = targetKey;
        entry.Info.UpdatedAt = DateTime.UtcNow;
        // Адаптер собран под прежнего провайдера (env и сигнатура хода) — убираем лениво,
        // как при смене собеседника: мгновенный dispose рвал бы доживающих агентов
        if (entry.Process is not null) entry.AdapterStale = true;
        // Minor (волна 3): миграция посреди план-фазы (интервью/планирование) на провайдера
        // без поддержки «План» — раньше SavedMode оставался висеть навсегда: селектор режима
        // заблокирован (SetMode отказывает при SavedMode != null), а ход уже идёт не в
        // план-режиме (новый провайдер --permission-mode plan не понимает). Та же деградация
        // «молча остаёмся в прежнем режиме», что и EnterPlanPhaseMode на входе в план-фазу —
        // только тут применяется задним числом, когда план-фаза уже шла на другом провайдере.
        if (entry.Info.TeamImplement is { SavedMode: not null }
            && !_llmProviders.CapabilitiesFor(newModel).SupportsPlanMode)
            _teamNotifier.RestoreUserMode(sessionId);
        SaveSessions();

        // Явно выбранный аккаунт пула — подпись «на подписке», а не безликое «на AI».
        // Label = null — сообщение уходит без разделителя: висящую карточку «Продолжить на …»
        // оно всё равно гасит (chatReducer), а ленту не засоряет. Разделителя нет в двух
        // случаях: переносить было нечего (продолжать нечего — карточка врала бы) и автовыбор
        // ДРУГОГО аккаунта того же пула Claude (ротация подписок по договорённости тихая: тип
        // поставщика не менялся, а «Продолжено на AI» читается как уход к другому вендору).
        // Явный тык в карточку подписки (pickedSub) подпись сохраняет — это выбор человека.
        string? switchLabel = null;
        if (transcriptMoved)
        {
            if (pickedSub is not null)
                switchLabel = "Продолжено на подписке "
                    + $"«{(string.IsNullOrWhiteSpace(pickedSub.DisplayName) ? pickedSub.Key : pickedSub.DisplayName)}»";
            else if (target is not null)
                switchLabel = "Продолжено на "
                    + (string.IsNullOrWhiteSpace(target.DisplayName) ? target.Key : target.DisplayName);
            // Возврат СО стороннего провайдера на родной Claude — смена типа поставщика, подпись нужна
            else if (_llmProviders.GetByKey(currentKey) is not null)
                switchLabel = "Продолжено на AI";
        }
        await BroadcastAsync(sessionId, new ProviderSwitchedMessage(targetKey, newModel, switchLabel));
        Console.WriteLine($"[SessionManager] Чат {sessionId} мигрирован: {currentKey} → {targetKey} "
            + $"({newModel ?? "по умолчанию"}, транскрипт: {(transcriptMoved ? "перенесён" : "нечего переносить")})");
        return entry.Info;
    }

    // Значения include ветвления чата (docs/research/chat-branching-2026-09.md §5, §6):
    // turn — история по конец хода (кнопка под ответом ассистента), beforePrompt — история
    // до этого сообщения, а его текст возвращается как draft для композера (транскрипт
    // обязан заканчиваться завершённым ходом — висящий промпт склеился бы со следующим).
    public static class ChatBranchInclude
    {
        public const string Turn = "turn";
        public const string BeforePrompt = "beforePrompt";
    }

    // Итог ветвления: новый чат + черновик композера (не null только у include=beforePrompt).
    public sealed record ChatBranchResult(Session Session, string? Draft);

    // Ветвление чата (шаг 3 плана chat-branch, документ-основание §3/§8/§9/§11): новый чат
    // со своим ClaudeSessionId, которому подложены обрезанный транскрипт CLI и обрезанная
    // history.json. Оригинал НЕ изменяется ни одним мутатором (ни SaveSessions по нему, ни
    // UpdatedAt/ArchivedAt/ClaudeSessionId) — источник читается, не трогается.
    //
    // userMessageIndex — 0-based номер элемента StoredUserMessage в истории оригинала
    // (включая служебные ходы auto/staffNote/systemDirective — они тоже user_message).
    // anchorText — фрагмент текста этого сообщения от клиента; сервер сверяет его с
    // найденным по индексу и на расхождении отказывает 409 (индекс — договорённость двух
    // счётчиков, молча резать не там нельзя).
    public async Task<ChatBranchResult> BranchAsync(string sessionId, string ownerId,
        int userMessageIndex, string anchorText, string include, string? name = null)
    {
        if (GetOwned(sessionId, ownerId) is not { } source
            || !_sessions.TryGetValue(sessionId, out var entry))
            throw new KeyNotFoundException("Чат не найден");

        // §9.1 — идёт ход или живут фоновые агенты (копируем файл, в который пишет CLI)
        if (HasTurnInFlight(entry))
            throw new InvalidOperationException(
                "В чате идёт ход — дождитесь его завершения или прервите его, затем попробуйте снова");
        if (entry.Process is { HasTrackedBg: true })
            throw new InvalidOperationException(
                "В чате работают фоновые агенты — дождитесь их завершения, затем попробуйте снова");

        // Локальный проект: транскрипт-источник на устройстве, копировать его серверу неоткуда
        if (source.ProjectId is { } branchProjectId && _projects.GetById(branchProjectId) is { } branchProject)
            Composition.ProjectCapabilityGuard.EnsureAllowed(branchProject, Composition.ProjectCapabilityArea.Transcript);

        // §9.2 — нет ClaudeSessionId: на экране история есть, в памяти модели — нет
        if (source.ClaudeSessionId is not string csid)
            throw new InvalidOperationException("Чат ещё не обращался к модели: ветвить нечего");

        // §9.3 — десктопный чат (ADR-008): его транскрипт с кадрами рабочего стола наружу
        // не отдаём. DesktopChatGuard.Refuse тут не работает (ищет по совпадению
        // resumeSessionId с чужим ClaudeSessionId, а у ветки id новый) — берём только текст.
        if (source.DesktopChat)
            throw new InvalidOperationException(Controllers.DesktopChatGuard.ResumeFromDesktop);

        // §9.6 — групповой чат и режим штаба: их состояние волн/спикеров живёт в Session,
        // а не в транскрипте — ветка унаследовала бы ленту без состояния
        if (source.Participants is { Count: > 0 })
            throw new InvalidOperationException(
                "Групповой чат нельзя ветвить: состав спикеров живёт в чате, а не в транскрипте");
        if (source.TeamImplement is not null)
            throw new InvalidOperationException(
                "Чат в режиме «Командная реализация» нельзя ветвить: состояние волн живёт в чате, а не в транскрипте");

        // §9.8 — чат в отдельном worktree: скопированный контекст указывал бы на чужое
        // дерево, которое сносится вместе с чатом-источником
        if (source.WorktreePath is not null)
            throw new InvalidOperationException(
                "Чат в отдельном рабочем дереве нельзя ветвить: контекст ветки указывал бы на дерево, которое сносится вместе с чатом-источником");

        if (include != ChatBranchInclude.Turn && include != ChatBranchInclude.BeforePrompt)
            throw new InvalidOperationException($"Неизвестное значение include: «{include}»");

        // §5 — адресация шага: тот же номер, что считает клиент по StoredUserMessage истории
        var history = await _history.LoadAsync(csid);
        var userMessages = history.OfType<StoredUserMessage>().ToList();
        if (userMessageIndex < 0 || userMessageIndex >= userMessages.Count)
            throw new InvalidOperationException(
                $"Сообщение с индексом {userMessageIndex} не найдено в истории чата");
        var anchorMessage = userMessages[userMessageIndex];

        // Сверка текста с индексом — fail-closed: живая лента клиента и серверная история
        // расходятся штатно, молча резать не там нельзя (§6 документа-основания)
        var normalizedFound = Llm.TranscriptBrancher.Normalize(anchorMessage.Text);
        var normalizedProvided = Llm.TranscriptBrancher.Normalize(anchorText);
        if (normalizedProvided.Length == 0
            || !normalizedFound.Contains(normalizedProvided, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Индекс сообщения и присланный текст разошлись — обновите чат и попробуйте снова");

        // §Резолв источника: самая длинная копия среди всех профилей (147 из 487 чатов имеют
        // копии разной длины — короткая дала бы ветку без начала разговора), фолбэк — архив
        // заархивированного чата. §9.4 — не найдено нигде — 400 (переносить нечего ≠ копировать
        // нечего). Оригинал из архива не возвращаем — ArchivedAt не трогаем.
        var searchRoots = TranscriptSearchRoots(source);
        var hostCwd = TryResolveCwd(source);
        var sourceCwd = hostCwd is null ? null : CwdForOwner(ownerId, hostCwd);

        string? srcPath = null;
        var bestLen = -1L;
        foreach (var file in Llm.TranscriptMigrator.FindAllTranscripts(searchRoots, sourceCwd, csid))
        {
            var len = new FileInfo(file).Length;
            if (len > bestLen) { bestLen = len; srcPath = file; }
        }
        srcPath ??= _archivedTranscripts.FindCopyPath(csid);

        if (srcPath is null)
            throw new InvalidOperationException(
                "Память этого разговора уже убрана плановой уборкой CLI");

        // §9.7 — потолок размера, тот же, что у ArchivedTranscriptStore.MaxCopyBytes
        if (new FileInfo(srcPath).Length > _archivedTranscripts.MaxCopyBytes)
            throw new InvalidOperationException(
                "Транскрипт чата слишком большой для ветвления (потолок 512 МБ)");

        // Новый csid — свой, не общий с оригиналом (иначе два чата делили бы один транскрипт
        // и одну историю, и удаление одного унесло бы память другого)
        var newCsid = Guid.NewGuid().ToString();

        // Схема хранения (§3): профиль тот же, что у оригинала; cwd — ВЕТКИ, не оригинала
        // (ветка не наследует WorktreePath — гейт §9.8 гарантирует, что у source его и нет,
        // поэтому cwd ветки = корень проекта/чатов владельца)
        var branchConfigRoot = ConfigRootFor(ownerId, source.Provider);
        var branchRootPath = source.ProjectId is { } sourcePid
            ? (_projects.GetById(sourcePid)?.RootPath
                ?? throw new InvalidOperationException("Проект чата не найден"))
            : ResolveChatRoot(ownerId);
        var branchCwd = CwdForOwner(ownerId, branchRootPath);

        var dstDir = Path.Combine(branchConfigRoot, "projects", Llm.TranscriptMigrator.FlattenCwd(branchCwd));
        Directory.CreateDirectory(dstDir);
        var dstPath = SafePath.Join(dstDir, newCsid + ".jsonl");

        // Якоря для резака: неслужебные сообщения истории до якоря (не включая его) +
        // сам якорь последним (TranscriptBrancher трактует последний элемент как якорь шага)
        var anchors = new List<string>();
        for (var i = 0; i < userMessageIndex; i++)
        {
            var m = userMessages[i];
            if (m.Auto == true || m.SystemDirective == true || m.StaffNote != null || m.ViaAgent == true)
                continue;
            anchors.Add(m.Text);
        }
        anchors.Add(anchorMessage.Text);

        // Точный якорь (шаг 5): uuid хвоста транскрипта, записанный на конце ЯКОРНОГО хода —
        // последний result этого хода, то есть до следующего сообщения пользователя. Есть —
        // резак берёт границу точным сравнением; нет (чат до этого поля) — текстовый путь.
        var anchorHistoryIndex = history.IndexOf(anchorMessage);
        string? anchorUuid = null;
        for (var i = anchorHistoryIndex + 1; i < history.Count; i++)
        {
            if (history[i] is StoredUserMessage) break;
            if (history[i] is StoredResultMessage { TranscriptTailUuid: { } uuid }) anchorUuid = uuid;
        }

        // §9.5 — границу не удалось сопоставить (отказ резака) — 409, резать наугад нельзя.
        // include резак обязан знать: у beforePrompt граница транскрипта — сам якорный промпт
        // (не включая), иначе лента ветки и её транскрипт расходятся — на экране вопроса нет,
        // а в памяти модели и вопрос, и прежний ответ на него.
        // anchorTimestampMs — главный ключ текстового пути резака (ResolveAnchorPrompt):
        // время отправки якорного сообщения, по лагу до записи промпта в транскрипт
        // различаются повторяющиеся тексты («продолжай», тики /loop). null — история до
        // этого поля, там резак работает по цепочке.
        var branchResult = Llm.TranscriptBrancher.Branch(srcPath, anchors, newCsid, dstPath, anchorUuid,
            include == ChatBranchInclude.BeforePrompt
                ? Llm.TranscriptBrancher.BranchInclude.BeforePrompt
                : Llm.TranscriptBrancher.BranchInclude.Turn,
            anchorMessage.Timestamp);
        if (!branchResult.Ok)
        {
            Console.Error.WriteLine($"[SessionManager] Ветвление чата {sessionId} отказано резаком: {branchResult.Reason}");
            throw new InvalidOperationException(
                "Не удалось найти этот шаг в памяти модели; попробуйте ветвиться от другого сообщения");
        }

        // Обрезанная история ветки: turn — по конец хода (следующий user_message или конец
        // файла), beforePrompt — до якорного сообщения (оно не входит, его текст — draft,
        // иначе транскрипт заканчивался бы висящим промптом и следующий ход подклеил бы второй)
        int cutAt;
        string? draft = null;
        if (include == ChatBranchInclude.BeforePrompt)
        {
            cutAt = anchorHistoryIndex;
            draft = anchorMessage.Text;
        }
        else
        {
            // Граница — следующий user_message ИЛИ собственная плашка источника: когда
            // ветвят саму ветку от её последнего хода, чужая плашка «Ветка от …» (§7 — она
            // всегда последняя запись истории) попала бы в копию и встала бы второй рядом
            // со свежей. Унаследованная плашка неверна: у новой ветки источник свой.
            cutAt = history.Count;
            for (var i = anchorHistoryIndex + 1; i < history.Count; i++)
                if (history[i] is StoredUserMessage or StoredBranchedFromMessage) { cutAt = i; break; }
        }

        // Резак отступил назад: хвост последнего хода префикса оказался непарным (прерванный
        // ход, кнопка «Стоп») и фактическая граница транскрипта ушла к НАЧАЛУ этого хода.
        // История обязана совпасть с ней шаг в шаг, иначе в ленте остался бы ход, которого
        // нет в памяти модели. 409 тут не отдаём (продуктовое решение): отказывать за то, что
        // разговор когда-то прервался, хуже, чем перенести незавершённый ввод в черновик —
        // тем же путём, что уже работает у beforePrompt.
        if (branchResult.AnchorTurnExcluded)
        {
            // Сообщение, начавшее исключённый ход: у turn это сам якорь, у beforePrompt —
            // предыдущее сообщение пользователя (якорный ход в ветку и так не входил).
            var droppedIndex = anchorHistoryIndex;
            if (include == ChatBranchInclude.BeforePrompt)
            {
                droppedIndex = -1;
                for (var i = anchorHistoryIndex - 1; i >= 0; i--)
                    if (history[i] is StoredUserMessage) { droppedIndex = i; break; }
                if (droppedIndex < 0)
                    throw new InvalidOperationException(
                        "Не удалось найти этот шаг в памяти модели; попробуйте ветвиться от другого сообщения");
            }
            cutAt = droppedIndex;
            draft = (history[droppedIndex] as StoredUserMessage)?.Text;
        }

        var branchHistory = history.Take(cutAt).ToList();
        // Плашка «Ветка от …» — последней записью, ровно в точке расхождения (§7)
        branchHistory.Add(new StoredBranchedFromMessage
        {
            SourceSessionId = sessionId,
            SourceName = source.Name ?? "Чат",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        await _history.SaveAsync(newCsid, branchHistory);

        // Имя по умолчанию — «{имя оригинала} (ветка)»; явно переданное имя перебивает
        var branchName = string.IsNullOrWhiteSpace(name)
            ? $"{(string.IsNullOrWhiteSpace(source.Name) ? "Чат" : source.Name)} (ветка)"
            : name.Trim();

        // Создание чата штатной фабрикой (§ «Создание чата»): выбор по ProjectId/PersonaId
        // оригинала, resumeSessionId = новый csid — StartNewSessionAsync подхватит уже
        // подготовленные транскрипт и историю
        var branchSession = !string.IsNullOrWhiteSpace(source.PersonaId)
            ? await CreatePersonaChatAsync(ownerId, source.PersonaId!, source.Mode,
                resumeSessionId: newCsid, name: branchName, contextProjectId: source.ProjectId)
            : source.ProjectId is not null
                ? await CreateAsync(source.ProjectId, source.Mode, resumeSessionId: newCsid,
                    name: branchName, model: source.Model, effort: source.Effort)
                : await CreateChatAsync(ownerId, source.Mode, resumeSessionId: newCsid,
                    name: branchName, model: source.Model, effort: source.Effort);

        // §11 — наследование полей поверх созданного чата: фабрики персоны/проекта не берут
        // все поля параметрами (персональная фабрика вовсе подставляет модель персоны, а не
        // оригинала), поэтому модель/провайдер/effort и остальные поля таблицы §11
        // принудительно переписываются на значения оригинала уже ПОСЛЕ создания.
        if (_sessions.TryGetValue(branchSession.Id, out var branchEntry))
        {
            var b = branchEntry.Info;
            // Наследуем: Model/Provider/Effort/Context/VoiceMode/VoiceStyle/NotificationsMuted —
            // тот же разговор тем же собеседником; ExcludeFromDossiers — иначе ветвление
            // обходит явный opt-out «не сохранять решения» (Session.cs:380-382).
            b.Model = source.Model;
            b.Provider = source.Provider;
            b.Effort = source.Effort;
            b.Context = [.. source.Context.Select(c => new SessionContextEntry
                { Type = c.Type, Id = c.Id, Title = c.Title })];
            b.VoiceMode = source.VoiceMode;
            b.VoiceStyle = source.VoiceStyle;
            b.NotificationsMuted = source.NotificationsMuted;
            b.ExcludeFromDossiers = source.ExcludeFromDossiers;
            // НЕ наследуем: AutoAllowTools (человек переспросит один раз, а не тихая эскалация
            // прав), WorktreePath/WorktreeBranch/TaskId/TaskExecution/ExpiresAfterMinutes/
            // ArchivedAt/ArchiveBatchId/LastReadAt — фабрика их и так не выставляет.
            b.BranchedFromSessionId = sessionId;
            SaveSessions();
            branchSession = b;
        }

        return new ChatBranchResult(branchSession, draft);
    }

    public Session? GetById(string id) =>
        _sessions.TryGetValue(id, out var entry) ? entry.Info : null;

    // Состояние делегирования ИДУЩЕГО хода сессии. Спрашивает DenyOnDelegatedTurnAttribute по
    // заголовку MCP-сервера: единственный достоверный источник — живой адаптер, тогда как
    // заголовок/env запекаются при старте процесса и протухают при переиспользовании прогона.
    // Чужая сессия или отсутствие процесса — «обычный ход» (запрет не применяется).
    // Владение проверяем ТОЛЬКО через GetOwned (внутри — ResolveOwnerId): у проектной сессии
    // Session.OwnerId равен null, владелец живёт у проекта. Прямое сравнение с этим полем молча
    // отключало запрет, и делегированный ход спокойно запускал исполнителя — поймано live-тестом,
    // юнит-тесты такое не видят. Один способ резолва владельца на весь класс.
    public TurnDelegationState GetActiveTurnDelegation(string sessionId, string ownerId) =>
        GetOwned(sessionId, ownerId) is not null
            && _sessions.TryGetValue(sessionId, out var entry)
            && entry.Process is { } adapter
            ? new TurnDelegationState(adapter.CurrentTurnAgentDepth, adapter.CurrentTurnSuppressTasksExecute)
            : new TurnDelegationState(0, false);

    // Активен ли цикл «до готово» в чате-вызывателе: гейт анти-рекурсии (DelegatedTurnGate.Decide)
    // спрашивает это, чтобы разрешить ход-реакцию на доклад при активном цикле (единственная точка,
    // где координатор принимает результат и запускает следующего). Владение — тот же путь, что в
    // GetActiveTurnDelegation: GetOwned (внутри — ResolveOwnerId).
    public bool HasActiveWorkLoop(string sessionId, string ownerId) =>
        GetOwned(sessionId, ownerId)?.WorkLoop is not null;

    // Запомнить заметку-итог сессии (SessionSummaryService) — для обновления при повторной генерации
    public void SetSummaryNoteId(string sessionId, string noteId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Info.SummaryNoteId = noteId;
        SaveSessions();
    }

    // Кэш сводки карточки архива (ChatDigestService, место chat-digest): текст и момент
    // сборки — при UpdatedAt > ArchiveSummaryAt сводка не актуальна (см. Session).
    // UpdatedAt намеренно не двигается: сборка сводки — не активность чата, иначе она
    // сама снимала бы архив и поднимала чат в списке. null — сбросить кэш.
    public void SetArchiveSummary(string sessionId, string? summary)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Info.ArchiveSummary = summary;
        entry.Info.ArchiveSummaryAt = summary is null ? null : DateTime.UtcNow;
        SaveSessions();
    }

    // Пометить пути чатов «правки зафиксированы в git» (детект сдвига HEAD —
    // CommitAttributionService). Bulk: ОДИН SaveSessions на весь коммит — SaveSessions
    // перезаписывает весь sessions.json, звать его в цикле по чатам недопустимо.
    // Paths — пути коммита, пересечённые с сырым множеством чата (считает вызывающий);
    // RawPaths — само сырое множество: пометки, выпавшие из него (история переписана),
    // вычищаются здесь же — на чтении индекс их только игнорирует, стор не переписывая.
    // Инвариант: UpdatedAt не двигаем (по нему сортировка и непрочитанность,
    // а фиксация правок — не активность чата). Пути — нормализованные (lowercase,
    // прямые слэши); список подменяется целиком, а не мутируется на месте, чтобы
    // конкурентная сериализация SaveSessions не увидела список в полразборе.
    public void MarkFilesCommitted(
        IReadOnlyList<(string SessionId, IReadOnlyCollection<string> Paths, IReadOnlyCollection<string> RawPaths)> batch)
    {
        // Общий лок с UnmarkFileCommitted: пометка (поток статуса) и снятие (петля сообщений
        // хода) делают read-modify-write одного списка — без лока коммит ровно в момент
        // правки того же файла терял бы обновление. _saveLock (System.Threading.Lock)
        // реентерабелен (подсчёт рекурсии): вложенный SaveSessions берёт его повторно без клинча.
        lock (_saveLock)
        {
            var changed = false;
            foreach (var (sessionId, paths, rawPaths) in batch)
            {
                if (!_sessions.TryGetValue(sessionId, out var entry)) continue;
                var current = new HashSet<string>(entry.Info.CommittedFilePaths, StringComparer.Ordinal);
                var next = new HashSet<string>(current, StringComparer.Ordinal);
                next.UnionWith(paths);
                next.IntersectWith(rawPaths is HashSet<string> h ? h : [.. rawPaths]);
                if (next.SetEquals(current)) continue;
                entry.Info.CommittedFilePaths = [.. next.Order()];
                changed = true;
            }
            if (changed) SaveSessions();
        }
    }

    // Вернуть путь в учёт атрибуции: чат снова правит файл после фиксации. Сидит на
    // ГОРЯЧЕМ пути (каждое write-сообщение хода) — ранний выход без записи, когда
    // пометки нет. path — уже нормализованный (SessionChangedPaths.Normalize).
    // UpdatedAt не двигаем (см. MarkFilesCommitted).
    public void UnmarkFileCommitted(string sessionId, string path)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        // Быстрая проверка ДО лока — горячий путь (каждое write-сообщение хода) не должен
        // толкаться с пометкой коммита; под локом перепроверяется (см. MarkFilesCommitted)
        if (!entry.Info.CommittedFilePaths.Contains(path, StringComparer.Ordinal)) return;
        lock (_saveLock)
        {
            if (!entry.Info.CommittedFilePaths.Contains(path, StringComparer.Ordinal)) return;
            entry.Info.CommittedFilePaths = [.. entry.Info.CommittedFilePaths.Where(p => p != path)];
            SaveSessions();
        }
    }

    // Ветка tool_use снятия пометки: file_changed НЕ хватает — событие не приходит, когда
    // содержимое не изменилось, путь отсечён TurnFileWatcher.ShouldIgnore или файл удалён;
    // такой файл после коммита выпал бы из атрибуции навсегда. Гейт worktree-хода зеркалит
    // SessionChangedPaths.Extract (TurnFileWatcher отдаёт пути относительно СВОЕГО корня —
    // правка в worktree не должна воскрешать атрибуцию одноимённого файла в корне).
    // Известное ограничение (принято): снятие идёт по ЗАЯВКЕ tool_use, а не по успешному
    // tool_result — отклонённый permission или упавший Edit вернут атрибуцию файлу,
    // которого чат фактически не менял (то же множество источников, что у Extract).
    private void TryUnmarkCommittedOnToolUse(string sessionId, SessionEntry? entry, string toolName, object? input)
    {
        if (entry is null || entry.TurnInWorktree) return;
        if (!SessionChangedPaths.IsWriteTool(toolName)) return;
        // Ранний выход до резолва проекта: у чата без пометок здесь горячий no-op
        if (entry.Info.CommittedFilePaths.Count == 0) return;
        var root = entry.Info.ProjectId is { } pid ? _projects.GetById(pid)?.RootPath : null;
        if (root is null) return;
        if (SessionChangedPaths.NormalizedToolPath(input, root) is { } rel)
            UnmarkFileCommitted(sessionId, rel);
    }

    public async Task<Session> CreateAsync(string projectId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? agentName = null,
        string? effort = null, string? personaId = null, bool taskExecution = false, string? taskId = null,
        string? onboardingKind = null, bool desktopChat = false)
    {
        var project = _projects.GetById(projectId)
            ?? throw new KeyNotFoundException($"Проект не найден: {projectId}");

        var defaultModel = ResolveDefaultModel(UsageKeyFor(taskExecution, taskId, personaId),
            model, resumeSessionId, project.OwnerId);
        var session = new Session
        {
            ProjectId = projectId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId,
            Name = name,
            Model = string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim(),
            AgentName = string.IsNullOrWhiteSpace(agentName) ? null : agentName.Trim(),
            Provider = ResolveSubscriptionProvider(defaultModel),
            Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim(),
            // Персона-слой подхватится общим механизмом (BuildPersonaLayer).
            // Маршрутизация остаётся по вызывающему коду (задача), не по зоне персоны.
            PersonaId = string.IsNullOrWhiteSpace(personaId) ? null : personaId,
            TaskExecution = taskExecution,
            TaskId = taskId,
            // Тип чата «Десктопный» (ADR-008): задаётся при СОЗДАНИИ и дальше не меняется —
            // состав грани фиксируется на момент запуска CLI
            DesktopChat = desktopChat,
            // Онбординг-сессия: задаётся ДО старта — BuildPersonaLayer читает поле при сборке слоя
            OnboardingKind = onboardingKind,
        };

        await StartNewSessionAsync(session, project.RootPath, project.SystemPrompt,
            () => _projects.GetById(projectId)?.PermissionRules ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>());
        return session;
    }

    // Создание чата вне проекта: рабочая папка — {домашняя папка владельца}/Chats,
    // системный промпт — только встроенная часть (rawSystemPrompt=null), без проектных правил.
    public async Task<Session> CreateChatAsync(string ownerId, ClaudeMode mode,
        string? resumeSessionId = null, string? name = null, string? model = null, string? effort = null,
        string? personaId = null, bool taskExecution = false, string? taskId = null,
        string? onboardingKind = null)
    {
        var rootPath = ResolveChatRoot(ownerId);

        var defaultModel = ResolveDefaultModel(UsageKeyFor(taskExecution, taskId, personaId),
            model, resumeSessionId, ownerId);
        var session = new Session
        {
            ProjectId = null,
            OwnerId = ownerId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId,
            Name = name,
            Model = string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim(),
            Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim(),
            // Персона-слой подхватится общим механизмом (BuildPersonaLayer)
            Provider = ResolveSubscriptionProvider(defaultModel),
            PersonaId = string.IsNullOrWhiteSpace(personaId) ? null : personaId,
            TaskExecution = taskExecution,
            TaskId = taskId,
            // Онбординг-сессия: задаётся ДО старта — BuildPersonaLayer читает поле при сборке слоя
            OnboardingKind = onboardingKind,
        };

        await StartNewSessionAsync(session, rootPath, rawSystemPrompt: null, permissionRules: null);
        return session;
    }

    // Создание чата от лица персоны. Маршрутизация по зоне:
    // проектная персона → сессия в её проекте (scope = проект); глобальная (или проект
    // недоступен) → чат вне проекта (scope = все данные владельца). Модель по умолчанию — из персоны.
    // contextProjectId — проект, ИЗ которого зовут глобальную персону («Поговорить» в проекте):
    // чат создаётся в нём, а не вне проекта (как давно позволяет смена собеседника SetPersona).
    public async Task<Session> CreatePersonaChatAsync(string ownerId, string personaId,
        ClaudeMode mode, string? resumeSessionId = null, string? name = null,
        string? contextProjectId = null, string? automationRuleId = null)
    {
        var persona = _personas.Get(personaId, ownerId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {personaId}");

        // Проект сессии: у проектной персоны — её собственный; у глобальной — контекстный
        var targetProjectId = persona.Scope == PersonaScope.Project
            ? persona.ProjectId
            : contextProjectId;

        // Персона без своей модели идёт своим уровнем, без уровня — назначением места «чат с персоной».
        // Дефолт места (Strong) передаётся в резолв, чтобы ячейка персоны без явного уровня сработала.
        var personaModel = ResolveDefaultModel(Llm.LocalActionCatalog.ChatPersona,
            _assignments.PersonaModel(persona, ownerId,
                Llm.LocalActionCatalog.DefaultTierOf(Llm.LocalActionCatalog.ChatPersona)),
            resumeSessionId, ownerId);

        if (!string.IsNullOrEmpty(targetProjectId)
            && _projects.GetById(targetProjectId) is { } project && project.OwnerId == ownerId)
        {
            var projectSession = new Session
            {
                ProjectId = project.Id,
                OwnerId = ownerId,
                PersonaId = personaId,
                Mode = mode,
                ClaudeSessionId = resumeSessionId,
                Name = name,
                Model = personaModel,
                Provider = ResolveSubscriptionProvider(personaModel),
                Effort = persona.Effort,
                AutomationRuleId = automationRuleId,
            };
            await StartNewSessionAsync(projectSession, project.RootPath, project.SystemPrompt,
                () => _projects.GetById(project.Id)?.PermissionRules
                    ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>());
            return projectSession;
        }

        var rootPath = ResolveChatRoot(ownerId);
        var session = new Session
        {
            ProjectId = null,
            OwnerId = ownerId,
            PersonaId = personaId,
            Mode = mode,
            ClaudeSessionId = resumeSessionId,
            Name = name,
            Model = personaModel,
            Provider = ResolveSubscriptionProvider(personaModel),
            Effort = persona.Effort,
            AutomationRuleId = automationRuleId,
        };
        await StartNewSessionAsync(session, rootPath, rawSystemPrompt: null, permissionRules: null);
        return session;
    }

    // Создание группового чата (флаг persona-group-chats): 2-4 персоны владельца,
    // первая — ведущая (стартовый активный спикер). Зона — по ведущей, как в
    // CreatePersonaChatAsync: проектная персона → сессия её проекта, глобальная → чат вне проекта.
    public async Task<Session> CreateGroupChatAsync(string ownerId, IReadOnlyList<string> personaIds,
        ClaudeMode mode, string? name = null)
    {
        var participants = ValidateParticipants(ownerId, personaIds);
        var leader = participants[0];
        var participantIds = participants.Select(p => p.Id).ToList();
        // Ведущая без своей модели идёт своим уровнем, без уровня — назначением места «чат с персоной»
        // (дефолт места передаётся в резолв, чтобы ячейка ведущей без уровня сработала).
        var leaderModel = ResolveDefaultModel(Llm.LocalActionCatalog.ChatPersona,
            _assignments.PersonaModel(leader, ownerId,
                Llm.LocalActionCatalog.DefaultTierOf(Llm.LocalActionCatalog.ChatPersona)),
            resumeSessionId: null, ownerId);

        if (leader.Scope == PersonaScope.Project && !string.IsNullOrEmpty(leader.ProjectId)
            && _projects.GetById(leader.ProjectId) is { } project && project.OwnerId == ownerId)
        {
            var projectSession = new Session
            {
                ProjectId = project.Id,
                OwnerId = ownerId,
                PersonaId = leader.Id,
                Participants = participantIds,
                Mode = mode,
                Name = name,
                Model = leaderModel,
                Provider = ResolveSubscriptionProvider(leaderModel),
                Effort = leader.Effort,
            };
            await StartNewSessionAsync(projectSession, project.RootPath, project.SystemPrompt,
                () => _projects.GetById(project.Id)?.PermissionRules
                    ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>());
            return projectSession;
        }

        var rootPath = ResolveChatRoot(ownerId);
        var session = new Session
        {
            ProjectId = null,
            OwnerId = ownerId,
            PersonaId = leader.Id,
            Participants = participantIds,
            Mode = mode,
            Name = name,
            Model = leaderModel,
            Effort = leader.Effort,
            Provider = ResolveSubscriptionProvider(leaderModel),
        };
        await StartNewSessionAsync(session, rootPath, rawSystemPrompt: null, permissionRules: null);
        return session;
    }

    // Обновить состав участников группового чата. Активный спикер сохраняется,
    // если остался в составе, иначе — новая ведущая. Адаптер пересоздаётся
    // (состав участников зашит в подсказку @упоминаний и групповой слой промпта).
    public Session? SetParticipants(string sessionId, string ownerId, IReadOnlyList<string> personaIds)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (ResolveOwnerId(entry.Info) != ownerId) return null;

        var participants = ValidateParticipants(ownerId, personaIds);
        entry.Info.Participants = participants.Select(p => p.Id).ToList();
        var speaker = participants.FirstOrDefault(p => p.Id == entry.Info.PersonaId) ?? participants[0];
        SwitchSpeaker(entry, speaker);
        return entry.Info;
    }

    // Участники группового чата: 2-4 уникальные персоны, все принадлежат владельцу
    private List<Persona> ValidateParticipants(string ownerId, IReadOnlyList<string> personaIds)
    {
        var ids = (personaIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count is < 2 or > 8)
            throw new InvalidOperationException("В групповом чате участвуют от 2 до 8 персон");
        return ids.Select(id => _personas.Get(id, ownerId)
            ?? throw new KeyNotFoundException($"Персона не найдена: {id}")).ToList();
    }

    // Чаты владельца, ведущиеся от лица конкретной персоны (для раздела «Персоны»)
    public IReadOnlyList<Session> GetPersonaChats(string ownerId, string personaId) =>
        _sessions.Values
            .Select(e => e.Info)
            .Where(s => s.PersonaId == personaId && ResolveOwnerId(s) == ownerId)
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();

    // Владелец сессии: у чата — OwnerId, у проектной — владелец проекта
    // Единая точка резолва владельца сессии: у проектной — владелец проекта, у чата вне
    // проекта — сама сессия. Единственный источник истины для хаба и контроллеров тоже.
    public string? ResolveOwnerId(Session s) =>
        s.ProjectId is not null ? _projects.GetById(s.ProjectId)?.OwnerId : s.OwnerId;

    // Есть ли у пользователя хоть одна сессия/чат — среда исполнения меняется только «начисто»
    // (корни проектов и профили сред различаются, resume привязан к путям старой среды)
    public bool HasSessionsOwnedBy(string ownerId) =>
        _sessions.Values.Any(e => ResolveOwnerId(e.Info) == ownerId);

    // Все сессии пользователя (проектные + чаты) — для сводки дашборда «Домой»
    public IReadOnlyCollection<Session> GetAllOwnedBy(string ownerId) =>
        _sessions.Values
            .Select(e => e.Info)
            .Where(s => ResolveOwnerId(s) == ownerId)
            .ToList();

    // Живая персона чата для цепочки фолбэка (ClaudeSession.EffectiveTurnChain →
    // ResolveChain с матрицами персоны): перечитывается на каждый ход — правка матриц
    // персоны/специальности применяется со следующего хода без пересоздания адаптера.
    // null — у чата нет персоны (или владельца).
    private Func<Persona?> BuildPersonaProvider(Session session, string? ownerId) =>
        () => session.PersonaId is { } pid && ownerId is not null ? _personas.Get(pid, ownerId) : null;

    // Персона-слой сессии: контекст памяти (для долгой памяти персоны/команды) + сама персона
    // для гейтов возможностей. Промпт характера + групповая надстройка + онбординг-оверлей —
    // у IPromptSectionContributor (PersonaLayerContributor).
    private (MemoryMcpContext? Memory, Persona? Persona)
        BuildPersonaLayer(Session session, string? ownerId)
    {
        if (session.PersonaId is null || ownerId is null) return (null, null);
        var persona = _personas.Get(session.PersonaId, ownerId);
        if (persona is null) return (null, null);
        // Долгая память — только если включена у персоны
        if (!persona.MemoryEnabled) return (null, persona);
        // team_memory_* (③-3.4, диета памяти команды ч.3) — по проекту ТЕКУЩЕГО чата, не по
        // scope персоны: состав MCP-инструментов один и тот же у проектных и глобальных персон
        // (инвариант «tools/list не зависит от хода» — тем более не от того, какая персона),
        // а пишет ли персона в команду — решает бэкенд (ProjectsController.TeamMemoryWriteAllowed:
        // Persona.Scope==Project && Persona.ProjectId==id проекта памяти). Глобальная персона в
        // проектном чате получает team_memory_list/search (read-only), персона другого проекта —
        // так же; вне проектного чата (session.ProjectId пуст) команды памяти нет вообще.
        return (BuildMemoryContext(ownerId, persona.Id, session.ProjectId), persona);
    }

    // Провайдер блока «Привязанные знания и правила» персоны (флаг persona-bindings):
    // на каждый ход перечитывает персону (привязки могли измениться) и собирает
    // индекс + always-выжимки. mountedSections — секции workspace, реально смонтированные
    // этой сессии (типы без своей секции в индекс не попадают). Ошибки → null (ход без блока).
    private Func<string, Task<string?>>? BuildBindingsProvider(string? ownerId, string? personaId,
        IReadOnlyList<string>? mountedSections)
    {
        if (ownerId is null || personaId is null) return null;
        var sections = mountedSections ?? [];
        return async text =>
        {
            try { return await _bindings.BuildTurnBlockAsync(ownerId, personaId, text, sections); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Блок привязок персоны {Persona}", personaId);
                return null;
            }
        };
    }

    // Сброс адаптеров живых сессий персоны (изменился профиль/возможности/привязки):
    // процесс пересоздаётся при следующем сообщении с актуальным контекстом,
    // транскрипт продолжается через --resume (паттерн SetPersona)
    private void InvalidatePersonaSessions(string personaId)
    {
        foreach (var entry in _sessions.Values.Where(e => e.Info.PersonaId == personaId))
            // Ленивая уборка (см. SwitchSpeaker): не рвём активный ход и доживающих агентов
            if (entry.Process is not null) entry.AdapterStale = true;
    }

    // Кандидаты на консультацию: участники группового чата либо доступные в контексте
    // персоны (глобальные + текущего проекта) + кросс-проектные ProjectPersonas-привязки
    // персоны САМОГО ЧАТА (persona) — команда/точечные персоны другого проекта; без персоны
    // самого чата. В групповом чате extra-персоны тоже примешиваются: остаются
    // консультантами через persona_ask (в MentionsHint, не участники/спикеры).
    private List<Persona> ResolveOtherPersonas(string ownerId, string? projectId, Session session, Persona? persona = null)
    {
        var isGroup = session.Participants is { Count: > 1 };
        var result = (isGroup
                ? session.Participants!.Select(id => _personas.Get(id, ownerId)).OfType<Persona>()
                : _personas.GetForContext(ownerId, projectId))
            .Where(p => p.Id != session.PersonaId)
            .ToList();

        if (persona is not null)
        {
            var seen = result.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var (extProjectId, extPersonaId) in _bindings.BuildExternalPersonaScopes(ownerId, persona))
            {
                IEnumerable<Persona> toAdd;
                if (extPersonaId is not null)
                    toAdd = _personas.Get(extPersonaId, ownerId) is { } single
                        ? new[] { single } : Array.Empty<Persona>();
                else
                    toAdd = _personas.GetByOwner(ownerId).Where(p =>
                        p.Scope == PersonaScope.Project && p.ProjectId == extProjectId);
                foreach (var p in toAdd)
                {
                    if (p.Id == session.PersonaId || !seen.Add(p.Id)) continue;
                    result.Add(p);
                }
            }
        }
        return result;
    }

    // Разделение персон по способу консультации: с файлом сабагента — через встроенный
    // Task; остальные (зарезервированный handle, за капом файлов) — через persona_ask.
    // Провайдер модели персоны роли НЕ играет: файлы сабагентов пинят максимум алиас-тир
    // Claude (opus/sonnet/haiku, см. PersonaAgentFileSync.ModelAliasFor) — он резолвится
    // у любого провайдера, кросс-провайдерная персона остаётся запускаемой.
    private (List<Persona> Subagents, List<Persona> ViaAsk) SplitConsultants(
        string ownerId, Session session, List<Persona> personas)
    {
        if (_agentSync is null)
            return ([], personas);
        var eligible = _agentSync.EligiblePersonas(ownerId).Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);
        var subagents = new List<Persona>();
        var viaAsk = new List<Persona>();
        foreach (var p in personas)
        {
            // Файл сабагента персоны физически лежит в ЭТОМ проекте (Project-персона своего
            // проекта) либо во всех проектах (Global) — см. PersonaAgentFileSync. Персона
            // чужого проекта (видна здесь только через ProjectPersonas-привязку) файла тут не
            // имеет — Task(agentType=handle) её не найдёт, консультация только через persona_ask.
            var reachable = p.Scope == PersonaScope.Global || p.ProjectId == session.ProjectId;
            if (reachable && eligible.Contains(p.Id) && !PersonaAgentFileSync.IsReserved(p.Handle))
                subagents.Add(p);
            else
                viaAsk.Add(p);
        }
        return (subagents, viaAsk);
    }

    // Решение «даём ли персоне консультантов» (сабагенты .md + их pmem-серверы + подсказка
    // с persona_ask): ключ tool:consultants. В ГРУППОВОМ чате ключ ИГНОРИРУЕТСЯ — спикер
    // обязан уметь спросить коллег по чату (BuildGroupChatHint прямо отсылает к этому блоку),
    // иначе групповой чат ломается по замыслу. Решение зависит только от персоны и состава
    // чата — детерминировано на сессию, состав tools/list от хода не зависит.
    // internal: ту же формулу резолвит PersonasToolset по живой сессии-вызывателю (http,
    // ADR-012 волна 2) — право на сервер проверяется на каждый tools/list и tools/call,
    // а не только при построении контекста адаптера
    internal bool ConsultantsEnabled(string? ownerId, Session session, Persona? persona) =>
        session.Participants is { Count: > 1 }
        || _bindings.ServerToolEnabled(ownerId, persona, "consultants");

    // Решение «даём ли персоне сервер персон» (CRUD + persona_ask): ключ tool:personas.
    // В ГРУППОВОМ чате ключ ИГНОРИРУЕТСЯ по той же причине, что и tool:consultants —
    // BuildGroupChatHint безусловно отсылает к блоку о консультациях (MentionsHint из
    // этого же сервера), Off-привязка сняла бы сервер и подсказка стала бы враньём.
    // internal — по той же причине, что ConsultantsEnabled: PersonasToolset проверяет
    // право на сервер персон по живой сессии на каждый вызов (http, ADR-012 волна 2)
    internal bool PersonasEnabled(string? ownerId, Session session, Persona? persona) =>
        session.Participants is { Count: > 1 }
        || _bindings.ServerToolEnabled(ownerId, persona, "personas");

    // Решение «в составе ли mentions-группа (persona_ask)» — ЕДИНАЯ формула для tools/list
    // тулсета (живой резолв по сессии-вызывателю) и отпечатка сигнатуры запуска CLI (shape
    // через PersonasMcpContext.MentionsToolsEnabled). Две формулы расходились при единственной
    // персоне владельца: MentionsHint обнулялся (спрашивать некого), а инструмент оставался —
    // shape говорил m0, tools/list отдавал persona_ask, и любой из переходов бил по запуску
    // (блокер приёмки волны 2.1). НЕ «MentionsHint != null»: подсказка — про текст промпта,
    // инструмент — про состав, у них разные условия.
    internal bool MentionsToolsEnabled(string? ownerId, Session session, Persona? persona) =>
        PersonasEnabled(ownerId, session, persona) && ConsultantsEnabled(ownerId, session, persona);

    // План файловых сабагентов-персон на ход: папки для --add-dir + pmem-серверы памяти
    // видимых персон. Замыкание вычисляется на каждый ход (актуальные персоны и модель
    // сессии); внутри — троттлёный reconcile файлов. Ошибки → null (ход идёт без
    // консультантов, persona_ask остаётся).
    private Func<PersonaAgentsContext?>? BuildPersonaAgentsProvider(string? ownerId, Session session, Persona? persona)
    {
        if (ownerId is null || _agentSync is null) return null;
        // Off-привязка tool:consultants убирает и pmem-серверы, и --add-dir с .md-агентами
        // (подсказка про Workflow отпадает следом — она условна от AgentHandles)
        if (!ConsultantsEnabled(ownerId, session, persona)) return null;
        return () =>
        {
            try
            {
                var projectId = session.ProjectId;
                var addDirs = _agentSync.GetAddDirs(ownerId, session.Model, projectId);
                // pmem — для ВСЕХ видимых персон с памятью (включая персону самого чата):
                // файлы в add-dir видны все, а Task(agentType=handle) может позвать любую.
                // С stdio объявление стоило процессом node на каждого консультанта (CLI
                // поднимает все stdio-серверы конфига на старте, alwaysLoad на это не
                // влияет — см. docs/architecture/mcp-servers.md); на http-транспорте
                // (ADR-012, фаза 2) все pmem_* живут в Kestrel одним тулсетом — процессов
                // нет, сколько бы персон ни было смонтировано. Сузишь список — сузишь и
                // круг персон, которых можно позвать сабагентом.
                var (subagents, _) = SplitConsultants(ownerId, session,
                    _personas.GetForContext(ownerId, projectId).ToList());
                var apiUrl = ResolveTasksApiUrl(ownerId);
                var tokenFactory = () => GetServiceToken(ownerId);
                var useHttp = HttpEndpointUsable(apiUrl);
                // ProjectId консультанта — проект ТЕКУЩЕГО чата (как у BuildPersonaLayer выше), не
                // scope самого консультанта: приглашённая в проектный workflow глобальная персона
                // тоже должна видеть team_memory_list/search этого проекта (read-only — пишет только
                // персона САМОГО проекта, гейт в TeamMemoryService.WriteDeniedFor).
                var servers = subagents
                    .Where(p => p.MemoryEnabled)
                    .Select(p => new ConsultantMemoryServer(
                        PersonaConsultantToolset.PmemServerKey(p.Handle),
                        apiUrl, tokenFactory, p.Id, projectId, useHttp))
                    .ToList();
                var handles = subagents.Select(p => p.Handle).Where(h => !string.IsNullOrWhiteSpace(h)).ToList()!;
                return new PersonaAgentsContext(addDirs, servers, handles);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "План сабагентов-персон не собрался — ход без файловых консультантов");
                return null;
            }
        };
    }

    // Блок-подсказка о консультациях: две группы — сабагенты (Task) и persona_ask
    private string? BuildMentionsHint(string ownerId, Session session, List<Persona> others)
    {
        if (others.Count == 0) return null;
        var (subagents, viaAsk) = SplitConsultants(ownerId, session, others);
        var sb = new System.Text.StringBuilder();
        if (subagents.Count > 0)
        {
            sb.AppendLine("Персоны-консультанты доступны как сабагенты: вызывай встроенный инструмент " +
                "Task с subagent_type=\"<handle>\" и самодостаточным вопросом в prompt — персона не видит " +
                "этот разговор; она сама прочитает нужные файлы, заметки и свою память и ответит от " +
                "своего лица. Не вызывай сабагента со своим собственным handle — отвечай сам. " +
                "Инструменты mcp__pmem_* НЕ вызывай напрямую — это личная память персон-консультантов, " +
                "она доступна только им самим; чтобы узнать, что персона помнит, спроси её через Task. " +
                "Когда пользователь упоминает персону через @handle — обязательно обратись к ней " +
                "и учти её ответ. В мультиагентных обсуждениях (workflow /panel-of-experts) по умолчанию " +
                "распределяй роли панели между этими персонами: передавай их handle в args.participants " +
                "(роли по порядку: Генератор, Критик, Адвокат, Модератор), подбирая персону под роль " +
                "по её характеру. Доступные консультанты:");
            AppendPersonaLines(sb, subagents, session.ProjectId);
        }
        if (viaAsk.Count > 0)
        {
            if (subagents.Count > 0)
                sb.AppendLine("Эти персоны доступны только инструментом persona_ask (параметры: handle, " +
                    "question, context?) — ответ придёт от их лица, с их характером и памятью:");
            else
                sb.AppendLine("Любую персону можно спросить инструментом persona_ask (параметры: handle, " +
                    "question, context?) — она ответит от своего лица, со своим характером и памятью. " +
                    "Когда пользователь упоминает персону через @handle — обязательно обратись к ней " +
                    "через persona_ask и учти её ответ. Вопрос формулируй самодостаточно: персона не " +
                    "видит этот разговор. Если вызов вернул «No such tool available» — сервер персон ещё " +
                    "подключается: подожди мгновение и повтори тот же вызов. Доступные собеседники:");
            AppendPersonaLines(sb, viaAsk, session.ProjectId);
        }
        return sb.ToString().TrimEnd();
    }

    // currentProjectId — ProjectId сессии: персона Project-скоупа ДРУГОГО проекта (видна здесь
    // только через кросс-проектную ProjectPersonas-привязку) помечается «[проект «Имя»]», чтобы
    // не путать тёзок из разных команд.
    private void AppendPersonaLines(System.Text.StringBuilder sb, List<Persona> personas, string? currentProjectId)
    {
        foreach (var p in personas)
        {
            var title = string.IsNullOrWhiteSpace(p.Role) ? p.Name : $"{p.Role} ({p.Name})";
            sb.Append($"- @{p.Handle} — {title}");
            if (p.Scope == PersonaScope.Project && p.ProjectId != currentProjectId
                && _projects.GetById(p.ProjectId!) is { } foreignProject)
                sb.Append($" [проект «{foreignProject.Name}»]");
            if (!string.IsNullOrWhiteSpace(p.Description)) sb.Append($": {p.Description.Trim()}");
            sb.AppendLine();
        }
    }

    // Контекст MCP-сервера персон: CRUD персон из любого чата (за флагом personas);
    // при включённом persona-mentions и наличии других персон в контексте — плюс
    // @упоминания: MentionsHint (блок «@handle — Роль (Имя)» для промпта) и persona_ask.
    // В групповом чате mentions-режим (persona_ask + подсказка по УЧАСТНИКАМ) включён
    // всегда, независимо от флага persona-mentions — иначе спикер не сможет спросить коллег.
    // persona — для кросс-проектных ProjectPersonas-привязок (доступ к команде ДРУГОГО проекта).
    private PersonasMcpContext? BuildPersonasContext(string? ownerId, string? projectId, Session session, Persona? persona = null)
    {
        if (ownerId is null) return null;
        // Off-привязка tool:personas снимает сервер персон целиком (вместе с CRUD и persona_ask);
        // в групповом чате исключение — см. PersonasEnabled
        if (!PersonasEnabled(ownerId, session, persona)) return null;

        var selfPersonaId = session.PersonaId;
        // @упоминания (persona_ask + подсказка) включены всегда, кроме Off-привязки
        // tool:consultants: без консультаций и подсказки не нужно, и persona_ask
        // выключается вместе с ней (PERSONAS_MENTIONS собирается из MentionsHint).
        var mentionsHint = ConsultantsEnabled(ownerId, session, persona)
            ? BuildMentionsHint(ownerId, session, ResolveOtherPersonas(ownerId, projectId, session, persona))
            : null;

        var externalPersonaScopes = _bindings.BuildExternalPersonaScopes(ownerId, persona);
        var extraProjectIds = externalPersonaScopes.Where(s => s.PersonaId is null)
            .Select(s => s.ProjectId).Distinct().ToList();
        var extraPersonaIds = externalPersonaScopes.Where(s => s.PersonaId is not null)
            .Select(s => s.PersonaId!).Distinct().ToList();

        // Модули сервера персон: manage (CRUD персон) и automation (правила проактивности) —
        // за своими tool-ключами с дефолтом по роли (SectionEnabled → SpecialtySections).
        // Ядро (personas_list/get, привязки, persona_ask) остаётся у всех, у кого сервер включён.
        // Проектный онбординг (знакомство v2, п.5) форсирует manage: шаг команды зовёт
        // personas_ai_team/personas_create, а ведёт сессию скромная личная дефолт-персона,
        // у которой по роли этих инструментов нет. Решение — по СВОЙСТВУ сессии
        // (OnboardingKind пишется при создании и не мутирует), не по ходу: состав tools/list
        // стабилен. Форс не сужается состоянием PresetKey по той же причине. Работает только
        // при включённом сервере персон: Off-привязка tool:personas снимает его целиком,
        // и шаг команды честно проговаривается оверлеем как ограничение.
        var manage = session.OnboardingKind == OnboardingKinds.Project
            || _bindings.SectionEnabled(ownerId, persona, "personas-manage");
        var automation = _bindings.SectionEnabled(ownerId, persona, "personas-automation");

        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new PersonasMcpContext(apiUrl, () => GetServiceToken(ownerId), projectId, selfPersonaId,
            mentionsHint, BindingsEnabled: true,
            extraProjectIds.Count > 0 ? extraProjectIds : null,
            extraPersonaIds.Count > 0 ? extraPersonaIds : null,
            ManageEnabled: manage, AutomationEnabled: automation,
            UseHttp: HttpEndpointUsable(apiUrl),
            MentionsToolsEnabled: MentionsToolsEnabled(ownerId, session, persona));
    }

    // Контекст MCP-сервера рабочего пространства: доступ ко всем проектам владельца
    // (за флагом workspace-tools). Секции сужаются возможностями персоны (единая точка
    // истины — PersonaBindingsService.EffectiveToolEnabled: Tool-привязка приоритетнее
    // Persona.Tools); search остаётся при любом непустом наборе. Секция chats — за
    // отдельным флагом workspace-chat-send, секция destructive (безвозвратное удаление) —
    // за флагом workspace-destructive. Все возможности выключены → сервер не подключаем.
    // Project/ProjectPath-привязки персоны сужают зону (AllowedProjectIds) до привязанных
    // проектов + проекта текущей сессии; БЕЗ таких привязок поведение как у Claude —
    // все проекты владельца (null).
    private WorkspaceMcpContext? BuildWorkspaceContext(string? ownerId, string? projectId,
        string? selfSessionId, Persona? persona)
    {
        if (ownerId is null) return null;
        var plan = BuildWorkspacePlan(ownerId, projectId, persona);
        if (plan is null) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new WorkspaceMcpContext(apiUrl, () => GetServiceToken(ownerId), projectId,
            plan.Sections, plan.AllowedProjectIds, selfSessionId,
            UseHttp: HttpEndpointUsable(apiUrl),
            // context_list (материалы контекста чата) — базовый инструмент wsp-сервера за флагом
            // ВЛАДЕЛЬЦА chat-context: решение принимается по владельцу, а не по ходу и не по
            // непустоте контекста, иначе состав tools/list мерцал бы между ходами.
            ChatContextEnabled: _flags.IsEnabled(ownerId, FeatureFlagKeys.ChatContext));
    }

    /// <summary>
    /// План сервера рабочего пространства: секции инструментов и зона проектов.
    /// ЕДИНАЯ формула состава (ADR-012, волна 3): её зовёт и BuildWorkspaceContext (конфиг
    /// хода и отпечаток shapes), и WorkspaceToolset (живой tools/list по сессии из хвоста
    /// маршрута). Состав и его отпечаток, посчитанные двумя формулами, расходятся — блокер
    /// приёмки волны 2 у personas. null — сервер не подключается (все секции выключены).
    /// </summary>
    internal sealed record WorkspaceMcpPlan(IReadOnlyList<string> Sections,
        IReadOnlyList<string>? AllowedProjectIds);

    internal WorkspaceMcpPlan? BuildWorkspacePlan(string ownerId, string? projectId, Persona? persona)
    {
        var sections = new List<string>();
        foreach (var key in new[] { "projects", "files", "knowledge" })
            if (_bindings.EffectiveToolEnabled(ownerId, persona, key)) sections.Add(key);
        // chats — явный Tool-ключ ИЛИ неявный opt-in через ProjectPersonas-привязки:
        // персона, допущенная к чужому проекту, может писать в его чаты (решение —
        // PersonaBindingsService.ChatsSectionEnabled, там же семантика).
        var chatScopes = _bindings.BuildChatScopes(ownerId, persona);
        var chatsEnabled = _bindings.ChatsSectionEnabled(ownerId, persona);
        if (chatsEnabled) sections.Add("chats");
        // Диагностика решения по chats: набор chats-инструментов обязан быть одинаковым на всех
        // ходах персоны — по этой строке видно, из чего решение сложилось в конкретной сессии
        if (persona is not null)
            _log.LogDebug("Секция chats для персоны {Persona}: {Decision} (tools={Tools}, " +
                "toolBinding={Binding}, chatScopes={Scopes})",
                persona.Handle ?? persona.Id, chatsEnabled ? "on" : "off",
                persona.Tools is null ? "null" : string.Join("|", persona.Tools),
                persona.Bindings?.LastOrDefault(b => b.Type == PersonaBindingType.Tool
                    && string.Equals(b.Target, "chats", StringComparison.OrdinalIgnoreCase))?.Mode
                    .ToString() ?? "нет",
                chatScopes is null ? "null" : string.Join("|", chatScopes));
        if (sections.Count == 0) return null;
        // Git-инструменты (read: status/diff/log; write: stage/commit — секция git_write)
        // и базы знаний Dify владельца (kb_list/search/add_document) —
        // надстройки над базовыми секциями files/knowledge: без базовой секции не монтируются,
        // а внутри неё решает свой tool-ключ (git/kb) с дефолтом по роли персоны
        // (PersonaBindingsService.SectionEnabled — пресет SpecialtySections). Раньше обе ехали
        // с базовой секцией безусловно и стоили контекста персонам, которым не нужны.
        // Секция git приходит двумя ступенями: пресет по роли даёт ЧТЕНИЕ истории
        // (git_status/diff/log), запись (git_stage/git_commit) добавляет только явно
        // включённый ключ git — исполнители коммитят через Bash, а ролям ReadOnly запись
        // не нужна по определению (SectionOrigin, там же данные использования).
        var gitOrigin = _bindings.SectionOrigin(ownerId, persona, "git");
        if (sections.Contains("files") && gitOrigin != SectionSource.Off)
        {
            sections.Add("git");
            if (gitOrigin == SectionSource.Explicit) sections.Add("git_write");
        }
        if (sections.Contains("knowledge") && _bindings.SectionEnabled(ownerId, persona, "kb"))
            sections.Add("knowledge_bases");
        // Разрушающие операции (files_delete/chats_delete) — за отдельным флагом
        // workspace-destructive; персоне дополнительно нужен tool-ключ destructive
        // (Tool-привязка или Persona.Tools). Одна destructive без базовых секций не монтируется.
        // Профиль «Только чтение» строже любых привязок — секцию не монтируем вовсе.
        if (_flags.IsEnabled(ownerId, FeatureFlagKeys.WorkspaceDestructive)
            && persona?.Access != PersonaAccess.ReadOnly
            && _bindings.EffectiveToolEnabled(ownerId, persona, "destructive"))
            sections.Add("destructive");
        // Выкатка прода из чата (ADR-010): секция монтируется только там, где контур реально
        // настроен, и только владельцу-админу — REST всё равно admin-only, а держать три схемы
        // в контексте у всех незачем. Профиль «Только чтение» её не получает: выкатка меняет
        // прод. Все три условия постоянны в рамках сессии (конфиг машины, роль пользователя,
        // профиль персоны), поэтому состав tools/list между ходами не мерцает.
        if (Deploy.DeployOptions.From(_config).Enabled
            && persona?.Access != PersonaAccess.ReadOnly
            && string.Equals(_users.GetById(ownerId)?.Role, "admin", StringComparison.OrdinalIgnoreCase))
            sections.Add("deploy");
        sections.Add("search");

        IReadOnlyList<string>? allowedIds = null;
        var fileScopes = _bindings.BuildFileScopes(ownerId, persona);
        if (fileScopes is { Count: > 0 } || chatScopes is { Count: > 0 })
        {
            // Привязки есть — зона ужимается; проект самой сессии всегда доступен
            var set = new HashSet<string>(fileScopes ?? []);
            if (chatScopes is { Count: > 0 })
                foreach (var id in chatScopes) set.Add(id);
            if (projectId is not null) set.Add(projectId);
            allowedIds = set.ToList();
        }

        return new WorkspaceMcpPlan(sections, allowedIds);
    }

    // Контекст MCP-серверов внешних модулей (контракт §6, ТЗ R7): по каждому модулю
    // с mcp[] в манифесте и включённым у владельца флагом module-{id} (R8) — записи
    // серверов с адресом ЧЕРЕЗ gateway ядра и фабрикой свежего токена chan=mcp (TTL 60 мин;
    // ход длиннее часа получит 401 на инструментах модуля — по контракту это корректно).
    // args манифеста резолвятся от каталога модуля. null — модулей нет/все скрыты.
    private ModulesMcpContext? BuildModulesContext(string? ownerId)
    {
        if (ownerId is null || _modules is null || _moduleTokens is null) return null;
        var user = _users.GetById(ownerId);
        if (user is null) return null;
        var apiBase = ResolveTasksApiUrl(ownerId);
        var servers = new List<ModuleMcpServer>();
        foreach (var module in _modules.All)
        {
            if (module.Manifest.Mcp is not { Count: > 0 } mcpList) continue;
            if (!_flags.IsEnabled(ownerId, module.FeatureFlagKey)) continue;
            var moduleRef = module;
            foreach (var mcp in mcpList)
            {
                var args = (mcp.Args ?? [])
                    .Select(a => Path.IsPathRooted(a) ? a : Path.GetFullPath(Path.Combine(module.ModuleDir, a)))
                    .ToList();
                servers.Add(new ModuleMcpServer(
                    mcp.Key, mcp.Command, args, module.Id,
                    $"{apiBase}{module.Manifest.Backend!.RoutePrefix}",
                    () => _moduleTokens.Issue(moduleRef, user.Id, user.DisplayName ?? user.Username, "mcp")));
            }
        }
        return servers.Count > 0 ? new ModulesMcpContext(servers) : null;
    }

    // Живой состав контекста чата для подсказки хода (фича chat-context): читаем сессию из
    // реестра на КАЖДЫЙ вызов — материал, добавленный кнопкой в идущем разговоре, обязан
    // попасть в промпт следующего хода без пересоздания адаптера. Снимок здесь был бы
    // тихой ложью: вкладки у человека есть, а модель о них не знает.
    // На СОСТАВ инструментов не влияет — только на текст подсказки (гейт самого инструмента
    // живёт в BuildWorkspaceContext и решается флагом владельца).
    public Func<IReadOnlyList<SessionContextEntry>> BuildChatContextProvider(string sessionId) =>
        () => _sessions.TryGetValue(sessionId, out var entry)
            ? entry.Info.Context
            : [];

    // Серверы личного реестра владельца в ход. Решение принимается ТОЛЬКО по
    // owner/project/persona — состав tools/list не смеет зависеть от свойств хода
    // (иначе сигнатура запуска мерцает и процесс CLI перезапускается со всеми MCP).
    // Провайдер, а не готовое значение: правка реестра применяется со следующего хода
    // без пересоздания адаптера. Секреты разворачиваются здесь и живут только во
    // временном конфиге хода.
    private Func<ExternalMcpContext?>? BuildExternalMcpProvider(string? ownerId, string? projectId, Persona? persona)
    {
        if (ownerId is null || _mcpRegistry is null || _mcpSecrets is null) return null;
        // Профиль «Только чтение»: имён инструментов чужого сервера мы не знаем, а гасить их
        // deny-правилами нельзя — список живёт на стороне сервера и меняется, а неизвестное имя
        // в правиле роняет запуск CLI (см. историю MultiEdit в PersonaAccessPolicy). Поэтому
        // решение принимается ЦЕЛИКОМ по серверу: такая персона получает только записи с явным
        // разрешением AllowReadOnlyPersonas. Свойство персоны, не хода — состав не мерцает.
        var readOnly = persona?.Access == PersonaAccess.ReadOnly;
        var registry = _mcpRegistry;
        var secretStore = _mcpSecrets;
        return () =>
        {
            try
            {
                var servers = new List<ExternalMcpServer>();
                // Настройки проекта читаем на каждый ход — правка настроек проекта
                // применяется со следующего хода, без пересоздания адаптера.
                // Чат вне проекта каскад проекта пропускает.
                var project = projectId is null ? null : _projects.GetById(projectId);
                var onInProject = project?.McpServersOn;     // allow-модель доступа
                var isProjectChat = projectId is not null;
                foreach (var record in registry.GetByOwner(ownerId))
                {
                    if (!record.Enabled) continue;
                    // Встроенные интеграции (IntegrationKeys: dify/fal-ai/glif/higgsfield)
                    // доставляются собственной веткой (TryAddHiggsfieldBuiltin) по рубильнику
                    // записи и RO-гейту. Реестровый путь для них НЕ применяется: иначе у
                    // доставки две точки истины — higgsfield, лежащий в реестре и включённый
                    // в проекте (или выданный персоне), доедет мимо продуктовой формулы, да
                    // ещё дублем. Сейчас записи dify/fal-ai/glif в реестре не заводятся
                    // (живут как HTTP-узлы Kestrel), условие держим общим — защита от случайного
                    // возврата в реестр.
                    if (Mcp.McpRegistry.IntegrationKeys.Contains(record.Key, StringComparer.OrdinalIgnoreCase)) continue;
                    // allow-модель: сервер едет, если включён «здесь» (проект этого чата
                    // по McpServersOn либо, вне проектов, AllowOutsideProjects записи) ИЛИ
                    // выдан персоне (McpServerGranted). Чистое условие — McpDelivery.ShouldDeliver,
                    // его OR-матрицу гоняем юнитами без SessionManager. Все входы — свойства
                    // owner/project/persona/записи, ни один не смотрит на ход.
                    var granted = _bindings.McpServerGranted(persona, "mcp:" + record.Key);
                    if (!Mcp.McpDelivery.ShouldDeliver(record, onInProject, isProjectChat, granted, readOnly))
                        continue;
                    var stdio = record.Transport == McpTransport.Stdio;
                    // OAuth: токен, доживающий последние секунды, обновляем ДО сборки конфига —
                    // заголовок запекается на старте CLI и живому процессу уже не доедет.
                    // null — вход протух и не восстановился: сервер снимается с хода, а статус
                    // «нужен вход» уже записан (молча ронять инструменты в 401 нельзя)
                    var fresh = record.Auth.Kind == McpAuthKind.OAuth2 && _mcpOAuth is not null
                        ? _mcpOAuth.EnsureFresh(ownerId, record)
                        : record;
                    if (fresh is null)
                    {
                        _log.LogWarning("MCP-сервер «{Key}» снят с хода: нужен вход (OAuth)", record.Key);
                        continue;
                    }
                    var env = ResolveSecretValues(ownerId, fresh.Env);
                    var headers = ResolveSecretValues(ownerId, fresh.Headers);
                    if (!stdio && !TryApplyAuthHeaders(ownerId, fresh, headers)) continue;
                    servers.Add(new ExternalMcpServer(
                        fresh.Key,
                        fresh.Transport.ToString().ToLowerInvariant(),
                        stdio ? fresh.Command : null,
                        fresh.Args ?? [],
                        env,
                        stdio ? null : fresh.Url,
                        headers,
                        fresh.AlwaysLoad,
                        fresh.AuthVersion));
                }

                return servers.Count > 0 ? new ExternalMcpContext(servers) : null;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Реестр MCP-серверов не собрался — ход без своих серверов");
                return null;
            }
        };
    }

    // Локальные обёртки вокруг Mcp.McpAuthHeaders / секрет-стора — нужны в лямбде
    // BuildExternalMcpProvider, поэтому живут на классе. Поведение и сообщения
    // логов совпадают с теми, что были внутри лямбды.
    private Dictionary<string, string> ResolveSecretValues(string ownerId, Dictionary<string, string>? map)
    {
        if (_mcpSecrets is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in map ?? [])
            result[name] = _mcpSecrets.Resolve(ownerId, value) ?? "";
        return result;
    }

    private bool TryApplyAuthHeaders(string ownerId, McpServerRecord record, Dictionary<string, string> headers)
    {
        if (Mcp.McpAuthHeaders.TryApply(record, headers, r => _mcpSecrets?.Resolve(ownerId, r))) return true;
        // Заголовок авторизации http/sse-сервера (общая точка с пробой — Mcp.McpAuthHeaders).
        // Потерянный секрет (запись ссылается в пустоту) — не повод отдавать серверу заведомо
        // анонимный запрос: пропускаем сервер с предупреждением, иначе инструменты молча
        // отвечали бы 401.
        _log.LogWarning("MCP-сервер «{Key}» снят с хода: не найдено значение авторизации", record.Key);
        return false;
    }

    // Контекст MCP-сервера уведомлений: обычному чату — всегда, персоне — по роли
    // (модуль автоматизации) либо по явной привязке tool:notifications; Off-привязка
    // выключает в любом случае. Единая точка решения — PersonaBindingsService.NotificationsEnabled
    // (по ПЕРСОНЕ, не по ходу). Тот же сервисный токен, что у tasks/notes/workspace.
    private NotificationsMcpContext? BuildNotificationsContext(string? ownerId, string? personaId, Persona? persona)
    {
        if (ownerId is null) return null;
        if (!_bindings.NotificationsEnabled(ownerId, persona)) return null;
        var apiUrl = ResolveTasksApiUrl(ownerId);
        return new NotificationsMcpContext(apiUrl, () => GetServiceToken(ownerId), personaId,
            UseHttp: HttpEndpointUsable(apiUrl));
    }

    // Дополнительные запреты сессии персоны: профиль доступа (PersonaAccessPolicy — «пол»
    // запретов: ReadOnly/Custom) + capability-решение «web» через привязки
    // (EffectiveToolEnabled: Tool-привязка приоритетнее Persona.Tools). Web-решение передаём
    // в policy параметром, чтобы не дублировать логику; запреты складываются — побеждает
    // более строгий (ReadOnly режет мутации, даже если binding разрешил инструмент).
    // Сессия нужна для правила «координатор не пишет код сам» (режим «Командная реализация»):
    // у чата-штаба поверх прав персоны запрещены инструменты правки файлов — работа идёт
    // задачами на исполнителей, а не руками координатора.
    private IReadOnlyList<string>? BuildExtraDisallowed(string? ownerId, Persona? persona, Session session)
    {
        var byPersona = PersonaAccessPolicy.BuildExtraDisallowed(persona,
            webAllowed: _bindings.EffectiveToolEnabled(ownerId, persona, "web"));
        if (session.TeamImplement is not { } team) return byPersona;
        // Режим включён — ExitPlanMode запрещён всегда (Э8): на стадиях интервью и планирования
        // штаб сидит в план-режиме, и штатная карточка plan_review дала бы второе согласование
        // поверх командной карточки плана. Правки файлов режет отдельная настройка.
        var extra = new List<string>(byPersona ?? []);
        extra.AddRange(TeamImplementPrompts.ModeDisallowed);
        if (team.CoordinatorNoCode) extra.AddRange(TeamImplementPrompts.CoordinatorDisallowed);
        return extra;
    }


    // Назначить/сменить собеседника чату (единый селектор): персону (personaId) ИЛИ
    // стандартного .md-агента Claude (agentName) — взаимоисключающе. Оба пустые = снять.
    // Разрешено и ПО ХОДУ разговора: персона-слой строится на каждый ход, транскрипт
    // продолжается через --resume с новым системным слоем. Модель/усилие подтягиваются
    // из персоны; у начатой сессии — только при том же провайдере: смена собеседника
    // транскрипт не перевозит (в отличие от явной смены модели), а уводить ход в чужой
    // профиль CLI молча нельзя — модель персоны просто не применяется.
    public Session? SetPersona(string sessionId, string ownerId, string? personaId, string? agentName = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (ResolveOwnerId(entry.Info) != ownerId) return null;

        Persona? persona = null;
        if (!string.IsNullOrEmpty(personaId))
        {
            persona = _personas.Get(personaId, ownerId)
                ?? throw new KeyNotFoundException("Персона не найдена");
        }

        SwitchSpeaker(entry, persona, agentName);
        return entry.Info;
    }

    // Запомнить персону, созданную в ходе онбординг-сессии (через personas_create из чата
    // мастера/наставника). Финализация (FinalizeOnboardingAsync) досевает профиль дефолта
    // только ей: выбранная существующая персона прав не получает.
    public void SetOnboardingCreatedPersona(string sessionId, string ownerId, string personaId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (ResolveOwnerId(entry.Info) != ownerId) return;
        if (entry.Info.OnboardingCreatedPersonaId == personaId) return;
        entry.Info.OnboardingCreatedPersonaId = personaId;
        SaveSessions();
    }

    // Пометить онбординг-сессию финализированной (PersonasController.FinalizeOnboardingAsync):
    // повторный make-default из живой сессии после этого — no-op без второго события в ленте.
    public void SetOnboardingFinalized(string sessionId, string ownerId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (ResolveOwnerId(entry.Info) != ownerId) return;
        if (entry.Info.OnboardingFinalized) return;
        entry.Info.OnboardingFinalized = true;
        SaveSessions();
    }

    // Общее ядро смены собеседника/спикера (SetPersona и роутинг группового чата):
    // PersonaId и модель/усилие персоны (у начатой сессии — только при том же провайдере),
    // флаг PersonaSwitched, сброс адаптера (новый слой подхватится при следующем ходе), Save.
    private void SwitchSpeaker(SessionEntry entry, Persona? persona, string? agentName = null)
    {
        var started = entry.Info.ClaudeSessionId is not null;
        // .md-агент и персона взаимоисключающие: назначение одного сбрасывает другого
        var newAgentName = persona is null && !string.IsNullOrWhiteSpace(agentName)
            ? agentName.Trim()
            : null;
        // Спикер реально сменился, только если изменилась личность собеседника (персона ИЛИ
        // .md-агент). Повторное назначение той же персоны — не смена (иначе PersonaSwitched
        // взводился бы навсегда и в ленту лез разделитель «теперь отвечает чужие прошлые ответы»).
        var switching = started &&
            (entry.Info.PersonaId != persona?.Id || entry.Info.AgentName != newAgentName);

        entry.Info.PersonaId = persona?.Id;
        entry.Info.AgentName = newAgentName;
        if (persona is not null)
        {
            // Модель персоны: своя сильнее её уровня (уровень уже развёрнут в модель по
            // слотам владельца) — дальше по тексту работаем только с этим значением.
            // Владелец — ТОЛЬКО через ResolveOwnerId: у проектной сессии Session.OwnerId
            // равен null (он живёт у проекта), и личные слоты тиров молча подменялись бы
            // глобальными — см. комментарий у GetActiveTurnDelegation
            var personaModel = _assignments.PersonaModel(persona, ResolveOwnerId(entry.Info),
                Llm.LocalActionCatalog.DefaultTierOf(Llm.LocalActionCatalog.ChatPersona));
            if (!started)
            {
                // ?? — а не присваивание в лоб: у персоны без своей модели чат остаётся на той,
                // что уже подставлена при создании (глобальная «модель по умолчанию»).
                // Раньше здесь её затирало в null, и назначение персоны в свежий чат молча
                // возвращало ход к дефолту CLI (заметнее всего в MCP chats_create + personaId)
                entry.Info.Model = personaModel ?? entry.Info.Model;
                entry.Info.Effort = persona.Effort ?? entry.Info.Effort;
            }
            else if (_llmProviders.ProviderKey(personaModel) == _llmProviders.ProviderKey(entry.Info.Model)
                && _subscriptionPool.SupportsModel(entry.Info.Provider ?? ClaudeSubscriptionPool.PrimaryKey, personaModel))
            {
                // Тот же провайдер И подписка сессии способна обслужить модель персоны —
                // модель применяется со следующего хода; иначе оставляем модель сессии
                // (характер всё равно её): пин Opus на аккаунте без Opus валил бы ход
                entry.Info.Model = personaModel ?? entry.Info.Model;
                entry.Info.Effort = persona.Effort ?? entry.Info.Effort;
            }
        }
        if (switching) entry.Info.PersonaSwitched = true;
        entry.Info.UpdatedAt = DateTime.UtcNow;

        // Адаптер несёт контекст прежнего собеседника (memory-MCP, привязки) — пометим
        // устаревшим. Уборка ЛЕНИВАЯ (EnsureProcessAsync перед следующим ходом), а не
        // немедленная: мгновенный dispose обрывал бы активный ход и убивал доживающих
        // фоновых агентов молча
        if (entry.Process is not null) entry.AdapterStale = true;
        SaveSessions();
    }

    // Роутинг спикера группового чата перед ходом: @упоминание участника переключает
    // активного спикера (SwitchSpeaker + speaker_changed клиентам). Во время активного
    // хода (Working/Waiting) состав не трогаем — переключение подействует со следующего.
    private async Task RouteGroupSpeakerAsync(string sessionId, SessionEntry entry, string text)
    {
        if (entry.Info.Participants is not { Count: > 1 } participantIds) return;
        if (entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting) return;
        var ownerId = ResolveOwnerId(entry.Info);
        if (ownerId is null) return;

        var participants = participantIds
            .Select(id => _personas.Get(id, ownerId))
            .OfType<Persona>()
            .ToList();
        if (participants.Count == 0) return;

        var route = GroupChatRouter.Resolve(text, participants, entry.Info.PersonaId);
        if (!route.Switched) return;

        var speaker = participants.First(p => p.Id == route.SpeakerPersonaId);
        SwitchSpeaker(entry, speaker);

        var label = string.IsNullOrWhiteSpace(speaker.Role) ? speaker.Name : $"{speaker.Role} ({speaker.Name})";
        // Только в session-группу: клиент открытого чата состоит и в user_/project_-группе,
        // рассылка в обе дублировала разделитель «Теперь отвечает» в ленте
        await BroadcastAsync(sessionId, new SpeakerChangedMessage(speaker.Id, label));
    }

    // Общий запуск новой сессии: аккумулятор истории, регистрация в реестре, старт процесса claude.
    private async Task StartNewSessionAsync(Session session, string rootPath, string? rawSystemPrompt,
        Func<IReadOnlyList<PermissionRule>>? permissionRules)
    {
        // ClaudeSessionId приходит не только от CLI: параметр resumeSessionId тела запроса
        // (POST /sessions, /chats, /personas/{id}/chats) садится в него как есть. А дальше он
        // становится ИМЕНЕМ ПАПКИ в data/sessions и именем файла транскрипта, которые при
        // удалении чата удаляются рекурсивно — «..» в значении увел бы удаление на всю папку
        // data (projects.json, users.json, история, сторы персон и задач). Единая точка старта
        // любой сессии, поэтому гейт стоит здесь; сообщение уходит клиенту как 400.
        if (session.ClaudeSessionId is { } resumeId && !Llm.TranscriptMigrator.IsSafeSessionId(resumeId))
            throw new InvalidOperationException(
                "Недопустимый resumeSessionId: разрешены только буквы, цифры, дефис и подчеркивание");

        var existingHistory = session.ClaudeSessionId != null
            ? await _history.LoadAsync(session.ClaudeSessionId)
            : [];
        var accumulator = new TurnAccumulator(existingHistory, session.ClaudeSessionId);

        var entry = new SessionEntry { Info = session, Accumulator = accumulator };
        _sessions[session.Id] = entry;

        var ownerId = ResolveOwnerId(session);

        // Персона: её характер инжектится в системный промпт.
        // Scope контекста уже задан типом сессии (глобальная персона → чат без проекта →
        // доступ ко всем данным владельца; проектная → сессия проекта → только он).
        var persona = BuildPersonaLayer(session, ownerId);
        var workspace = BuildWorkspaceContext(ownerId, session.ProjectId, session.Id, persona.Persona);

        // Чат в отдельном worktree: рабочая папка сессии — его дерево, не корень проекта
        // (корень запоминаем — он fallback для slice графа, пока свой граф дерева не построен)
        var projectRoot = rootPath;
        rootPath = EffectiveRoot(session, rootPath);

        // Идентификатор прогона несёт колбэк: по нему OnMessageAsync отличает exited этого
        // прогона от позднего exited доживающего (см. SessionEntry.DrainOnExitedRun)
        var runId = Interlocked.Increment(ref _runSeq);

        var widgetsMcp = BuildWidgetsContext(ownerId, persona.Persona);
        var watchMcp = BuildWatchContext(ownerId);
        var webSearchMcp = BuildWebSearchContext(ownerId, persona.Persona);
        var higgsfieldMcp = BuildHiggsfieldContext(ownerId, persona.Persona);
        var localMediaMcp = BuildLocalMediaContext(ownerId, session.ProjectId, persona.Persona);
        var memoryMcp = persona.Memory ?? BuildTeamMemoryContext(ownerId, session.ProjectId);
        var tasksMcp = TasksMcpEnabled(ownerId, session, persona.Persona)
            ? BuildTasksContext(ownerId, session.ProjectId, persona.Persona) : null;
        var notesMcp = _bindings.EffectiveToolEnabled(ownerId, persona.Persona, "notes")
            ? BuildNotesContext(ownerId, session.ProjectId, persona.Persona) : null;
        var personasMcp = BuildPersonasContext(ownerId, session.ProjectId, session, persona.Persona);
        var notificationsMcp = BuildNotificationsContext(ownerId, session.PersonaId, persona.Persona);
        var codeGraphMcp = BuildCodeGraphContext(ownerId, session.ProjectId, session.Id, rootPath, persona.Persona);
        var difyMcp = BuildDifyContext(ownerId);
        var adapter = _adapters.Create(session, new LlmSessionContext(rootPath,
            msg => OnMessageAsync(session.Id, accumulator, msg, runId),
            rawSystemPrompt, ProjectManager.BuiltInSystemPrompt, permissionRules,

            ContentRootPath: AppContext.BaseDirectory,
            TasksMcp: tasksMcp,
            NotesMcp: notesMcp,
            PersonaProvider: BuildPersonaProvider(session, ownerId),
            MemoryMcp: memoryMcp,
            ExtraDisallowedTools: BuildExtraDisallowed(ownerId, persona.Persona, session),
            PersonasMcp: personasMcp,
            NotificationsMcp: notificationsMcp,
            WorkspaceMcp: workspace,
            PersonaAgentsProvider: BuildPersonaAgentsProvider(ownerId, session, persona.Persona),
            // Проектный чат — среда проекта (ADR-016: локальный проект исполняется на устройстве)
            Launcher: session.ProjectId is { } launchProjectId && _projects.GetById(launchProjectId) is { } launchProject
                ? _launchers.ForProject(launchProject)
                : _launchers.ForOwner(ownerId),
            ModulesMcp: BuildModulesContext(ownerId),
            WidgetsMcp: widgetsMcp,
            CodeGraphMcp: codeGraphMcp,
            DifyMcp: difyMcp,
            DesktopMcp: BuildDesktopContext(ownerId, session, persona.Persona),
            BrowserEnabled: BrowserEnabled(ownerId, persona.Persona),
            CliConfigRoot: ConfigRootFor(ownerId, session.Provider),
            ExternalMcpProvider: BuildExternalMcpProvider(ownerId, session.ProjectId, persona.Persona),
            PersistSessions: SaveSessions,
            EnqueueBypass: BuildEnqueueBypass(session.Id),
            OrchestrationDone: BuildOrchestrationDone(session.Id),
            HttpMcpActive: HttpMcpActive(widgetsMcp, memoryMcp, tasksMcp, notesMcp, personasMcp,
                workspace, notificationsMcp, codeGraphMcp, difyMcp, watchMcp, webSearchMcp, higgsfieldMcp,
                localMediaMcp),
            HttpMcpEnabledProvider: HttpMcpEnabled,
            // Материалы контекста — только у проектных чатов (адреса file/task живут внутри
            // проекта); во внепроектной ветке восстановления провайдер не передаётся вовсе
            ChatContextProvider: session.ProjectId is not null ? BuildChatContextProvider(session.Id) : null,
            Events: _turnEvents,
            WatchMcp: watchMcp,
            WebSearchMcp: webSearchMcp,
            HiggsfieldMcp: higgsfieldMcp,
            LocalMediaMcp: localMediaMcp,
            // Корень ГЛАВНОЙ ветки проекта — fallback для slice графа кода, пока свой граф
            // worktree-ветки не построен (ADR-003). У не-worktree чата совпадает с rootPath,
            // fallback сводится к no-op в CodeGraphPromptProvider.GetSliceAsync.
            MainRootPath: projectRoot,
            ServerContent: ServerContentFor(session.ProjectId),
            TranscriptOnServer: TranscriptOnServer(session)));
        entry.Process = adapter;
        entry.RunId = runId;

        await adapter.StartAsync();
        SaveSessions();
    }

    // Приём сообщения от пользователя (Hub SendMessage) и серверных отправок (авто-ходы).
    // Пользовательское сообщение в занятом чате встаёт в видимую серверную очередь
    // (pending_messages) и ЖДЁТ штатного конца хода — доставку разбирает drain по result.
    // Идущий ход при этом НЕ убивается (поведение claude CLI: отправка ≠ остановка): kill
    // посреди хода выбрасывал сделанную работу и оплаченные токены, оставлял tool_use без
    // tool_result в транскрипте и рваные правки в проекте, а пользователь в 9 случаях из 10
    // дописывает уточнение, а не просит остановиться. Для «перебить сейчас» есть явные
    // действия: кнопка «Стоп» и PreemptForPending (кнопка на карточке очереди).
    // Возвращаемый исход (Started/Queued) говорит клиенту, рисовать ли оптимистичный баллон.
    public async Task<SendUserOutcome> SendMessageAsync(string sessionId, string text, IReadOnlyList<string> attachedPaths, string? mode = null, bool systemDirective = false, bool auto = false, string? senderPersonaId = null, bool suppressTasksExecute = false, string? senderOrigin = null, string? senderConnectionId = null, string? staffNote = null, DeliveryCause cause = DeliveryCause.Unknown)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");

        if (!auto && !systemDirective)
        {
            // Идёт awaited-ход агента (REST SendMessageAndWaitAsync выставил TurnWaiter): не
            // запускаем параллельный пользовательский ход — иначе его result ошибочно разрезолвил
            // бы чужой ожидатель (agentDepth). Авто-ходы (work-loop/automation) не в счёт: они
            // не выставляют TurnWaiter и идут строго после result предыдущего.
            //
            // Осознанное исключение из «честной очереди»: здесь пользователь получает отказ, а
            // не enqueue + interrupt. Прерывание такого хода отдало бы ждущему агенту огрызок
            // (exited резолвит ожидателя тем, что успело написаться) — а сообщение пользователя
            // всё равно легло бы в очередь. Окно узкое: awaited-ход живёт до таймаута chats_send,
            // после которого ожидатель снимается и следующая попытка идёт обычным путём.
            if (entry.TurnWaiter is not null)
                throw new InvalidOperationException("Сессия занята другим ходом");

            // Человек написал в чат — цепочка автоотчётов оборвана: дальше это уже новый разговор,
            // и накопленная глубина не должна запрещать отчёты по нему
            entry.ReportChainDepth = 0;

            // Режим «Командная реализация» (Э5): вводная человека начинает новую итерацию —
            // бюджет с нуля. Делаем это на приёме сообщения, ДО очереди: иначе вводная,
            // постоявшая в очереди, доехала бы до координатора уже с исчерпанным потолком.
            _teamNotifier.OnHumanInput(sessionId);
            // Э4/M3: ответ на карточку остановки обычным сообщением — равноправная замена
            // её кнопок (спека: «написал, что делать, координатор учёл и пошёл дальше с того
            // же места»). Стадию возвращаем ДО очереди, по тем же правилам, что решение по
            // карточке, — иначе текст человека упирался бы в гейты стадии «ждёт решения».
            await _teamNotifier.OnHumanInputAsync(sessionId);

            // Занятый чат (ход в полёте) ИЛИ активный цикл «до готово»: сообщение встаёт в
            // видимую очередь (pending_messages) и ждёт конца хода — разбор по result
            // (см. drain в OnMessageAsync). Цикл при этом НЕ снимается (решение владельца
            // 2026-08-08): пользовательское сообщение само продолжит цикл как следующую
            // итерацию (между итерациями — форсирует dispatchNow, при живом ходе — по
            // прерыванию, см. ниже).
            var loopActive = entry.Info.WorkLoop is not null;
            // Снимок статуса: ниже он решает, ждать ход или прерывать, а перечитывание дало бы
            // уже статус собственной доставки (см. про Dispatched)
            var statusSnapshot = entry.Info.Status;
            var turnInFlight = statusSnapshot is SessionStatus.Working or SessionStatus.Waiting;
            if (loopActive || turnInFlight)
            {
                // Потолок очереди проверяем ДО побочных эффектов: разморозка очереди на отказе
                // QueueFull не откатывается, и пользователь получил бы исключение при уже
                // нарушенном состоянии. Проверка предварительная — точная (под PendingLock)
                // остаётся в EnqueuePendingAsync, конкурентная постановка между ними лишь
                // вернёт тот же отказ на шаг позже.
                lock (entry.PendingLock)
                {
                    if (entry.Pending.Count >= MaxPendingPerSession)
                        throw new InvalidOperationException(
                            $"В очереди чата уже {MaxPendingPerSession} сообщений — дождитесь, пока она разберётся");
                }

                // Пользователь возобновил разговор — заморозка «Стоп» снимается ДО постановки,
                // иначе форсаж dispatchNow и разбор по концу хода упёрлись бы в QueueFrozen
                entry.QueueFrozen = false;
                var enqueued = await EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin,
                    agentDepth: 0, kind: PendingKind.User, attachedPaths: attachedPaths, mode: mode);
                if (enqueued is SendAndWaitResult.QueueFull f)
                    throw new InvalidOperationException(
                        $"В очереди чата уже {f.Limit} сообщений — дождитесь, пока она разберётся");
                // Ход прерываем ТОЛЬКО там, где ожидание его конца бессмысленно:
                //  • Waiting — ход стоит на запросе разрешения/вопросе к человеку и сам не
                //    закончится никогда; текст вместо ответа на диалог означает, что отвечать
                //    на него не будут, и без прерывания сообщение висело бы в очереди вечно;
                //  • активный цикл «до готово» — сообщение человека становится следующей
                //    итерацией (решение владельца 2026-08-08), ждать конца цикла ему незачем.
                // Обычный Working не трогаем: ход доживает сам, очередь разберёт его result.
                // Занятость и статус — по снимку ДО постановки: свободный между итерациями
                // цикла чат прерывать нечего (доставку форсирует dispatchNow), а перечитывание
                // статуса ЗДЕСЬ увидело бы Working уже своего только что доставленного
                // dispatchNow'ом хода и убило бы его. Тот же ход мог быть доставлен и форсажем
                // самой постановки (Dispatched): сообщение уже в работе — прерывать нечего,
                // иначе убьём собственный ход.
                // Снимок статуса устарел, пока шла постановка: ход, стоявший на карточке
                // разрешения, мог получить ответ из другой вкладки, дойти до result, и штатный
                // drain уже унёс НАШЕ же сообщение в новый ход. Убивать его — потерять
                // доставленное (метка разбора отработает по пустой очереди). Поэтому перед
                // kill сверяемся с текущим состоянием: ждущий ход всё ещё ждёт, а очередь
                // ещё не разобрана. Для цикла «до готово» такой сверки нет — там прерывание
                // не привязано к Waiting и ход в любом случае продолжится нашей итерацией.
                var stillPreemptable = loopActive
                    || (entry.Info.Status is SessionStatus.Waiting && HasPending(entry));
                if (turnInFlight
                    && (loopActive || statusSnapshot is SessionStatus.Waiting)
                    && stillPreemptable
                    && enqueued is not SendAndWaitResult.Queued { Dispatched: true })
                {
                    PreemptTurnForQueue(sessionId, entry, "user-message (preempt хода пользователя)",
                        byUser: cause == DeliveryCause.User);
                    return SendUserOutcome.QueuedPreempted;
                }
                return SendUserOutcome.Queued;
            }

            // Чат свободен, но очередь непуста (заморожена «Стоп» либо процесс умер аварийно):
            // новое сообщение — в КОНЕЦ очереди, в работу берётся голова (FIFO: очередь
            // приоритетнее свежего сообщения), заморозка снимается — это и есть возобновление
            QueuedMessage? head = null;
            lock (entry.PendingLock)
            {
                if (entry.Pending.Count > 0)
                {
                    entry.QueueFrozen = false;
                    head = entry.Pending[0];
                    entry.Pending.RemoveAt(0);
                    // Потолок не пробивается: голова изъята до добавления
                    entry.Pending.Add(new QueuedMessage(Guid.NewGuid().ToString("N"), text, senderPersonaId,
                        senderOrigin, AgentDepth: 0, DateTime.UtcNow, Kind: PendingKind.User,
                        AttachedPaths: attachedPaths, Mode: mode));
                }
            }
            if (head is not null)
            {
                await BroadcastPendingAsync(sessionId, entry);
                await DeliverPendingAsync(sessionId, entry, head);
                return SendUserOutcome.Queued;
            }
        }

        await SendDirectAsync(sessionId, entry, text, attachedPaths, mode, systemDirective, auto,
            senderPersonaId, suppressTasksExecute, senderOrigin, senderConnectionId: senderConnectionId, staffNote: staffNote, cause: cause);
        return SendUserOutcome.Started;
    }

    // Непосредственный запуск хода в процесс (гейты очереди уже пройдены либо не требуются).
    // fromQueue — доставка пользовательского сообщения из очереди: клиент рисовал его
    // призраком, поэтому live-баллон бродкастим так же, как для сервер-инициированных отправок.
    private async Task SendDirectAsync(string sessionId, SessionEntry entry, string text,
        IReadOnlyList<string> attachedPaths, string? mode, bool systemDirective, bool auto,
        string? senderPersonaId, bool suppressTasksExecute, string? senderOrigin, string? senderConnectionId = null,
        bool fromQueue = false, string? staffNote = null, DeliveryCause cause = DeliveryCause.Unknown)
    {
        // ДИАГНОСТИКА повторных доставок (инцидент 2026-08-10): каждая доставка хода в
        // процесс проходит через эту точку. src различает источник — hub (пользователь
        // через SignalR), auto (серверный ход: цикл/автоматизация/доклад исполнителя),
        // fromQueue (доставка пользовательского сообщения из серверной очереди pending).
        // cause — атрибуция callsite внутри auto/fromQueue (drain/WorkLoop/обход байпаса/…):
        // pinpoint'ит источник повторных доставок, прежде неразличимых при пустом origin.
        // Дубли видны как повторные строки с одинаковым/похожим text — pinpoint'ят источник.
        // Фоновая или отложенная доставка в чат локального проекта с офлайн-устройством: хода
        // не будет (канал отказал бы до старта) — сообщение ждёт устройство на самой сессии.
        // Прямой ввод человека и системные директивы цикла сюда не попадают: человек у экрана
        // и получает честный отказ хода, директива без своего цикла смысла не имеет.
        if (!systemDirective && (auto || fromQueue) && DeviceWaitGate(entry) is { } deviceGate)
        {
            await ParkForDeviceAsync(sessionId, entry, deviceGate, new DeviceWaitMessage(
                Guid.NewGuid().ToString("N"), text,
                fromQueue && !auto ? DeviceWaitMessage.RouteUser : DeviceWaitMessage.RouteAuto,
                DateTime.UtcNow, senderPersonaId, senderOrigin,
                SuppressTasksExecute: suppressTasksExecute, StaffNote: staffNote,
                AttachedPaths: attachedPaths.Count > 0 ? attachedPaths : null, Mode: mode));
            return;
        }

        var deliverySrc = fromQueue ? "fromQueue" : auto ? "auto" : "hub";
        var effectiveCause = cause != DeliveryCause.Unknown ? cause : !auto ? DeliveryCause.User : DeliveryCause.Unknown;
        _log.LogInformation("Доставка хода {Session}: src={Src} cause={Cause} origin={Origin} mode={Mode} text=\"{Text}\"",
            sessionId, deliverySrc, effectiveCause, senderOrigin ?? "-", mode ?? "-",
            (text.Length > 60 ? text[..60] : text).Replace('\n', ' '));

        // Режим, выбранный в Composer, применяется со следующего хода: процесс claude
        // пересоздаётся в RunTurnAsync и читает --permission-mode из Info.Mode.
        // Режим «План» у провайдера без поддержки тихо игнорируем (защита от рассинхрона UI).
        var caps = _llmProviders.CapabilitiesFor(entry.Info.Model);
        if (mode is not null && Enum.TryParse<ClaudeMode>(mode, true, out var parsedMode)
            && (parsedMode != ClaudeMode.Plan || caps.SupportsPlanMode))
        {
            // M1: сообщение с mode — та же точка смены режима, что и селектор (SetMode).
            // На стадиях интервью/планирования чат держится в план-режиме (SavedMode), и
            // сообщение его не сбрасывает — молча, отказ здесь стоил бы самого сообщения.
            if (entry.Info.TeamImplement is { SavedMode: not null })
                parsedMode = ClaudeMode.Plan;
            // …а режим, в котором CLI не спрашивает разрешений (acceptEdits/bypass), штабу
            // запрещён в любой точке смены — иначе CoordinatorWriteGuard молчит.
            if (entry.Info.TeamImplement is { } teamForGuard)
                parsedMode = PermissionModeGuard.GuardCompatibleMode(parsedMode, teamForGuard.CoordinatorNoCode);
            if (entry.Info.Mode != parsedMode)
            {
                entry.Info.Mode = parsedMode;
                SaveSessions();
            }
        }

        // M7: помечаем инициатора хода для добавочного авто-подтверждения — ход человека
        // (не авто и не директива) является контрольной точкой, агентский — нет.
        entry.TeamTurnFromHuman = !auto && !systemDirective;

        // Человек вмешался в чат — счётчик добиваний сабагента начинается заново
        // (потолок в две попытки считается на серию подряд, а не на всю жизнь чата)
        if (!auto && !systemDirective)
        {
            entry.SubagentNudges = 0;
            entry.NudgeAgentId = null;
        }

        // Групповой чат: @упоминание участника в тексте переключает активного спикера
        // ДО пересоздания процесса — новый персона-слой применяется уже к этому ходу
        await RouteGroupSpeakerAsync(sessionId, entry, text);

        // Аккаунт чата могли исчерпать другие чаты, пока этот простаивал: перед ходом
        // тихо перевозим его на здоровую подписку пула (та же модель и эндпоинт)
        TryPoolFailover(sessionId, entry);

        // Live-баллон пользовательского сообщения — в session-группу, до тяжёлого старта
        // CLI-процесса. Адресация зависит от того, кто рисовал реплику оптимистично:
        //  • авто-отправка (командный ход, автоматизация, задача) и доставка из очереди
        //    (fromQueue — клиент рисовал призрак, а не баллон): клиент не добавлял её в
        //    ленту сам — рассылаем всем;
        //  • ручной ввод из хаба (senderConnectionId): отправитель уже нарисовал баллон
        //    по исходу 'started' (useSession.send), его соединение исключаем — а остальным
        //    устройствам того же чата реплику отдаём live. Раньше они не получали её вовсе
        //    и видели только ответ «в пустоту» до перезагрузки страницы;
        //  • ручной ввод без соединения (REST/сервисные вызовы): оптимистичного баллона
        //    нет ни у кого — рассылаем всем.
        // ТОЛЬКО в session-группу: клиент открытого чата состоит и в user_/project_-группе,
        // широкая рассылка дублировала сообщение в ленте (см. комментарий у внутриходовых событий).
        if (!systemDirective)
        {
            var userMsg = new UserMessageMessage(text, attachedPaths.Count > 0 ? attachedPaths : null,
                senderPersonaId, auto, senderOrigin, StaffNote: staffNote);
            if (!auto && !fromQueue && senderConnectionId is not null)
                await BroadcastExceptAsync(sessionId, senderConnectionId, userMsg);
            else
                await BroadcastAsync(sessionId, userMsg);
        }

        // Локальный голосовой ход (место chat-voice на «Локальная»): CLI-процесс не нужен,
        // достаточно аккумулятора истории — ответ принесёт RunLocalVoiceTurnAsync. Гейт
        // обязан быть детерминирован на протяжении всего SendDirectAsync (второй вызов
        // ниже, перед диспетчеризацией): все его входы — константы вызова плюс ручной
        // тумблер VoiceMode, между вызовами они не меняются.
        var localVoice = ShouldRunLocalVoice(entry, auto, systemDirective, attachedPaths);
        if (localVoice)
            await EnsureAccumulatorAsync(entry);
        else
            await EnsureProcessAsync(sessionId, entry);

        // Авторство реплик хода: text-сообщения истории получают персону на момент хода
        // (после смены собеседника старые реплики сохраняют прежний аватар)
        entry.Accumulator?.SetPersona(entry.Info.PersonaId);

        // Авто-имя сессии по первому сообщению (Claude в --print не отдаёт title/summary).
        // Только у по-настоящему нового чата: имя ещё пустое, не залочено явным заданием
        // (вручную/MCP/ретайтл) и ход ещё НЕ запускался (ClaudeSessionId == null). Иначе после
        // рестарта сообщение-продолжение («продолжи») перетирало бы уже сложившееся название.
        // Работает и для чатов вне проекта, и для проектных сессий.
        if (string.IsNullOrWhiteSpace(entry.Info.Name) && !entry.Info.NameLocked && entry.Info.ClaudeSessionId is null)
        {
            var title = MakeChatTitle(text);
            if (!string.IsNullOrEmpty(title))
            {
                entry.Info.Name = title;
                SaveSessions();
                // Фоново уточняем заголовок локальной моделью (best-effort, не блокирует ход)
                _ = RefineChatTitleAsync(sessionId, text, title, ResolveOwnerId(entry.Info));
            }
        }

        await ApplyStatusAsync(sessionId, entry, SessionStatus.Working);

        entry.Accumulator?.OnUserMessage(text, attachedPaths, systemDirective: systemDirective, auto: auto, senderPersonaId: senderPersonaId, senderOrigin: senderOrigin, staffNote: staffNote);
        // Сообщение пользователя = начало нового хода в основном дереве (зеркало
        // сброса skippingWorktreeTurn в SessionChangedPaths.Extract)
        entry.TurnInWorktree = false;

        // Push-источники автоматизаций: @упоминание персоны в тексте пользователя.
        // Fire-and-forget — обработчик не должен тормозить ход (он лишь детектит и ставит в очередь).
        if (OnUserMessage is { } userMsgObservers)
        {
            _ = Task.Run(async () =>
            {
                try { await userMsgObservers(entry.Info, text, senderPersonaId); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SessionManager] Ошибка OnUserMessage ({sessionId}): {ex.Message}");
                }
            });
        }
        // Обвязки хода (OmO) дописываются только к тексту для CLI —
        // история и UI хранят исходное сообщение пользователя.
        // Снимок хода человека — для возврата в композер по «Стоп», если очередь пуста;
        // авто/агентские/директивные ходы не восстанавливаются (null)
        entry.CurrentTurnSnapshot = !auto && !systemDirective
            ? new UserTurnSnapshot(text, attachedPaths, mode)
            : null;
        // Превью чата — исходным сообщением, без обвязок BuildCliTurnText (адаптер превью
        // не выставляет: ему текст приходит уже обвязанным и повторяется на каждой попытке
        // фолбэка). Служебные директивы (цикл «до готово», добивание сабагента) превью НЕ
        // трогают вовсе: их сырой текст («[СИСТЕМНАЯ ДИРЕКТИВА — …]») человеку в списке чатов
        // не адресован (MINOR B-п.5) — в карточке остаётся то, что он видел последним.
        if (!systemDirective)
            entry.Info.LastMessage = ChatPreview(text);
        // Диспетчеризация: локальный голосовой ход идёт мимо CLI (fire-and-forget — как
        // CLI-ветка, где SendMessageAsync лишь ставит ход в процесс; ответ приходит
        // событиями через OnMessageAsync). Реплика уже в аккумуляторе (OnUserMessage выше).
        if (localVoice)
        {
            // Отметка активности: у CLI-ветки её ставит адаптер (SendMessageAsync), а
            // локальный ход идёт мимо него — без явной записи архивный чат остался бы в
            // архиве (ApplyStatusAsync его больше не двигает)
            entry.Info.UpdatedAt = DateTime.UtcNow;
            _ = Task.Run(async () =>
            {
                try { await RunLocalVoiceTurnAsync(sessionId, entry); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SessionManager] Локальный голосовой ход ({sessionId}): {ex.Message}");
                }
            });
        }
        else
        {
            await entry.Process!.SendMessageAsync(BuildCliTurnText(entry, text), attachedPaths,
                suppressTasksExecute: suppressTasksExecute);
        }
    }

    // Превью чата для карточки списка: первые 100 символов сообщения
    internal static string ChatPreview(string text) => text.Length > 100 ? text[..100] + "…" : text;

    // Текст хода для CLI: исходное сообщение + обвязки.
    // Протокол цикла «до готово» — пока Session.WorkLoop активен. Своей вставки ultrawork
    // больше нет: слова ultrawork/ulw ловит keyword-detector плагина oh-my-claudecode.
    private string BuildCliTurnText(SessionEntry entry, string text)
    {
        var result = text;

        // Обрыв фонового агента (см. NoteTruncatedBgAgent): CLI объявил его прогон
        // завершённым и отдал координатору последнюю реплику как результат. Пометка едет
        // префиксом ближайшего хода — своего хода на неё не тратим, а координатор обязан
        // знать, что итог по обрывку подводить нельзя. Одноразовая: уехала — снята.
        if (entry.TruncatedBgNote is { } cutBgAgent)
        {
            entry.TruncatedBgNote = null;
            result = SubagentPrompts.TruncatedBgAgent(cutBgAgent) + "\n\n" + result;
        }

        if (entry.Info.WorkLoop is { } loop)
        {
            // Буфер LoopTurnText здесь НЕ чистим: ход ставится в очередь, а стримящийся
            // сейчас ход ещё копит текст — очистка на постановке стирала бы его маркер.
            // Буфер потребляет и чистит ContinueWorkLoopAsync по result хода.
            entry.LoopTurnInFlight = true;
            // Верификационный ход идёт со своей директивой — рабочий протокол не дописываем
            if (loop.Phase != "verifying")
                result += "\n\n" + OmoPrompts.WorkLoopTurn(loop.Promise);
        }

        // Режим «Командная реализация»: штаб на каждом ходу получает правило «любая работа —
        // через задачу» и свою стадию. Без напоминания координатор срезает углы и делает
        // работу сам — гард на инструменты правки его только останавливает, но не направляет.
        if (entry.Info.TeamImplement is { } team)
            result += "\n\n" + TeamImplementPrompts.CoordinatorTurn(team);

        // Магслова oh-my-claudecode (ultrawork, ralph, autopilot…): хук keyword-detector
        // плагина отключён вместе со всеми хуками (disableAllHooks — иначе окна консоли
        // на хосте), поэтому активируем скилл сами — дописываем инструкцию его запуска.
        if (OmcKeywordRouting.BuildKeywordHint(text) is { } keywordHint)
            result += "\n\n" + keywordHint;

        // Процессы oh-my-claudecode: советнические роли плагина замещаются
        // персонами-сабагентами с подходящей специальностью (таблица соответствий)
        if (OmcPersonaRouting.MentionsPluginCommand(text) && ResolveOwnerId(entry.Info) is { } ownerId)
        {
            var (subagents, _) = SplitConsultants(ownerId, entry.Info,
                ResolveOtherPersonas(ownerId, entry.Info.ProjectId, entry.Info));
            if (OmcPersonaRouting.BuildHint(subagents) is { } routingHint)
                result += "\n\n" + routingHint;
        }

        // Возврат на CLI после локальных голосовых ходов: транскрипт CLI реплик разговора
        // не знает, без сводки модель отвечала бы так, будто разговора не было. Берём
        // хвост истории (лента едина) тем же лимитом, что и разговорный контекст.
        if (entry.LocalTurnsSinceCli > 0 && entry.Accumulator is { } voiceAcc)
        {
            var summary = BuildVoiceContextBlock(voiceAcc);
            if (!string.IsNullOrEmpty(summary))
                result = summary + "\n\n" + result;
            entry.LocalTurnsSinceCli = 0;
        }

        return result;
    }

    // Сводка локального разговора для CLI-хода: «Пользователь: … / Ассистент: …» из
    // хвоста истории. null/пусто — реплик не нашлось (тогда и блок не нужен).
    private static string? BuildVoiceContextBlock(TurnAccumulator acc)
    {
        var lines = acc.GetAll()
            .Where(m => m is StoredUserMessage { SystemDirective: not true, Text: { Length: > 0 } }
                        or StoredTextMessage { Text: { Length: > 0 } })
            .TakeLast(VoiceHistoryMessages)
            .Select(m => m switch
            {
                StoredUserMessage u => $"Пользователь: {TrimVoiceMessage(u.Text!)}",
                StoredTextMessage t => $"Ассистент: {TrimVoiceMessage(t.Text!)}",
                _ => null,
            })
            .Where(l => l is not null)
            .ToList();
        if (lines.Count == 0) return null;
        return "[Контекст: до этого пользователь разговаривал голосом с локальной моделью " +
               "(краткие реплики, без инструментов). Последние реплики разговора:\n" +
               string.Join("\n", lines) + "\n]";
    }

    // Гейт локального голосового хода: разговор (Session.VoiceMode) на локальной модели
    // (место chat-voice маршрутизировано на «Локальная»). Только ходы человека без
    // вложений и без протоколов CLI (цикл «до готово»/штаб свои маркеры локаль не
    // воспроизведёт); авто-ходы и доклады автоматизаций — через CLI как раньше.
    // Персона не блокирует: её характер подмешивается в system-промпт разговора.
    private bool ShouldRunLocalVoice(SessionEntry entry, bool auto, bool systemDirective,
        IReadOnlyList<string> attachedPaths) =>
        entry.Info.VoiceMode
        // Только стиль talk: digest — это полный агентный ответ с маркером <voice> в конце,
        // а локальная болталка (LocalCompanionSection) не умеет ни инструменты, ни маркер.
        // Забыть этот гейт — значит молча озвучить фолбэком всю реплику Ollama целиком.
        && !entry.Info.IsVoiceDigest
        && _router is not null && _ollama is not null
        && _router.UsesLocal(Llm.LocalActionCatalog.ChatVoice)
        && !auto && !systemDirective
        && entry.Info.WorkLoop is null
        && entry.Info.TeamImplement is null
        && attachedPaths.Count == 0;

    // messages[] для разговорного вызова Ollama: system-промпт собеседника (+ характер
    // персоны при чате с персоной — дёшево, без полного слоя персоны: инструменты, память
    // и привязки локальному ходу всё равно недоступны) + хвост истории. Текущая реплика
    // уже лежит в аккумуляторе (OnUserMessage общего хвоста SendDirectAsync) — отдельно
    // не добавляется, иначе продублировалась бы. GetAll() сам берёт лок аккумулятора.
    private List<Llm.ChatMsg> BuildVoiceMessages(SessionEntry entry, TurnAccumulator acc)
    {
        var system = Prompts.VoicePrompts.LocalCompanionSection;
        if (entry.Info.PersonaId is { } pid
            && _personas.GetByIdInternal(pid)?.Contract is { } contract)
        {
            var character = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(contract.Character)) character.Add(contract.Character);
            if (!string.IsNullOrWhiteSpace(contract.Tone)) character.Add($"Тон: {contract.Tone}.");
            if (character.Count > 0)
                system += "\n\nТы говоришь от лица персонажа: " + string.Join(" ", character) +
                          "\nЭто сокращённый характер — полный образ и память недоступны в этом режиме.";
        }

        var history = acc.GetAll()
            .Where(m => m is StoredUserMessage { SystemDirective: not true, Text: { Length: > 0 } }
                        or StoredTextMessage { Text: { Length: > 0 } })
            .TakeLast(VoiceHistoryMessages)
            .Select(m => m switch
            {
                StoredUserMessage u => new Llm.ChatMsg("user", TrimVoiceMessage(u.Text!)),
                StoredTextMessage t => new Llm.ChatMsg("assistant", TrimVoiceMessage(t.Text!)),
                _ => null,
            })
            .Where(m => m is not null)
            .Cast<Llm.ChatMsg>()
            .ToList();

        var result = new List<Llm.ChatMsg>(history.Count + 1) { new("system", system) };
        result.AddRange(history);
        return result;
    }

    // Лимиты разговорного контекста: хвост истории и усечение одной реплики. Разговор
    // короткий, а num_ctx профиля Text — 8192 токенов: без усечения длинный старый ход
    // молча вытеснил бы свежие реплики.
    internal const int VoiceHistoryMessages = 16;
    internal static string TrimVoiceMessage(string text) =>
        text.Length <= 1500 ? text : text[..1500] + "…";

    // Локальный голосовой ход: прямой вызов Ollama мимо claude CLI. Ответ приходит
    // синтетическими ServerMessage через OnMessageAsync — тот же конвейер, что у CLI
    // (аккумулятор, статусы Working→Active→Finished, разбор очереди, бродкаст).
    // Фолбэка на CLI нет: тихий 15-секундный старт подпроцесса в разговоре хуже
    // видимой ошибки в ленте.
    private async Task RunLocalVoiceTurnAsync(string sessionId, SessionEntry entry)
    {
        var runId = Interlocked.Increment(ref _runSeq);
        entry.RunId = runId;
        var acc = entry.Accumulator!;
        // Ключ истории: транскрипт CLI, если чат уже начат (лента едина), иначе id чата.
        // Info.ClaudeSessionId ветка НЕ ставит — его ставит CLI штатно при первом CLI-ходе.
        acc.SetSaveKey(entry.Info.ClaudeSessionId ?? sessionId);

        // Provider не заполняем (дефолт claude): фронт модель из каталога не знает и
        // деградирует в Claude-облик — бейдж не ломается, фронт не трогаем.
        await OnMessageAsync(sessionId, acc, new SessionStartedMessage(
            entry.Info.ClaudeSessionId ?? sessionId, IsResume: false,
            Model: _router!.LocalModel, Mode: entry.Info.Mode.ToString().ToLowerInvariant()), runId);

        // Профиль читаем по ключу места — раньше жёстко `CheapProfile.Text` и таймаут
        // по ключу места; пока значения совпадали — `chat-voice` объявлен `CheapProfile.Text`.
        // Одна строка правды устраняет рассинхрон при переводе места на другой профиль.
        var spec = _router.ProfileFor(Llm.LocalActionCatalog.ChatVoice);
        var messages = BuildVoiceMessages(entry, acc);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new CancellationTokenSource();
        entry.LocalVoiceCts = cts;
        Llm.ChatTurnResult? turn = null;
        var cancelled = false;
        // Сколько текста уже ушло в ленту потоком: по нему решается, слать ли ответ
        // целиком в конце (страховка на случай, если поток не дал ни куска)
        var streamed = 0;
        try
        {
            turn = await _ollama!.ChatTurnAsync(messages, _router.LocalModel,
                TimeSpan.FromMilliseconds(_router.TimeoutMsFor(Llm.LocalActionCatalog.ChatVoice)),
                spec.NumPredict, spec.NumCtx, ResolveOwnerId(entry.Info),
                // Поток: куски ответа уходят в ленту (и в озвучку разговора) по мере
                // генерации — иначе первый звук ждал бы конца всего ответа
                onDelta: async chunk =>
                {
                    streamed += chunk.Length;
                    await OnMessageAsync(sessionId, acc, new TextDeltaMessage(chunk), runId);
                },
                ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // «Стоп» пользователя: сообщения не шлём, exited ниже закроет ход (Working→Active)
            cancelled = true;
        }
        catch (Exception ex)
        {
            await OnMessageAsync(sessionId, acc, new ErrorMessage($"Локальная модель: {ex.Message}"), runId);
        }
        finally
        {
            entry.LocalVoiceCts = null;
            entry.LocalTurnsSinceCli++;
        }

        if (turn?.Text is { Length: > 0 } answer)
        {
            // Обычный путь: текст уже ушёл кусками из onDelta. Целиком шлём только если
            // поток не дал ничего (не-потоковый ответ, сбой до первого куска)
            if (streamed == 0)
                await OnMessageAsync(sessionId, acc, new TextDeltaMessage(answer), runId);
            await OnMessageAsync(sessionId, acc, new ResultMessage(
                Subtype: "success", DurationMs: sw.ElapsedMilliseconds, NumTurns: 1,
                Usage: turn.Usage, TotalCostUsd: 0), runId);
        }
        else if (!cancelled)
        {
            // Пустой ответ/сбой HTTP — честная ошибка без фолбэка на CLI
            // (ErrorMessage исключения уже ушёл выше — второй не дублируем).
            if (turn is not null)
                await OnMessageAsync(sessionId, acc,
                    new ErrorMessage("Локальная модель не ответила"), runId);
        }

        await OnMessageAsync(sessionId, acc, new ExitedMessage(), runId);
    }


    // Отправка сообщения с ожиданием завершения хода — REST-канал агентов (chats_send).
    // Занятая или ждущая человека сессия НЕ отвергает сообщение: оно встаёт в очередь
    // (Queued). По умолчанию (preempt=true) обычный ход при этом прерывается — доставка
    // сразу по его концу; preempt=false рабочего хода НЕ прерывает (сообщение ждёт
    // штатного result — Kill процесса убил бы фоновых сабагентов внутри хода), но ход
    // на карточке разрешения (Waiting) прерывается всегда. Цикл
    // «до готово» и штаб «Командной реализации» агентское НЕ рушит: в цикле сообщение ждёт
    // конца ВСЕГО цикла (гейт разбора очереди по WorkLoop), в штабе — штатного конца хода
    // координатора. Таймаут НЕ отменяет ход: вызывающий получает Running и позже читает
    // результат через историю (chats_history).
    public async Task<SendAndWaitResult> SendMessageAndWaitAsync(string sessionId, string text,
        TimeSpan timeout, int agentDepth = 0, string? senderPersonaId = null,
        string? senderOrigin = null, string? senderChatName = null, bool preempt = true)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");

        // Занята только при реально идущем ходе (Working) или ожидании человека (Waiting).
        // Starting у живой сессии означает лишь «создан, ход ещё не запускался»: после первого
        // хода статус идёт Working→Active/Finished и назад в Starting не возвращается (обратно
        // Starting ставит только рестарт → Orphaned). Поэтому свежесозданный чат (у него Process
        // уже присвоен в StartNewSessionAsync, но ходов не было) НЕ занят — принимаем сообщение
        // и стартуем первый ход. Гонку двух одновременных ходов ловит TurnWaiter ниже.
        var status = entry.Info.Status;
        if (status is SessionStatus.Working or SessionStatus.Waiting)
        {
            var queued = await EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin, agentDepth,
                senderChatName: senderChatName);
            // Агентское сообщение по умолчанию (preempt=true) прерывает текущий ход
            // (доставится сразу по его концу), но НЕ рушит цикл «до готово» и штаб
            // «Командной реализации» — там доклад ждёт штатного конца хода, как раньше.
            // preempt=false рабочего хода (Working) не прерывает: сообщение ждёт штатного
            // result, и живой процесс получает его same-process — Kill ради доставки убивал
            // бы фоновых сабагентов, работающих внутри чужого хода (диагностика обрывов
            // агентов 28.08, чат ea9a0f18). Waiting прерывается и при preempt=false: ход на
            // карточке разрешения сам не закончится, «не прерывать» там — висеть вечно
            // (та же логика, что у пользовательского пути в SendMessageAsync).
            // Дубликат и переполнение ничего не прерывают;
            // замороженную «Стоп» очередь агент не возобновляет — прерывать ход тоже не ему.
            // Dispatched — ход успел кончиться между снимком занятости и постановкой, и
            // доставку уже форсировал сам enqueue: прерывать теперь означало бы убить
            // собственный только что запущенный ход.
            if (queued is SendAndWaitResult.Queued { Duplicate: false, Dispatched: false }
                && entry.Info.WorkLoop is null && entry.Info.TeamImplement is null
                && !entry.QueueFrozen
                && (preempt || status is SessionStatus.Waiting))
            {
                entry.DrainOnExitedRun = entry.RunId;
                _log.LogInformation("Interrupt адаптера {Session}: callsite=agent-message (preempt хода агента, chats_send)", sessionId);
                entry.Process?.Interrupt();
            }
            return queued;
        }

        // Чат локального проекта, устройство офлайн: агентское сообщение (chats_send,
        // будильник сторожа) ждёт устройство на сессии — принято, но хода пока не будет
        if (DeviceWaitGate(entry) is { } deviceGate)
            return await ParkForDeviceAsync(sessionId, entry, deviceGate, new DeviceWaitMessage(
                Guid.NewGuid().ToString("N"), text, DeviceWaitMessage.RouteAgent, DateTime.UtcNow,
                senderPersonaId, senderOrigin, agentDepth, SenderChatName: senderChatName));

        await EnsureProcessAsync(sessionId, entry);
        entry.Accumulator?.SetPersona(entry.Info.PersonaId);

        // Авто-имя по первому сообщению — как при отправке человеком
        if (string.IsNullOrWhiteSpace(entry.Info.Name) && !entry.Info.NameLocked && entry.Info.ClaudeSessionId is null)
        {
            var title = MakeChatTitle(text);
            if (!string.IsNullOrEmpty(title))
            {
                entry.Info.Name = title;
                SaveSessions();
                // Фоново уточняем заголовок локальной моделью (best-effort, не блокирует ход)
                _ = RefineChatTitleAsync(sessionId, text, title, ResolveOwnerId(entry.Info));
            }
        }

        // Один ожидатель на ход: параллельная отправка проиграла гонку — в очередь БЕЗ
        // прерывания (чужой ход только стартует, рубить его чужим сообщением нельзя)
        var tcs = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref entry.TurnWaiter, tcs, null) is not null)
            return await EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin, agentDepth,
                senderChatName: senderChatName);
        entry.TurnWaiterBaseline = entry.Accumulator?.GetAll().Count ?? 0;

        await ApplyStatusAsync(sessionId, entry, SessionStatus.Working);
        entry.Accumulator?.OnUserMessage(text, [], viaAgent: agentDepth >= 1, senderPersonaId: senderPersonaId, senderOrigin: senderOrigin);
        entry.TurnInWorktree = false; // сообщение = новый ход в основном дереве (зеркало Extract)
        entry.CurrentTurnSnapshot = null; // ход агента — по «Стоп» в композер не возвращается
        entry.TeamTurnFromHuman = false; // ход поднят агентом (chats_send), не человеком (M7)
        // Превью чата — сообщением агента: адаптер его больше не выставляет (см. ChatPreview)
        entry.Info.LastMessage = ChatPreview(text);
        await entry.Process!.SendMessageAsync(text, null, agentDepth);

        if (timeout <= TimeSpan.Zero) return new SendAndWaitResult.Running();
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        return completed == tcs.Task
            ? new SendAndWaitResult.Completed(await tcs.Task)
            : new SendAndWaitResult.Running(); // ход продолжается, ожидатель очистит OnMessageAsync
    }

    // === Очередь сообщений занятой сессии ===

    // Поставить сообщение в очередь занятой сессии. Дедуп по (текст, отправитель) — ТОЛЬКО
    // для агентских: прежний контракт chats_send советовал ретраить при отказе, и наивный
    // агент насыпал бы дублей одного и того же текста. Человек может осознанно слать
    // повторы («ещё раз», «продолжи») — его сообщения не дедупятся.
    private async Task<SendAndWaitResult> EnqueuePendingAsync(string sessionId, SessionEntry entry,
        string text, string? senderPersonaId, string? senderOrigin, int agentDepth,
        bool silent = false, bool suppressTasksExecute = false, string? senderChatName = null,
        PendingKind kind = PendingKind.Agent, IReadOnlyList<string>? attachedPaths = null,
        string? mode = null, string? staffNote = null)
    {
        bool dispatchNow;
        int position;
        lock (entry.PendingLock)
        {
            if (kind == PendingKind.Agent
                && entry.Pending.Any(p => p.Kind == PendingKind.Agent && p.Text == text && p.SenderPersonaId == senderPersonaId))
                return new SendAndWaitResult.Queued(entry.Pending.Count, Duplicate: true);
            if (entry.Pending.Count >= MaxPendingPerSession)
                return new SendAndWaitResult.QueueFull(MaxPendingPerSession);

            entry.Pending.Add(new QueuedMessage(Guid.NewGuid().ToString("N"), text, senderPersonaId,
                senderOrigin, agentDepth, DateTime.UtcNow, silent, suppressTasksExecute, senderChatName,
                kind, attachedPaths, mode, staffNote));
            position = entry.Pending.Count;

            // Защита от гонки TOCTOU: статус занятости читается БЕЗ лока выше (в SendMessageAsync/
            // SendMessageAndWaitAsync), а Add — здесь, под PendingLock. Если между чтением и Add ход
            // успел завершиться (статус упал из Working, а ResultMessage уже прошёл и его DrainNextPendingAsync
            // отработал по ЕЩЁ ПУСТОЙ очереди) — сообщение зависнет при свободном чате: триггер автодоставки
            // (по result) уже стрелял, а нового не будет. Форсируем разбор при переходе очереди 0→1: drain
            // идемпотентен (RemoveAt атомарен), повторный безопасен. Запускаем ровно один раз на переход,
            // чтобы конкурентные постановки не стимулировали несколько drain'ов. Условия НЕ срабатывания:
            // замороженная «Стоп» очередь (возобновляет только новое пользовательское сообщение) и активный
            // цикл «до готово» — между итерациями чат на мгновение свободен, но агентское сообщение должно
            // ждать конца ВСЕГО цикла (посторонний агент не сбивает координатора, решение владельца
            // 2026-09-01). User и Report — наоборот, продолжают цикл как следующая итерация / ход-реакция
            // постановщика, поэтому при живом цикле dispatchNow форсируется.
            dispatchNow = position == 1
                && !entry.QueueFrozen
                && (entry.Info.WorkLoop is null
                    || kind is PendingKind.User or PendingKind.Report)
                && entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting)
                // Адаптер ведёт оркестрацию хода (фолбэк) — НЕ форсируем разбор очереди: ход,
                // вернувшийся из-под оркестрации через EnqueueBypass, должен дождаться её конца
                // (OrchestrationDone в finally адаптера), иначе drain↔requeue закрутит цикл на
                // рассинхроне «статус Active, но _turn ещё активен» (инцидент 2026-08-10 П3).
                && entry.Process?.OrchestrationActive != true;
        }
        await BroadcastPendingAsync(sessionId, entry);

        // ВНЕ лока: drain сам возьмёт PendingLock и достанет entry из реестра по sessionId.
        if (dispatchNow)
            _ = Task.Run(() => DrainNextPendingAsync(sessionId));

        // Dispatched говорит вызывающему, что доставка уже форсирована: прерывать ход после
        // такой постановки нельзя — это был бы собственный, только что запущенный ход
        return new SendAndWaitResult.Queued(position, Duplicate: false, Dispatched: dispatchNow);
    }

    // Постановка хода в серверную Pending взамен байпаса в _inner (инцидент 2026-08-10 П3):
    // фолбэк-адаптер вызывает при попытке доставки под активной оркестрацией. Ход уходит в
    // очередь как агентский (kind=Agent, дедуп по text+persona), origin помечает источник для
    // лога «Доставка хода». dispatchNow внутри EnqueuePendingAsync гейтится OrchestrationActive —
    // разбор откладывается до OrchestrationDone (finally адаптера).
    private async Task EnqueueBypassTurn(string sessionId, string text,
        IReadOnlyList<string>? attachedPaths, int agentDepth, bool suppressTasksExecute)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        await EnqueuePendingAsync(sessionId, entry, text,
            senderPersonaId: null, senderOrigin: "orchestration-bypass",
            agentDepth, silent: false, suppressTasksExecute,
            kind: PendingKind.Agent, attachedPaths: attachedPaths);
    }

    // Билдеры колбэков для LlmSessionContext: замыкают sessionId сессии (адаптер передаёт тот же
    // Info.Id, но захват надёжнее — сессия уже известна при создании контекста).
    private Func<string, string, IReadOnlyList<string>?, int, bool, Task> BuildEnqueueBypass(string sessionId)
        => (_, text, paths, depth, suppress) => EnqueueBypassTurn(sessionId, text, paths, depth, suppress);

    private Action<string> BuildOrchestrationDone(string sessionId)
        => __ => { _ = Task.Run(async () =>
            {
                try { await DrainNextPendingAsync(sessionId); }
                catch (Exception ex) { Console.Error.WriteLine($"[SessionManager] Разбор очереди после оркестрации ({sessionId}): {ex.Message}"); }
            }); };

    // Отправить сообщение сразу либо, если чат занят, поставить в очередь — единая точка
    // для серверных отправок (доклад исполнителя). Раньше такие ходы полагались на неявную
    // очередь семафора в адаптере: она невидима, безразмерна и молча теряет ходы при
    // Interrupt. Возвращает true, если сообщение отложено.
    //
    // kind — вид сообщения для гейта разбора очереди при активном цикле «до готово». Дефолт
    // Agent: подавляющее большинство серверных отправок — посторонние агенты, и они по-
    // прежнему ждут конца ВСЕГО цикла. Report — доклад исполнителя (TaskExecutionService.
    // ReportToDelegatorAsync): должен будить ждущий цикл, иначе координатор и исполнитель
    // зависают во взаимном ожидании. User отсюда не шлётся.
    public async Task<bool> SendOrEnqueueAsync(string sessionId, string text,
        string? senderPersonaId = null, string? senderOrigin = null,
        bool silent = false, bool suppressTasksExecute = false, string? staffNote = null,
        PendingKind kind = PendingKind.Agent)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");

        if (entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting)
        {
            await EnqueuePendingAsync(sessionId, entry, text, senderPersonaId, senderOrigin,
                agentDepth: 0, silent, suppressTasksExecute, senderChatName: null, kind: kind,
                staffNote: staffNote);
            return true;
        }

        await SendMessageAsync(sessionId, text, [], auto: true, senderPersonaId: senderPersonaId,
            suppressTasksExecute: suppressTasksExecute, senderOrigin: senderOrigin, staffNote: staffNote,
            cause: DeliveryCause.Direct);
        return false;
    }

    // === Отчёт «наверх»: в родительский чат ===

    // Результат попытки отчитаться. TooDeep — цепочка автоотчётов упёрлась в потолок;
    // NoParent — у чата нет эффективного родителя (обычный чат либо вынесен в корень).
    public enum ReportUpResult { Delivered, Queued, NoParent, TooDeep, NotFound }

    // Положить отчёт в родительский чат. Карточка ложится всегда (0 токенов); ход родителя
    // запускается только для withTurn — финального доклада по задаче. Промежуточный отчёт
    // («застрял на блокере») ходом не платим: родитель увидит его на своём следующем ходу.
    //
    // Глубину цепочки считаем на сервере, а не гейтим инструмент через env: значение,
    // меняющееся между ходами, пересобирает MCP-конфиг и рвёт незавершённые вызовы
    // («Stream closed» — на этих граблях уже стояли, см. ClaudeSession.ResolveTasksExecuteEnabled).
    public async Task<ReportUpResult> ReportUpAsync(string sessionId, string text, string ownerId,
        bool withTurn, string? reactionPrompt = null)
    {
        if (GetOwned(sessionId, ownerId) is not { } chat) return ReportUpResult.NotFound;
        if (!_sessions.TryGetValue(sessionId, out var from)) return ReportUpResult.NotFound;
        if (SessionTaskLinks.ParentSessionId(chat, _taskLookup) is not { } parentId) return ReportUpResult.NoParent;
        if (GetOwned(parentId, ownerId) is null) return ReportUpResult.NoParent;
        if (!_sessions.TryGetValue(parentId, out var to)) return ReportUpResult.NoParent;

        var depth = from.ReportChainDepth + 1;
        if (depth > MaxReportChainDepth) return ReportUpResult.TooDeep;
        to.ReportChainDepth = depth;

        // Лицо отчёта: персона чата-отправителя, иначе — нейтральная карточка с его именем
        var persona = chat.PersonaId;
        // Время доклада ставим один раз и кладём в оба слоя (история + живая лента), чтобы
        // подпись поста не разъезжалась между перезагрузкой и пришедшим в моменте событием
        var reportTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await AppendStoredAsync(parentId,
            persona is not null
                ? new StoredTextMessage(text, personaId: persona, timestamp: reportTs)
                : new Protocol.StoredUserMessage(text, viaAgent: true, senderChatName: chat.Name, timestamp: reportTs),
            persona is not null
                ? new GuestTextMessage(text, persona, reportTs)
                : new UserMessageMessage(text, null, null, true, null, chat.Name, Timestamp: reportTs));

        if (!withTurn) return ReportUpResult.Delivered;

        var queued = await SendOrEnqueueAsync(parentId, reactionPrompt ?? text,
            senderPersonaId: null, silent: true, suppressTasksExecute: true);
        return queued ? ReportUpResult.Queued : ReportUpResult.Delivered;
    }

    // Доклад о блокере (Э4): в отличие от промежуточного отчёта БУДИТ постановщика — ход
    // запускается сразу. Тело переехало в TeamTurnCompletionService (волна Ж): пробуждение
    // штаба через `TryConsumeTeamWakeup` с правильным шаблоном карточки при исчерпанной
    // квоте (Stopped vs BudgetExhausted), компенсация квоты через `RefundTeamWakeup`,
    // подъём от чата исполнителя к штабу — собственное дело штабного цикла «блокер»,
    // и вертикаль владеет единым тестом, чтобы не размазывать развилку «квота выбрана
    // vs практика остановлена» между ядром и обёрткой. Обёртка сохранена ради
    // публичной сигнатуры (контроллеры и TaskExecutionService зовут по этому контракту).
    public Task<ReportUpResult> ReportBlockerAsync(string sessionId, string text, string ownerId)
        => _teamTurnCompletion.ReportBlockerAsync(sessionId, text, ownerId);

    // Снимок очереди для клиента и REST
    public IReadOnlyList<QueuedMessage> GetPending(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return [];
        lock (entry.PendingLock) return entry.Pending.ToList();
    }

    // Прервать идущий ход РАДИ очереди: убитый процесс result не пришлёт, поэтому доставку
    // разберёт exited того же прогона (DrainOnExitedRun). В отличие от «Стоп» очередь НЕ
    // морозится — прерывание здесь и есть требование доставить ждущее сообщение сейчас.
    // byUser — перебой затеял человек (кнопка на карточке очереди, его сообщение в ход, ждавший
    // его ответа): живая лента ставит на нём отметку «Ход остановлен пользователем», история
    // обязана её повторить, иначе после F5 разъедется с ней (и со сверкой длин на фронте).
    private void PreemptTurnForQueue(string sessionId, SessionEntry entry, string callsite, bool byUser)
    {
        if (byUser) RecordUserInterrupt(sessionId, entry);
        // Ход убит — result по нему не придёт, а с ним не придёт и потребление буфера маркеров
        // (конец хода в OnMessageAsync, у штаба ещё и HandleTeamTurnEndAsync). Чистим синхронно:
        // иначе маркер мёртвого хода склеился бы с текстом следующего и применился задним
        // числом — фантомная эскалация и сдвиг стадии (класс «волны-призрака»).
        // Буфер копится в любом чате, поэтому и чистим без оглядки на режим штаба.
        lock (entry.TeamTurnLock)
        {
            entry.TeamTurnText.Clear();
            entry.TeamTurnShownLength = 0;
            entry.TurnSawAngleBracket = false;
            entry.TeamTurnAsked = false;
        }
        // Прерванный ход result не пришлёт — погасим маркер итерации цикла, иначе он
        // заблокирует разбор очереди (drain уступает, пока LoopTurnInFlight).
        entry.LoopTurnInFlight = false;
        entry.DrainOnExitedRun = entry.RunId;
        _log.LogInformation("Interrupt адаптера {Session}: callsite={Callsite}", sessionId, callsite);
        entry.Process?.Interrupt();
    }

    // Явное «прервать ход и доставить ждущее сейчас» (кнопка на карточке очереди). Отдельно
    // от «Стоп»: тот морозит очередь и возвращает сообщение в композер, а здесь наоборот —
    // очередь разбирается сразу по exited. Без живого хода и без очереди делать нечего:
    // холостой kill убил бы чужой только что стартовавший ход. false — прерывать было нечего.
    public bool PreemptForPending(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        if (entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting)) return false;
        // Живого прогона нет — убивать нечего, exited не придёт, а взведённая метка разбора
        // осталась бы висеть. Два случая, и оба должны получить честный отказ:
        //  • чат залип в Working/Waiting (ход убил ватчдог/сбой либо статус выставил протухший
        //    ответ на карточку) — реанимирует его «Стоп», клиенту подскажет 409;
        //  • окно ротации фолбэка (OrchestrationActive без прогона): Interrupt внутреннего
        //    адаптера там no-op, придержанного терминала у попытки нет (Held очищен при
        //    SwallowCleanup), и SettleAsync вынесет наружу пустоту — ни exited, ни доставки.
        if (entry.Process is null or { HasLiveTurn: false }) return false;
        // Идёт сворачивание контекста: это тоже ход (CompactAsync → QueueTurnAsync), но обёртка
        // фолбэка его не оркеструет (_turn == null), и exited убитой компакции она ГЛОТАЕТ —
        // до SessionManager не дойдёт ничего. Перебой оставил бы чат в вечном Working с
        // застрявшей очередью, поэтому отказываем: компакция короткая, её стоит дождаться.
        if (entry.CompactRun != 0 && entry.CompactRun == entry.RunId) return false;
        if (entry.QueueFrozen) return false;
        lock (entry.PendingLock)
            if (entry.Pending.Count == 0) return false;
        PreemptTurnForQueue(sessionId, entry, "pending-preempt (кнопка «прервать и отправить»)", byUser: true);
        return true;
    }

    // Отменить ожидающее сообщение (крестик на карточке-призраке). false — уже доставлено.
    public async Task<bool> CancelPendingAsync(string sessionId, string messageId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        bool removed;
        var removedWaiting = false;
        lock (entry.PendingLock)
        {
            removed = entry.Pending.RemoveAll(p => p.Id == messageId) > 0;
            // Крестик на карточке «ждёт устройство» — снимает сообщение с сессии
            if (!removed && entry.Info.DeviceWaitQueue is { } waiting && waiting.Any(m => m.Id == messageId))
            {
                var rest = waiting.Where(m => m.Id != messageId).ToList();
                entry.Info.DeviceWaitQueue = rest.Count > 0 ? rest : null;
                removed = removedWaiting = true;
            }
        }
        if (removedWaiting) SaveSessions();
        if (removed) await BroadcastPendingAsync(sessionId, entry);
        return removed;
    }

    // Прерывание хода («Стоп») замораживает очередь: сообщения НЕ выбрасываются (как было
    // раньше), а остаются стоять — автодоставки после прерывания нет до возобновления
    // пользователем. Одновременно последнее пользовательское возвращается в композер:
    //   • пользовательские в очереди есть → последнее ИЗЫМАЕТСЯ и уходит в composer_restore;
    //   • пользовательских нет, но прерванный ход был пользовательским → копия этого хода
    //     (из ленты не убирается, вернётся как ghost при возобновлении);
    //   • прерван авто/агентский ход и очередь пуста → composer_restore пустой.
    private async Task FreezePendingAsync(string sessionId, SessionEntry entry)
    {
        QueuedMessage? lastUser = null;
        bool hadAny;
        lock (entry.PendingLock)
        {
            entry.QueueFrozen = true;
            hadAny = entry.Pending.Count > 0;
            // Последнее пользовательское изымаем — оно вернётся в композер для правки/повтора.
            // Агентские и более ранние пользовательские остаются ждать возобновления.
            for (var i = entry.Pending.Count - 1; i >= 0; i--)
            {
                if (entry.Pending[i].Kind == PendingKind.User)
                {
                    lastUser = entry.Pending[i];
                    entry.Pending.RemoveAt(i);
                    break;
                }
            }
        }
        if (hadAny) await BroadcastPendingAsync(sessionId, entry);

        // Что вернуть в композер: изъятое из очереди, иначе — снимок прерванного хода (если он
        // был пользовательским). Авто/агентский ход и пустая очередь → пустой restore.
        var restore = lastUser is not null
            ? new ComposerRestoreMessage(lastUser.Text,
                lastUser.AttachedPaths is { Count: > 0 } ? lastUser.AttachedPaths : null, lastUser.Mode)
            : entry.CurrentTurnSnapshot is { } snap
                ? new ComposerRestoreMessage(snap.Text,
                    snap.AttachedPaths is { Count: > 0 } ? snap.AttachedPaths : null, snap.Mode)
                : new ComposerRestoreMessage(null, null, null);
        await BroadcastSessionMessageAsync(sessionId, restore);
        // Snapshot гасим после доставки restore — разовый, повторный FreezePending не должен
        // формировать restore из того же snapshot
        entry.CurrentTurnSnapshot = null;
    }

    // Достать следующее сообщение и отправить его обычным ходом. Вызывается по концу хода
    // (OnMessageAsync). Замороженная «Стоп» очередь не разбирается автоматически — только
    // возобновление (новое пользовательское сообщение при свободном чате) снимает заморозку.
    // Ошибка отправки не должна ронять разбор — сообщение уже снято с очереди, иначе оно
    // застряло бы навсегда и блокировало остальные.
    private async Task DrainNextPendingAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.QueueFrozen) return;

        QueuedMessage? next;
        var scheduleContinue = false;
        lock (entry.PendingLock)
        {
            if (entry.QueueFrozen) return;
            if (entry.Info.WorkLoop is not null)
            {
                // Цикл «до готово» активен: пользовательские сообщения продолжают цикл как
                // следующие итерации, агентские ждут конца ВСЕГО цикла. Если итерация уже
                // стартует (LoopTurnInFlight — системная директива продолжения из
                // ContinueWorkLoopAsync либо ход, извлечённый здесь же в параллельном drain),
                // очередь не трогаем: иначе два хода подряд ушли бы в один процесс. Маркер
                // LoopTurnInFlight выставляем атомарно с извлечением — тогда параллельный
                // ContinueWorkLoopAsync по result увидит его и уступит, не дублируя директиву.
                if (entry.LoopTurnInFlight) return;
                // При активном цикле подхватываем не только User, но и Report (доклад исполнителя):
                // иначе доклад пролежит до конца ВСЕГО цикла, а цикл тем временем жжёт итерации в
                // фазе waiting. Посторонние Agent-сообщения по-прежнему ждут (решение владельца
                // 2026-09-01: посторонний агент не сбивает координатора).
                next = entry.Pending.FirstOrDefault(p => p.Kind is PendingKind.User or PendingKind.Report);
                if (next is null)
                {
                    // Minor 6: при активном цикле, свободном маркере и пустой user-очереди
                    // (напр. сообщение удалили из очереди до exited прерванного хода, а result
                    // не пришёл) — цикл продолжит свою работу директивой, иначе он висел бы
                    // «активным» без движения. ContinueWorkLoopAsync пройдёт гейты сама: если
                    // параллельный запуск (по result) уже взвёл маркер — она сразу уступит.
                    scheduleContinue = true;
                }
                else
                {
                    entry.Pending.Remove(next);
                    entry.LoopTurnInFlight = true;
                }
            }
            else
            {
                // Параллельный drain уже вытащил сообщение и отправляет — уступаем, чтобы не
                // запустить второй ход. Сообщение не теряется: по result этого хода отработает
                // следующий drain (DrainInFlight к тому моменту уже погашен).
                if (entry.DrainInFlight) return;
                next = entry.Pending.FirstOrDefault();
                if (next is not null)
                {
                    entry.Pending.RemoveAt(0);
                    entry.DrainInFlight = true;
                }
            }
        }
        if (scheduleContinue)
        {
            _ = Task.Run(async () =>
            {
                try { await ContinueWorkLoopAsync(sessionId); }
                catch (Exception ex) { Console.Error.WriteLine($"[SessionManager] Продолжение цикла из drain ({sessionId}): {ex.Message}"); }
            });
            return;
        }
        if (next is null) return;

        bool delivered = false;
        try
        {
            // Бродкаст внутри try (Minor 1): исключение хаба (disposed/отвал транспорта) не
            // должно оставлять DrainInFlight взведённым навсегда и глушить разбор очереди чата
            // до перезапуска сервера — finally гарантированно погасит флаг.
            await BroadcastPendingAsync(sessionId, entry);
            delivered = await DeliverPendingAsync(sessionId, entry, next);
        }
        finally
        {
            entry.DrainInFlight = false;
            // Гейт каскада (Minor 3): перепроверяем очередь, ТОЛЬКО если предыдущая доставка
            // выполнена (delivered — не бросилась). Имя шире «ход стартовал»: для гейта важен лишь
            // факт «доставка не провалилась». При провале (CLI не поднялся) статус не ушёл в
            // Working — без гейта перепроверка вычерпала бы ВСЮ очередь подряд, теряя каждое
            // сообщение (одно потерянное — прежнее поведение, весь хвост — регрессия). Гонка
            // «result пришёл раньше finally» (Minor 2): статус уже Idle, очередь непуста —
            // перезапуск разыграет следующее сообщение. Сам DrainNextPendingAsync имеет гейт
            // DrainInFlight, так что повторный запуск безопасен (уступит, если кто-то уже взял).
            if (delivered && !entry.QueueFrozen
                && entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting or SessionStatus.Starting))
            {
                // Minor 2: чтение Pending.Count — под PendingLock, по конвенции файла.
                bool hasPending;
                lock (entry.PendingLock) hasPending = entry.Pending.Count > 0;
                if (hasPending)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await DrainNextPendingAsync(sessionId); }
                        catch (Exception ex) { Console.Error.WriteLine($"[SessionManager] Повторный разбор очереди ({sessionId}): {ex.Message}"); }
                    });
                }
            }
        }
    }

    // Флаг «уже предупредили про opt-out»: выводим ровно один раз на инстанс процесса, иначе
    // прод-настройки с потолком 0 будут сыпать Warning на каждом ходе. opt-out — исключительный
    // кейс (тесты или явное выключение защиты), в нормальной работе потолок = 8.
    private static int _awaitExitOptOutLogged;

    // Ожидание подтверждённой смерти прогона предыдущего хода перед отложенной доставкой
    // (инцидент 2026-08-10, П2-Ф1 «сериализация на смерти»). См. комментарий в DeliverPendingAsync.
    // Потолок — Delivery:AwaitProcessExitSeconds (дефолт 8): латентность старта авто-хода в
    // секунды принята владельцем как цена за устранение класса гонок с доживающим прогоном.
    // По истечении — НЕ fail-open (вернуло бы гонку) и НЕ честная ошибка (доставка нужна):
    // прерываем доживающий прогон (фоновые агенты гибнут, добиваются в следующем ходе через
    // notification — как при смене окружения) и коротко ждём его финализации.
    private async Task AwaitPreviousTurnExitAsync(string sessionId, SessionEntry entry)
    {
        var ceiling = int.TryParse(_config["Delivery:AwaitProcessExitSeconds"], out var s) && s >= 0
            ? s : 8;
        // Потолок 0 — отключить сериализацию (тесты, или явный opting-out). Иначе ждём смерти.
        // Один раз на процесс предупреждаем в лог: защита от гонок на смерти прогона выключена,
        // это исключительный кейс — в проде потолок должен быть положительным.
        if (ceiling <= 0)
        {
            if (Interlocked.Exchange(ref _awaitExitOptOutLogged, 1) == 0)
                _log.LogWarning(
                    "Delivery:AwaitProcessExitSeconds={Ceiling} — сериализация отложенной доставки выключена (opt-out)",
                    ceiling);
            return;
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(ceiling);
        while (Busy(entry.Process))
        {
            if (DateTime.UtcNow >= deadline)
            {
                _log.LogInformation(
                    "Ожидание смерти прогона {Session} истекло ({Ceiling} с) — прерываю доживание ради отложенной доставки",
                    sessionId, ceiling);
                entry.Process?.Interrupt();
                // Interrupt асинхронен: прогон финализируется за секунды, даём короткий grace.
                var grace = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                while (Busy(entry.Process) && DateTime.UtcNow < grace)
                    await Task.Delay(50);
                break;
            }
            await Task.Delay(50);
        }
    }

    // Адаптер занятprevious-ходом: у него жив прогон CLI (HasLiveTurn — _turnLock держит,
    // транзрипт пишет) ИЛИ активна фолбэк-оркестрация (OrchestrationActive — между SettleAsync
    // и finally). В обоих случаях старт нового хода гоняется с предыдущим → сериализуем на
    // смерти. Чистая функция для прямого тестирования веток (Ф1, инцидент 2026-08-10).
    internal static bool Busy(ILlmSessionAdapter? p) =>
        p is { HasLiveTurn: true } or { OrchestrationActive: true };

    // Доставка конкретного сообщения из очереди обычным ходом (без повторных гейтов очереди).
    // Пользовательское идёт со своими вложениями и режимом; агентское — как серверная отправка.
    // Возвращает true, если доставка выполнена (ход стартовал — можно ждать result); false —
    // бросилась внутри, ход не поднялся и его повторная обработка по result не придёт (Minor 3).
    private async Task<bool> DeliverPendingAsync(string sessionId, SessionEntry entry, QueuedMessage next)
    {
        // Сериализация отложенной доставки на смерти предыдущего хода (инцидент 2026-08-10, П2-Ф1):
        // статус Active ставится по result РАНЬШЕ, чем ClaudeSession отпустит _turnLock и финализирует
        // прогон. Старт нового хода в этом окне гоняется с доживающим процессом — same-process reuse
        // (Ч1), лок миграции (Ч2 — держатель .jsonl жив), interrupt свежего прогона. Ждём, пока прогон
        // умрёт (HasLiveTurn=false) И фолбэк-оркестрация снимется (OrchestrationActive=false) — тогда
        // _turnLock свободен, транзрипт закрыт, новый ход стартует свежим процессом. Только для
        // отложенной доставки (из Pending): свежий ход человека через SendDirectAsync сюда не попадает.
        await AwaitPreviousTurnExitAsync(sessionId, entry);
        try
        {
            if (next.Kind == PendingKind.User)
                // fromQueue: true — клиент рисовал призраком, бродкастим live-баллон (как auto).
                // Режим уже применён при постановке, повторно не передаём, чтобы не сбросить
                // возможную смену режима после (SetMode) — Info.Mode источник правды.
                await SendDirectAsync(sessionId, entry, next.Text,
                    next.AttachedPaths ?? [], mode: next.Mode, systemDirective: false, auto: false,
                    senderPersonaId: next.SenderPersonaId, suppressTasksExecute: next.SuppressTasksExecute,
                    senderOrigin: next.SenderOrigin, fromQueue: true, cause: DeliveryCause.QueueUser);
            else
                await SendMessageAsync(sessionId, next.Text, [], auto: true,
                    senderPersonaId: next.SenderPersonaId, senderOrigin: next.SenderOrigin,
                    suppressTasksExecute: next.SuppressTasksExecute, staffNote: next.StaffNote,
                    cause: DeliveryCause.QueueAgent);
            return true;
        }
        catch (Exception ex)
        {
            // При активном цикле drain выставил LoopTurnInFlight=true до отправки. Если ход
            // так и не стартовал — ход-то не пришлёт result, и маркер повис бы навсегда,
            // заблокировав разбор очереди. Сбрасываем, чтобы цикл не завис на мёртвой итерации.
            if (entry.Info.WorkLoop is not null)
                entry.LoopTurnInFlight = false;
            Console.Error.WriteLine($"[SessionManager] Доставка отложенного сообщения ({sessionId}): {ex.Message}");
            return false;
        }
    }

    // Клиенту уходят только видимые элементы: silent — ход-реакция на доклад, чей текст
    // уже лежит в ленте отдельной репликой, призрак дублировал бы её служебным промптом.
    // Kind/AttachedPaths/Mode — только для пользовательских (карточка-призрак и композер).
    private Task BroadcastPendingAsync(string sessionId, SessionEntry entry) =>
        BroadcastSessionMessageAsync(sessionId, new PendingMessagesMessage(VisiblePending(entry)));

    // Ждущие устройство идут впереди обычной очереди: они пришли раньше и уйдут в работу
    // первыми (ReleaseDeviceWaitAsync ставит их в голову Pending).
    private static IReadOnlyList<PendingMessageDto> VisiblePending(SessionEntry entry)
    {
        lock (entry.PendingLock)
            return [
                .. (entry.Info.DeviceWaitQueue ?? []).Select(m => new PendingMessageDto(
                    m.Id, m.Text, m.SenderPersonaId, m.SenderOrigin, m.QueuedAt, m.SenderChatName,
                    m.Route == DeviceWaitMessage.RouteUser ? "user" : "agent",
                    m.AttachedPaths, m.Route == DeviceWaitMessage.RouteUser ? m.Mode : null,
                    WaitingForDevice: true)),
                .. entry.Pending.Where(p => !p.Silent).Select(p => new PendingMessageDto(
                    p.Id, p.Text, p.SenderPersonaId, p.SenderOrigin, p.EnqueuedAt, p.SenderChatName,
                    p.Kind == PendingKind.User ? "user" : "agent",
                    p.AttachedPaths, p.Kind == PendingKind.User ? p.Mode : null)),
            ];
    }

    // === Ожидание устройства локального проекта (ADR-016, вариант А плана §5) ===

    // Вердикт «ждать устройство» для чата: только проектный чат локального проекта, чьё
    // устройство не готово. Устройства нет вовсе (отозвано) — не ждём: ход откажет честно.
    private ProjectBackgroundGate? DeviceWaitGate(SessionEntry entry)
    {
        if (_deviceGate is null || entry.Info.ProjectId is not { } pid) return null;
        if (_projects.GetById(pid) is not { } project) return null;
        var gate = _deviceGate.Check(project);
        return gate.MustWait ? gate : null;
    }

    // Парковка на сессии: список заменяется целиком (копия с добавлением) — SaveSessions
    // сериализует Info без лока очереди и не должен застать список посреди правки. Дедуп
    // агентских — как у Pending: наивный ретрай агента не множит одинаковые ходы.
    private async Task<SendAndWaitResult> ParkForDeviceAsync(string sessionId, SessionEntry entry,
        ProjectBackgroundGate gate, DeviceWaitMessage message)
    {
        int position;
        lock (entry.PendingLock)
        {
            var queue = entry.Info.DeviceWaitQueue ?? [];
            if (message.Route == DeviceWaitMessage.RouteAgent
                && queue.Any(m => m.Route == DeviceWaitMessage.RouteAgent && m.Text == message.Text
                    && m.SenderPersonaId == message.SenderPersonaId))
                return new SendAndWaitResult.Queued(queue.Count, Duplicate: true);
            entry.Info.DeviceWaitQueue = [.. queue, message];
            position = entry.Info.DeviceWaitQueue.Count;
        }
        SaveSessions();
        await BroadcastPendingAsync(sessionId, entry);
        _log.LogInformation("Чат {Session}: сообщение ждёт устройство {Device} ({Reason}), в ожидании {Count}",
            sessionId, gate.DeviceId, gate.Reason, position);
        return new SendAndWaitResult.Queued(position, Duplicate: false);
    }

    /// <summary>Чаты, у которых есть сообщения, ждущие устройство.</summary>
    public IReadOnlyList<Session> GetDeviceWaitingSessions() =>
        [.. _sessions.Values.Select(e => e.Info).Where(i => i.DeviceWaitQueue is { Count: > 0 })];

    /// <summary>
    /// Устройство готово: ждавшие сообщения встают в голову обычной очереди (порядок прихода
    /// сохраняется) и разбираются штатным drain — первое сразу, если чат свободен, остальные
    /// по концу хода. Возвращает число выпущенных сообщений.
    /// </summary>
    public async Task<int> ReleaseDeviceWaitAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return 0;
        List<DeviceWaitMessage> released;
        bool dispatchNow;
        lock (entry.PendingLock)
        {
            released = entry.Info.DeviceWaitQueue ?? [];
            if (released.Count == 0) return 0;
            entry.Info.DeviceWaitQueue = null;
            entry.Pending.InsertRange(0, released.Select(m => new QueuedMessage(
                m.Id, m.Text, m.SenderPersonaId, m.SenderOrigin, m.AgentDepth, m.QueuedAt,
                SuppressTasksExecute: m.SuppressTasksExecute, SenderChatName: m.SenderChatName,
                Kind: m.Route == DeviceWaitMessage.RouteUser ? PendingKind.User : PendingKind.Agent,
                AttachedPaths: m.AttachedPaths, Mode: m.Mode, StaffNote: m.StaffNote)));
            dispatchNow = !entry.QueueFrozen
                && entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting)
                && entry.Process?.OrchestrationActive != true;
        }
        SaveSessions();
        await BroadcastPendingAsync(sessionId, entry);
        _log.LogInformation("Чат {Session}: устройство готово, в работу {Count} ждавших сообщений", sessionId, released.Count);
        if (dispatchNow) _ = Task.Run(() => DrainNextPendingAsync(sessionId));
        return released.Count;
    }

    /// <summary>
    /// Снять сообщения, ждущие устройство дольше потолка (поставлены раньше <paramref name="olderThanUtc"/>).
    /// Возвращает снятые — уведомление владельцу шлёт вызывающий.
    /// </summary>
    public async Task<IReadOnlyList<DeviceWaitMessage>> ExpireDeviceWaitAsync(string sessionId, DateTime olderThanUtc)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return [];
        List<DeviceWaitMessage> expired;
        lock (entry.PendingLock)
        {
            var queue = entry.Info.DeviceWaitQueue ?? [];
            expired = [.. queue.Where(m => m.QueuedAt <= olderThanUtc)];
            if (expired.Count == 0) return [];
            var rest = queue.Where(m => m.QueuedAt > olderThanUtc).ToList();
            entry.Info.DeviceWaitQueue = rest.Count > 0 ? rest : null;
        }
        SaveSessions();
        await BroadcastPendingAsync(sessionId, entry);
        return expired;
    }

    // Есть ли в очереди сообщение, продолжающее цикл. User — следующая итерация цикла;
    // Report — доклад исполнителя, требующий хода-реакции постановщика (тоже будит цикл,
    // иначе цикл висит в фазе waiting без движения). Посторонние Agent-сообщения не в счёт:
    // при активном цикле они ждут его конца (решение владельца 2026-09-01). Опирается на
    // гейт drain по result в OnMessageAsync — без такого сообщения цикл сам поднимет
    // ContinueWorkLoopAsync по своему LoopTurnInFlight-маркеру.
    private static bool HasContinuingPending(SessionEntry entry)
    {
        lock (entry.PendingLock)
            return entry.Pending.Any(p => p.Kind is PendingKind.User or PendingKind.Report);
    }

    private static bool HasPending(SessionEntry entry)
    {
        lock (entry.PendingLock)
            return entry.Pending.Count > 0;
    }

    // Снимок для replay при JoinSession (без служебных)
    public IReadOnlyList<PendingMessageDto> GetVisiblePending(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) ? VisiblePending(entry) : [];

    // Ручное сворачивание контекста: /compact в CLI, минуя счётчики и историю user-сообщений
    public async Task CompactAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");
        if (entry.Info.ClaudeSessionId is null) return; // ходов ещё не было — сворачивать нечего
        if (!_llmProviders.CapabilitiesFor(entry.Info.Model).SupportsCompact)
            return; // провайдер не умеет compact — защита от рассинхрона UI

        await EnsureProcessAsync(sessionId, entry);
        entry.Accumulator?.SetPersona(entry.Info.PersonaId);

        await ApplyStatusAsync(sessionId, entry, SessionStatus.Working);

        // Метка «идёт компакция» — её читает PreemptForPending: прерывать компакцию нельзя,
        // её exited глотает обёртка фолбэка. Гасится на конце хода в OnMessageAsync — но
        // только терминалом ЭТОГО прогона, поэтому и метка хранит его идентификатор.
        // Ход не стартовал (адаптер отвалился на запуске compact) — снимаем сразу: терминала
        // не будет, а повисшая метка запретила бы перебой в этом чате навсегда.
        entry.CompactRun = entry.RunId;
        try { await entry.Process!.CompactAsync(); }
        catch { entry.CompactRun = 0; throw; }
    }

    // После перезапуска сервера Process может быть null — восстанавливаем сессию
    // Создание/переиспользование аккумулятора истории (без CLI-процесса). Вырезано из
    // EnsureProcessCoreAsync: локальному голосовому ходу (chat-voice) процесс не нужен,
    // а аккумулятор — да (реплики разговора пишутся в ту же историю). Ключ: ClaudeSessionId
    // чата, а при его отсутствии (ходов CLI ещё не было) — id чата: локальные ходы пишут
    // историю в data/sessions/{id чата}/history.json, и после рестарта сервера, и при
    // возврате на CLI она подхватывается отсюда же.
    private async Task EnsureAccumulatorAsync(SessionEntry entry)
    {
        if (entry.Accumulator is not null) return;
        // Оживление под _falPersistLock — сериализуем с прямой записью fal-стоимости в
        // историю неактивной сессии (PublishFalCostAsync): иначе LoadAsync тут и запись там
        // теряли бы друг друга (lost update). Повторная проверка под локом.
        await WithFalPersistLockAsync(async () =>
        {
            if (entry.Accumulator is not null) return;
            var key = entry.Info.ClaudeSessionId ?? entry.Info.Id.ToString();
            var existingHistory = await _history.LoadAsync(key);
            entry.Accumulator = new TurnAccumulator(existingHistory, entry.Info.ClaudeSessionId);
        });
    }

    private async Task EnsureProcessAsync(string sessionId, SessionEntry entry)
    {
        // Сериализуем весь check-then-act под per-entry локом: иначе два конкурентных хода
        // создали бы два адаптера одной сессии (см. EnsureLock).
        await entry.EnsureLock.WaitAsync();
        try
        {
            await EnsureProcessCoreAsync(sessionId, entry);
        }
        finally { entry.EnsureLock.Release(); }
    }

    private async Task EnsureProcessCoreAsync(string sessionId, SessionEntry entry)
    {
        // Отложенная уборка устаревшего адаптера (смена собеседника/правка персоны):
        // дожидаемся dispose ДО старта нового — иначе старый процесс ещё умирает,
        // когда новый уже поднялся с --resume того же транскрипта
        if (entry.AdapterStale && entry.Process is { } stale)
        {
            entry.AdapterStale = false;
            entry.Process = null;
            try { await stale.DisposeAsync(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Уборка устаревшего адаптера ({sessionId}): {ex.Message}");
            }
        }
        if (entry.Process is not null) return;

        // Переиспускаем существующий in-memory аккумулятор (процесс мог быть сброшен
        // сменой собеседника — SwitchSpeaker; его состояние, включая ещё не сохранённые
        // внеходовые карточки конвейера/совещания, нельзя терять). Новый создаём только
        // при ленивом восстановлении сессии после рестарта сервера (Accumulator == null).
        await EnsureAccumulatorAsync(entry);
        var accumulator = entry.Accumulator!;

        // Чат вне проекта — рабочая папка Chats, без проектного промпта и правил;
        // проектная сессия — RootPath/SystemPrompt/PermissionRules из проекта.
        // Персона-слой восстанавливаем так же, как при первом старте (иначе после рестарта
        // сервера персонная сессия теряла бы характер и долгую память).
        // Идентификатор прогона — как в StartNewSessionAsync (см. SessionEntry.RunId)
        var runId = Interlocked.Increment(ref _runSeq);

        LlmSessionContext context;
        if (entry.Info.ProjectId is null)
        {
            var rootPath = ResolveChatRoot(entry.Info.OwnerId
                ?? throw new InvalidOperationException("У чата не задан владелец"));
            var persona = BuildPersonaLayer(entry.Info, entry.Info.OwnerId);
            var workspace = BuildWorkspaceContext(entry.Info.OwnerId, null, entry.Info.Id, persona.Persona);
            var widgetsMcp = BuildWidgetsContext(entry.Info.OwnerId, persona.Persona);
            var watchMcp = BuildWatchContext(entry.Info.OwnerId);
            var webSearchMcp = BuildWebSearchContext(entry.Info.OwnerId, persona.Persona);
            var higgsfieldMcp = BuildHiggsfieldContext(entry.Info.OwnerId, persona.Persona);
            var tasksMcp = TasksMcpEnabled(entry.Info.OwnerId, entry.Info, persona.Persona)
                ? BuildTasksContext(entry.Info.OwnerId, null, persona.Persona) : null;
            var notesMcp = _bindings.EffectiveToolEnabled(entry.Info.OwnerId, persona.Persona, "notes")
                ? BuildNotesContext(entry.Info.OwnerId, null, persona.Persona) : null;
            var personasMcp = BuildPersonasContext(entry.Info.OwnerId, null, entry.Info, persona.Persona);
            var notificationsMcp = BuildNotificationsContext(entry.Info.OwnerId, entry.Info.PersonaId, persona.Persona);
            var difyMcp = BuildDifyContext(entry.Info.OwnerId);
            context = new LlmSessionContext(rootPath,
                msg => OnMessageAsync(sessionId, accumulator, msg, runId),
                RawSystemPrompt: null, BuiltInSystemPrompt: ProjectManager.BuiltInSystemPrompt,
                PermissionRules: null,
                ContentRootPath: AppContext.BaseDirectory,
                TasksMcp: tasksMcp,
                NotesMcp: notesMcp,
                PersonaProvider: BuildPersonaProvider(entry.Info, entry.Info.OwnerId),
                MemoryMcp: persona.Memory,
                ExtraDisallowedTools: BuildExtraDisallowed(entry.Info.OwnerId, persona.Persona, entry.Info),
                PersonasMcp: personasMcp,
                NotificationsMcp: notificationsMcp,
                WorkspaceMcp: workspace,
                PersonaAgentsProvider: BuildPersonaAgentsProvider(entry.Info.OwnerId, entry.Info, persona.Persona),
                Launcher: _launchers.ForOwner(entry.Info.OwnerId),
                ModulesMcp: BuildModulesContext(entry.Info.OwnerId),
                WidgetsMcp: widgetsMcp,
                // Чат вне проекта — графа кода нет (он ключуется проектом)
                CodeGraphMcp: null,
                DifyMcp: difyMcp,
                BrowserEnabled: BrowserEnabled(entry.Info.OwnerId, persona.Persona),
                CliConfigRoot: ConfigRootFor(entry.Info.OwnerId, entry.Info.Provider),
                ExternalMcpProvider: BuildExternalMcpProvider(entry.Info.OwnerId, null, persona.Persona),
                PersistSessions: SaveSessions,
                EnqueueBypass: BuildEnqueueBypass(sessionId),
                OrchestrationDone: BuildOrchestrationDone(sessionId),
                HttpMcpActive: HttpMcpActive(widgetsMcp, persona.Memory, tasksMcp, notesMcp, personasMcp,
                    workspace, notificationsMcp, dify: difyMcp, watch: watchMcp, webSearch: webSearchMcp,
                    higgsfield: higgsfieldMcp),
                HttpMcpEnabledProvider: HttpMcpEnabled,
                Events: _turnEvents,
                WatchMcp: watchMcp,
                WebSearchMcp: webSearchMcp,
                HiggsfieldMcp: higgsfieldMcp,
                // Чат вне проекта — fallback для slice графа не применяется (граф ключуется проектом)
                MainRootPath: null);
                // Чат вне проекта: трейлер CCS-Session в подсказке досье (DossierTrailerContributor) пропускается
        }
        else
        {
            var project = _projects.GetById(entry.Info.ProjectId)
                ?? throw new InvalidOperationException("Проект не найден");
            var persona = BuildPersonaLayer(entry.Info, project.OwnerId);
            var workspace = BuildWorkspaceContext(project.OwnerId, project.Id, entry.Info.Id, persona.Persona);
            // Корень проекта запоминаем ДО EffectiveRoot: у worktree-чата это и есть fallback
            // для slice графа (ADR-003). Совпадение с rootPath — чат без worktree, fallback
            // сводится к no-op в CodeGraphPromptProvider.GetSliceAsync.
            var projectRoot = project.RootPath;
            var rootPath = EffectiveRoot(entry.Info, projectRoot);
            var widgetsMcp = BuildWidgetsContext(project.OwnerId, persona.Persona);
            var watchMcp = BuildWatchContext(project.OwnerId);
            var webSearchMcp = BuildWebSearchContext(project.OwnerId, persona.Persona);
            var higgsfieldMcp = BuildHiggsfieldContext(project.OwnerId, persona.Persona);
            var localMediaMcp = BuildLocalMediaContext(project.OwnerId, project.Id, persona.Persona);
            var memoryMcp = persona.Memory ?? BuildTeamMemoryContext(project.OwnerId, project.Id);
            var tasksMcp = TasksMcpEnabled(project.OwnerId, entry.Info, persona.Persona)
                ? BuildTasksContext(project.OwnerId, project.Id, persona.Persona) : null;
            var notesMcp = _bindings.EffectiveToolEnabled(project.OwnerId, persona.Persona, "notes")
                ? BuildNotesContext(project.OwnerId, project.Id, persona.Persona) : null;
            var personasMcp = BuildPersonasContext(project.OwnerId, project.Id, entry.Info, persona.Persona);
            var notificationsMcp = BuildNotificationsContext(project.OwnerId, entry.Info.PersonaId, persona.Persona);
            var codeGraphMcp = BuildCodeGraphContext(project.OwnerId, project.Id, entry.Info.Id, rootPath, persona.Persona);
            var difyMcp = BuildDifyContext(project.OwnerId);
            context = new LlmSessionContext(rootPath,
                msg => OnMessageAsync(sessionId, accumulator, msg, runId),
                project.SystemPrompt,
                ProjectManager.BuiltInSystemPrompt,
                () => _projects.GetById(entry.Info.ProjectId!)?.PermissionRules ?? (IReadOnlyList<PermissionRule>)Array.Empty<PermissionRule>(),
                ContentRootPath: AppContext.BaseDirectory,
                TasksMcp: tasksMcp,
                NotesMcp: notesMcp,
                PersonaProvider: BuildPersonaProvider(entry.Info, project.OwnerId),
                MemoryMcp: memoryMcp,
                ExtraDisallowedTools: BuildExtraDisallowed(project.OwnerId, persona.Persona, entry.Info),
                PersonasMcp: personasMcp,
                NotificationsMcp: notificationsMcp,
                WorkspaceMcp: workspace,
                PersonaAgentsProvider: BuildPersonaAgentsProvider(project.OwnerId, entry.Info, persona.Persona),
                Launcher: _launchers.ForProject(project),
                ModulesMcp: BuildModulesContext(project.OwnerId),
                WidgetsMcp: widgetsMcp,
                CodeGraphMcp: codeGraphMcp,
                DifyMcp: difyMcp,
                DesktopMcp: BuildDesktopContext(project.OwnerId, entry.Info, persona.Persona),
                BrowserEnabled: BrowserEnabled(project.OwnerId, persona.Persona),
                CliConfigRoot: ConfigRootFor(project.OwnerId, entry.Info.Provider),
                ExternalMcpProvider: BuildExternalMcpProvider(project.OwnerId, project.Id, persona.Persona),
                PersistSessions: SaveSessions,
                EnqueueBypass: BuildEnqueueBypass(sessionId),
                OrchestrationDone: BuildOrchestrationDone(sessionId),
                HttpMcpActive: HttpMcpActive(widgetsMcp, memoryMcp, tasksMcp, notesMcp, personasMcp,
                    workspace, notificationsMcp, codeGraphMcp, difyMcp, watchMcp, webSearchMcp, higgsfieldMcp,
                    localMediaMcp),
                HttpMcpEnabledProvider: HttpMcpEnabled,
                ChatContextProvider: BuildChatContextProvider(sessionId),
                Events: _turnEvents,
                WatchMcp: watchMcp,
                WebSearchMcp: webSearchMcp,
                HiggsfieldMcp: higgsfieldMcp,
                LocalMediaMcp: localMediaMcp,
                // Корень ГЛАВНОЙ ветки проекта — fallback для slice графа кода, пока свой граф
                // worktree-ветки не построен (ADR-003).
                MainRootPath: projectRoot,
                ServerContent: ProjectCapabilities.ServerContentEnabled(project),
                TranscriptOnServer: ProjectCapabilities.TranscriptOnServer(project));
        }
        var adapter = _adapters.Create(entry.Info, context);
        entry.Process = adapter;
        entry.RunId = runId;
        await adapter.StartAsync();
    }

    // Заголовок чата из первого сообщения: первая строка, обрезанная до разумной длины.
    // Ход командной механики (/team-implement {...}, /oh-my-claudecode:ralplan "тема"…) — не
    // текст для человека, поэтому сначала пробуем вытащить тему из обвязки (см.
    // ExtractTeamMechanicTopic), а к сырому тексту падаем только если распознать не удалось.
    private static string MakeChatTitle(string text)
    {
        var topic = ExtractTeamMechanicTopic(text);
        var t = (string.IsNullOrWhiteSpace(topic) ? text : topic).Trim();
        var nl = t.IndexOfAny(['\n', '\r']);
        if (nl >= 0) t = t[..nl].Trim();
        const int max = 48;
        if (t.Length > max) t = string.Concat(t.AsSpan(0, max).TrimEnd(), "…");
        return t;
    }

    // Слаги командных механик с JSON-аргументами — тема лежит в одном из полей ниже
    // (порядок — приоритет: первое непустое). Зеркалит describeTeamTurn во frontend/teamMechanics.ts.
    private static readonly string[] JsonMechanicSlugs =
        ["/panel-of-experts", "/review-consilium", "/red-team", "/team-implement"];
    private static readonly string[] JsonTopicKeys = ["task", "topic", "target", "brief"];

    // Слаги строковых механик — тема в кавычках (см. quoteTopic во frontend/teamMechanics.ts:
    // внутренние " заменены на «», поэтому кавычки темы — первая и единственная пара).
    private static readonly string[] QuotedTopicSlugs =
    [
        "/oh-my-claudecode:ralplan", "/oh-my-claudecode:deep-interview",
        "/oh-my-claudecode:autopilot", "/oh-my-claudecode:trace", "/oh-my-claudecode:sciomc",
    ];
    private const string UltraqaSlug = "/oh-my-claudecode:ultraqa";
    private static readonly Regex QuotedTopicRegex = new("\"([^\"]*)\"", RegexOptions.Compiled);

    // Тема хода командной механики или null, если текст — не вызов известной механики
    // (обычное сообщение, режим «Командная реализация» без обвязки — тема уходит как есть).
    private static string? ExtractTeamMechanicTopic(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '/') return null;

        foreach (var slug in JsonMechanicSlugs)
        {
            if (!trimmed.StartsWith(slug, StringComparison.Ordinal)) continue;
            var braceIdx = trimmed.IndexOf('{');
            if (braceIdx < 0) return null;
            try
            {
                using var doc = JsonDocument.Parse(trimmed[braceIdx..]);
                foreach (var key in JsonTopicKeys)
                {
                    if (doc.RootElement.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                    {
                        var s = val.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
            }
            catch (JsonException) { /* битый JSON — падаем к обрезке сырого текста */ }
            return null;
        }

        foreach (var slug in QuotedTopicSlugs)
        {
            if (!trimmed.StartsWith(slug, StringComparison.Ordinal)) continue;
            var m = QuotedTopicRegex.Match(trimmed);
            return m.Success ? m.Groups[1].Value : null;
        }

        if (trimmed.StartsWith(UltraqaSlug, StringComparison.Ordinal))
        {
            var rest = trimmed[UltraqaSlug.Length..].TrimStart();
            if (rest.StartsWith("--", StringComparison.Ordinal))
            {
                var spaceIdx = rest.IndexOf(' ');
                rest = spaceIdx >= 0 ? rest[(spaceIdx + 1)..] : "";
            }
            return rest.Trim();
        }

        return null;
    }

    // Уточнение авто-заголовка чата (действие chat-title) по маршруту, назначенному месту в
    // «Поставщиках моделей» (локаль/direct-модель/слот — решает CheapTextRunner.RunAsync).
    // Best-effort: молчим при любой проблеме, не трогаем имя, переименованное вручную.
    private async Task RefineChatTitleAsync(string sessionId, string firstMessage, string expectedTitle, string? ownerId)
    {
        if (_cheap is null) return;
        try
        {
            var prompt =
                "Придумай короткий заголовок (3-6 слов, по-русски, без кавычек и точки в конце) для чата " +
                "по первому сообщению пользователя. " + Llm.TitleExtraction.JsonHintWithIcon + "\n\n" +
                (firstMessage.Length > 1500 ? firstMessage[..1500] : firstMessage);
            var raw = await _cheap.RunAsync(Llm.LocalActionCatalog.ChatTitle, prompt,
                ownerId: ownerId, jsonFormat: Llm.TitleExtraction.SchemaWithIcon);
            var line = Llm.TitleExtraction.Extract(raw);
            if (line is null || line.Length > 80) return;
            // Значок темы — имя lucide-компонента (PascalCase): имя остаётся чистым текстом,
            // иконку рисует фронт по icons[iconName]
            var iconName = Llm.TitleExtraction.ExtractIconName(raw);

            if (!_sessions.TryGetValue(sessionId, out var entry)) return;
            // Пользователь мог переименовать вручную, пока модель думала — тогда не трогаем
            if (entry.Info.Name != expectedTitle) return;
            entry.Info.Name = line;
            if (iconName is not null) entry.Info.Topic = iconName;
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
            await BroadcastChatRenamedAsync(sessionId, entry.Info, line);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Уточнение заголовка чата {Session}", sessionId); }
    }

    // Обновление названия чата по ТЕКУЩЕЙ переписке — явное действие пользователя (AI-хаб).
    // В отличие от авто-заголовка (только первое сообщение, ставится один раз) — читает весь
    // транскрипт и ВСЕГДА перезаписывает имя. Раннер как у «Итога сессии»: локаль или claude-фолбэк.
    public async Task<Session?> RetitleAsync(string userId, string sessionId, CancellationToken ct)
    {
        var session = GetOwned(sessionId, userId);
        if (session is null) return null;
        if (_cheap is null) throw new InvalidOperationException("ИИ недоступен");

        var history = await GetHistoryAsync(sessionId);
        var transcript = SessionSummaryService.BuildTranscript(history, 8000);
        if (string.IsNullOrWhiteSpace(transcript))
            throw new InvalidOperationException("В чате ещё нет сообщений");

        var prompt =
            "Ниже — переписка чата. Придумай короткое название (3-6 слов, по-русски, без кавычек и точки в конце), " +
            "отражающее суть текущего разговора. " + Llm.TitleExtraction.JsonHint + "\n\n" + transcript;
        var raw = await _cheap.RunAsync(Llm.LocalActionCatalog.ChatRetitle, prompt,
            _config["Notes:AiModel"] ?? "haiku", userId, jsonFormat: Llm.TitleExtraction.Schema, ct: ct);
        var line = Llm.TitleExtraction.Extract(raw);
        if (line is null || line.Length > 80)
            throw new InvalidOperationException("Модель вернула пустое название");

        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        entry.Info.Name = line;
        entry.Info.NameLocked = true; // явное действие пользователя — авто больше не трогает
        // Архивный чат из архива не выводим: «Обновить название» доступен и в разделе
        // «Архив», а правка названия — не активность разговора (признак архива производный)
        if (!entry.Info.IsArchived) entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastChatRenamedAsync(sessionId, entry.Info, line);
        return entry.Info;
    }

    // Подобрать значок-иконку ОДНОМУ чату по переписке. Имя не трогает и NameLocked не ставит —
    // в отличие от RetitleAsync (тот переименовывает). null — значок поставить не вышло
    // (нет переписки, модель не дала имя, чат исчез). Зовётся и пакетным прогоном.
    public async Task<Session?> SetChatIconAsync(string userId, string sessionId, CancellationToken ct)
    {
        var session = GetOwned(sessionId, userId);
        if (session is null) return null;
        if (_cheap is null) throw new InvalidOperationException("ИИ недоступен");

        // Транскрипт короткий: для темы разговора сути хватает, лишние токены ни к чему
        var history = await GetHistoryAsync(sessionId);
        var transcript = SessionSummaryService.BuildTranscript(history, 1500);
        if (string.IsNullOrWhiteSpace(transcript)) return null;

        var prompt = Llm.TitleExtraction.IconHint + "\n\n" + transcript;
        var raw = await _cheap.RunAsync(Llm.LocalActionCatalog.ChatTitle, prompt,
            ownerId: userId, jsonFormat: Llm.TitleExtraction.SchemaIcon, ct: ct);
        var iconName = Llm.TitleExtraction.ExtractIconName(raw);
        if (iconName is null)
        {
            // Диагностика: модель не дала валидное PascalCase-имя. Логируем сырой ответ,
            // чтобы понять — пустой {} (отступила), не-PascalCase (опечатка/пробел) или мусор.
            // Обрезаем: модели иногда шлют длинные рассуждения вслух
            _log.LogInformation("Значок чата {Session} («{Name}»): модель не дала имя. Ответ: {Raw}",
                sessionId, session.Name, raw is null ? "<null>" : (raw.Length > 300 ? raw[..300] + "…" : raw));
            return null;
        }

        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        // Повторная проверка: пока модель думала, значок мог поставить авто-заголовок нового
        // чата — не перезаписываем то, что уже стоит
        if (!string.IsNullOrEmpty(entry.Info.Topic)) return entry.Info;
        entry.Info.Topic = iconName;
        // Архивный чат из архива не выводим — зеркало гейта в RetitleAsync и предфильтра
        // пакетного прогона (SetChatIconsAsync): значок — не активность разговора
        if (!entry.Info.IsArchived) entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        // Имя не менялось — шлём его же: событие переносит и значок, отдельного не заводим
        await BroadcastChatRenamedAsync(sessionId, entry.Info, entry.Info.Name ?? "");
        return entry.Info;
    }

    // Результат пакетного прогона значков: сколько чатов получили иконку и сколько пропущено
    // (значок уже стоит, нет переписки, модель не дала имя). Идёт в тост палитры.
    public sealed record IconBatchResult(int Processed, int Skipped);

    // Подобрать значки чатам без него в рамках проекта (projectId) или, если проект не задан,
    // по всем чатам владельца. Действие AI-палитры «Проставить значки тем» в разделе проекта —
    // разовый проход. Чаты со значком отсеиваются ДО вызова модели (экономия вызовов), поэтому
    // в processed попадают только реально размеченные этим прогоном. Каждый оставшийся чат —
    // отдельный вызов модели: на десятках чатов это десятки секунд.
    public async Task<IconBatchResult> SetChatIconsAsync(string userId, CancellationToken ct, string? projectId = null)
    {
        var all = _sessions.Values
            .Where(e => ResolveOwnerId(e.Info) == userId && (projectId is null || e.Info.ProjectId == projectId))
            .Select(e => e.Info)
            .ToList();
        // Предфильтр: у кого значок уже есть — сразу в пропущенные, модель не зовём.
        // Архивные — туда же: у старых чатов Topic как раз пуст (значки появились позже),
        // и один клик «Проставить значки» выводил бы из архива ВСЕ старые чаты (UpdatedAt
        // двигается в SetChatIconAsync) и заказывал сотни вызовов модели
        var pending = all
            .Where(s => string.IsNullOrEmpty(s.Topic) && !s.IsArchived)
            .Select(s => s.Id)
            .ToList();
        var processed = 0;
        var skipped = all.Count - pending.Count;
        foreach (var id in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var updated = await SetChatIconAsync(userId, id, ct);
                if (updated is not null) processed++; else skipped++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogDebug(ex, "Определение значка чата {Session}", id); skipped++; }
        }
        return new IconBatchResult(processed, skipped);
    }

    // Уведомить клиентов об авто-переименовании чата (адресация как у BroadcastChatDeletedAsync)
    private async Task BroadcastChatRenamedAsync(string sessionId, Session info, string name)
    {
        var msg = new ChatRenamedMessage(name, info.Topic) with { SessionId = sessionId };
        var tasks = new List<Task> { _broadcaster.ToSession(sessionId, msg) };
        if (info.ProjectId is string pid)
            tasks.Add(_broadcaster.ToProject(pid, msg));
        else if (info.OwnerId is string oid)
            tasks.Add(_broadcaster.ToOwner(oid, msg));
        await Task.WhenAll(tasks);
    }

    // Занят ли чат ходом ПРЯМО СЕЙЧАС. Status для этого не годится: у свежего чата он ещё
    // Starting, а между стартом хода и сменой статуса есть реальное окно. Занятость адаптера
    // спрашиваем каноническим Busy (живой прогон ИЛИ фолбэк-оркестрация): в паузе между
    // попытками цепочки прогона CLI нет, но ход идёт — и оркестратор в этот момент сам
    // копирует транскрипт, так что смена провайдера под ним разошлась бы с его restore.
    // Плюс признаки, которых Busy не знает: ход, принятый адаптером но ещё не поднявший
    // процесс, awaited-ход агента (TurnWaiter) и непустая серверная очередь — её сообщения
    // уедут в тот же чат, и провайдера менять под ними нельзя.
    private static bool HasTurnInFlight(SessionEntry entry)
    {
        if (Busy(entry.Process) || entry.Process is { HasQueuedTurn: true }) return true;
        if (entry.TurnWaiter is not null) return true;
        lock (entry.PendingLock) return entry.Pending.Count > 0;
    }

    // Публичная обёртка HasTurnInFlight для API-слоя (архивация чата, шаг 2 плана
    // «Архив чатов»): нужен тот же гейт, что у смены провайдера, — Status для этого не
    // годится (у свежего чата Starting, между стартом хода и сменой статуса есть окно).
    // false и для отсутствующего чата — гейт по чужому id не должен падать, caller
    // всё равно ответит 404 по GetOwned.
    public bool HasTurnInFlight(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) && HasTurnInFlight(entry);

    // Редактирование названия и модели. Модель применяется со следующего хода
    // (процесс claude пересоздаётся в RunTurnAsync), Info — общая ссылка с адаптером.
    //
    // PATCH-семантика: null = «поле не передано, не трогать». Иначе частичные апдейты
    // (MCP chats_update только с name; PUT {pinned} из togglePin) затирали бы модель/имя.
    //
    // ownerId — владелец, от чьего имени идёт правка (UserId контроллера). Единственная
    // мутирующая точка, где его раньше не было: смена провайдера уходит в
    // MigrateProviderAsync, а тот проверяет владение — брать владельца из самой сессии
    // означало бы сверять её саму с собой.
    public async Task<Session?> UpdateAsync(string sessionId, string ownerId, string? name, string? model,
        string? effort, List<string>? tags = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;

        if (model is not null)
        {
            var newModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
            // Провайдера резолвим по ЭФФЕКТИВНЫМ моделям: пустая означает «по назначению места»,
            // а назначение может указывать на модель стороннего провайдера. По сырому null
            // возврат glm-чата к «По умолчанию» (при назначении на glm) выглядел бы как переезд
            // на claude и упирался бы в guard, а переход claude-чата на «По умолчанию» с чужой
            // моделью, наоборот, проскакивал бы мимо guard и ломал транскрипт.
            var usageKey = UsageKeyFor(entry.Info.TaskExecution, entry.Info.TaskId, entry.Info.PersonaId);
            var effectiveNew = _assignments.Resolve(usageKey, newModel, entry.Info.OwnerId);
            var effectiveCur = _assignments.Resolve(usageKey, entry.Info.Model, entry.Info.OwnerId);

            // Смена провайдера: контекст сессии живёт у провайдера (транскрипт эндпоинта),
            // и «переехавшая» сессия молча потеряла бы его. Прежний запрет «нельзя у начатой
            // сессии» держался ровно на этом, но с тех пор в продукте завелась миграция
            // транскрипта — та же, что кнопка «Продолжить на …» даёт живому разговору.
            // Значит вопрос не в том, начат ли чат, а в том, есть ли что переносить: этим
            // занимается MigrateProviderAsync.
            var newProvKey = _llmProviders.ResolveByModel(effectiveNew)?.Key;
            var curProvKey = _llmProviders.ResolveByModel(effectiveCur)?.Key;
            var migrated = false;
            if (newProvKey != curProvKey)
            {
                // Единственная причина отказа — ход прямо сейчас в полёте: смена провайдера
                // на лету увела бы ход в другой профиль CLI посреди прогона. Проверка стоит
                // ЗДЕСЬ, а не внутри MigrateProviderAsync: кнопка «Продолжить на …» аварийная
                // (приходит при исчерпании лимита, когда ход ещё формально жив), и такая
                // проверка сломала бы её.
                if (HasTurnInFlight(entry))
                    throw new InvalidOperationException(
                        "Идёт ответ ассистента — дождитесь его завершения, затем смените модель");

                try
                {
                    // effectiveNew = null — выбрали «По умолчанию», а назначение места модель не
                    // даёт: это переезд на родной Claude без закреплённой модели, а не «модель не
                    // указана». MigrateProviderAsync такой вызов понимает.
                    await MigrateProviderAsync(sessionId, ownerId, effectiveNew);
                    // Сброс на «По умолчанию» не должен закреплять модель: MigrateProviderAsync
                    // кладёт в Info.Model то, что ему передали (эффективную), и чат переставал бы
                    // следовать назначению места. Provider внутри уже выставлен.
                    if (newModel is null) entry.Info.Model = null;
                    migrated = true;
                }
                catch (ProviderUnchangedException)
                {
                    // Здесь провайдеров сравнивали по ЭФФЕКТИВНЫМ моделям, а миграция — по сырым
                    // (Info.Model ?? Info.Provider), и после переставленного назначения места эти
                    // две картины расходятся: чат с Model = null числится на claude (или на своём
                    // аккаунте пула), а по назначению эффективно уезжает на glm; выбор opus
                    // миграция видит как claude → claude. По сырым полям переносить нечего, так
                    // что это не повод валить 400 весь PATCH (вместе с name/effort/tags) —
                    // просто закрепляем модель обычной веткой ниже. Настоящая неизвестная
                    // модель сюда не попадает: у неё свой тип (InvalidOperationException) и
                    // честный 400 наружу.
                }
            }
            if (!migrated)
            {
                // В Info.Model кладём именно то, что выбрали: null = «следовать настройке»
                // (резолвится на каждом ходу, смена настройки подхватывается сама)
                entry.Info.Model = newModel;
                // Провайдер ВСЕГДА выводится из модели (комментарий LlmProviderRegistry: модель —
                // единственный источник правды, Provider не персистится как самостоятельное значение).
                // Инцидент 14.08.2026: пересчёт был только «в стороннего», и смена glm→opus[1m]
                // (пилюля модели в NewChatSetup до первого хода) оставляла пару (Claude-модель, ключ
                // glm) — CLI стартовал в профиле glm с моделью Anthropic → мгновенный 401.
                if (effectiveNew is not null && _llmProviders.ResolveByModel(effectiveNew) is { } newProv)
                    entry.Info.Provider = newProv.Key;
                // Родной Claude (ResolveByModel == null, в т.ч. пул подписок): ключ — из пула
                // (Pick сам отфильтрует исчерпанные/auth-dead, при пустом пуле — PrimaryKey),
                // модель меняем на лету у живого хода — применится к его последующим round-trip'ам.
                else if (effectiveNew is not null)
                {
                    // Чат уже сидит на аккаунте пула, который эту модель тянет — Provider НЕ
                    // трогаем: транскрипт лежит в профиле именно этого аккаунта, а Pick при
                    // равных тарифах тай-брейкает случайно (LeastLoaded, deterministic: false).
                    // Переезд без переноса .jsonl и без AdapterStale не виден сразу (живой
                    // адаптер держит старый корень), но после рестарта --resume идёт в чужой
                    // профиль — «No conversation found» и разговор с нуля. Pick нужен ровно
                    // там, где текущий ключ модель не тянет (пин Opus на плане без Opus) либо
                    // это возврат со стороннего провайдера/чат вообще без аккаунта пула.
                    var cur = entry.Info.Provider;
                    if (cur is null || !_subscriptionPool.All.Any(s => s.Key == cur)
                        || !_subscriptionPool.SupportsModel(cur, effectiveNew))
                        entry.Info.Provider = _subscriptionPool.Pick(effectiveNew);
                    entry.Process?.TrySetModelLive(effectiveNew);
                }
            }
        }

        if (name is not null)
        {
            var trimmed = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            entry.Info.Name = trimmed;
            // Явно заданное имя (вручную/MCP chats_update) — лочим от авто-переименования;
            // очистка имени (пустое) снимает лок, чтобы авто-заголовок мог сработать снова.
            entry.Info.NameLocked = trimmed is not null;
        }
        if (effort is not null)
            entry.Info.Effort = string.IsNullOrWhiteSpace(effort) ? null : effort.Trim();
        if (tags is not null)
            entry.Info.Tags = tags;

        // Отметка времени — только когда что-то реально правили. PUT приходит и с одними
        // настройками (срок хранения, тумблер уведомлений — их применяет контроллер до этого
        // вызова, сюда все поля доезжают как null): безусловный UpdatedAt поднимал бы чат
        // в списке и метил непрочитанным просто за смену настройки.
        // Архивный чат из архива не выводим: правка имени/модели/тегов доступна и в разделе
        // «Архив», а признак архива производный — UpdatedAt снимает его сам.
        if (name is not null || model is not null || effort is not null || tags is not null)
            if (!entry.Info.IsArchived)
                entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        return entry.Info;
    }

    /// <summary>Явно перевести чат с окна 1M на базовое (200K): срезать суффикс [1m] у модели.</summary>
    /// Единственный путь, которым суффикс окна снимается по воле человека — кнопка «продолжить в
    /// стандартном окне» под карточкой отказа Window1MUnavailable. Автоматического среза больше
    /// нет нигде в ходе чата (он был тихой миной для длинных разговоров), поэтому решение
    /// «мне хватит 200K» принимает пользователь, а сервер только исполняет.
    ///
    /// Модель берём ЭФФЕКТИВНУЮ (Info.Model может быть пуста — тогда модель приходит от слота
    /// назначения места), а закрепляем в чате базовый алиас явно: иначе назначение места на
    /// следующем ходу вернуло бы окно 1M и человек снова упёрся бы в ту же карточку.
    /// Не тир-алиас с окном — снимать нечего, отказ (InvalidOperationException → 400).
    public async Task<Session?> DropWindow1MAsync(string sessionId, string ownerId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        var usageKey = UsageKeyFor(entry.Info.TaskExecution, entry.Info.TaskId, entry.Info.PersonaId);
        var effective = _assignments.Resolve(usageKey, entry.Info.Model, entry.Info.OwnerId);
        if (!LlmProviderRegistry.IsClaudeTierWindowAlias(effective))
            throw new InvalidOperationException("У чата не выбрано окно 1M — переключать нечего");
        return await UpdateAsync(sessionId, ownerId,
            name: null, model: LlmProviderRegistry.StripClaudeWindowAlias(effective), effort: null);
    }

    // Ответ на карточку, которой уже нет, — протухший: конец хода (result/error/exited) снял её
    // сам. Гонка живая: ватчдог обрывает зависший ход, а клик пользователя долетает мгновением
    // позже — раньше такой ответ безусловно ставил Working на мёртвом процессе, и чат залипал
    // в нём навсегда (новые сообщения уходят в Pending, разбор которой ждёт конца хода, а хода
    // уже не будет). Сверяем только наличие карточки, но не id: у одного хода их может быть
    // несколько (параллельные tool_use ждут каждый своего ответа), и ответ на неактуальную
    // из них — законный.
    private static bool IsStaleInteractionAnswer(string sessionId, SessionEntry entry, string what)
    {
        if (entry.PendingInteraction is not null) return false;
        Console.Error.WriteLine(
            $"[SessionManager] Протухший ответ ({what}) в сессии {sessionId}: карточки уже нет — игнорируем");
        return true;
    }

    public void RespondPermission(string sessionId, string requestId, string behavior)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (IsStaleInteractionAnswer(sessionId, entry, $"permission {requestId}")) return;
        // «Разрешать всегда» запоминаем НА СЕССИИ, а не в памяти адаптера: тот пересоздаётся
        // рестартом сервера, ленивым восстановлением чата и сменой собеседника, и человеку
        // приходилось жать «всегда» заново. Имя инструмента берём из висящей карточки —
        // в ответе клиента его нет. Сверяем requestId: у одного хода карточек может быть
        // несколько (параллельные tool_use), и ответ на неактуальную законен — но запомнить
        // «всегда» по чужой карточке значило бы выдать бессрочные права инструменту, которому
        // их не давали. Карточка не та (или имя пустое) — просто не запоминаем, ход идёт
        // своим чередом.
        if (behavior == "allow_always"
            && entry.PendingInteraction is PermissionRequestMessage
                { ToolName: { Length: > 0 } tool } pending
            && pending.RequestId == requestId
            && !entry.Info.AutoAllowTools.Contains(tool, StringComparer.OrdinalIgnoreCase))
        {
            entry.Info.AutoAllowTools.Add(tool);
            SaveSessions();
        }
        entry.Process?.RespondPermission(requestId, behavior);
        entry.PendingInteraction = null;
        // Вердикт — в тех же токенах, что хранит карточка на фронте (ChatItem.decision)
        var decision = behavior switch
        {
            "allow" => "allowed",
            "allow_always" => "always",
            _ => "denied",
        };
        FireAndForget(BroadcastAsync(sessionId,
                new InteractionResolvedMessage("permission", requestId, Decision: decision)),
            $"рассылка ответа на permission ({sessionId})");
        FireAndForget(ApplyStatusAsync(sessionId, entry, SessionStatus.Working),
            $"смена статуса после permission ({sessionId})");
    }

    // Снять инструмент с «Разрешать всегда» этого чата: следующий его вызов снова спросит.
    // Идемпотентно — инструмента в списке нет, отдаём сессию как есть (диск не трогаем).
    // Сравнение имён — OrdinalIgnoreCase, как при проверке в ClaudeSession.
    // null — сессии нет (владение проверяет контроллер, как у соседних эндпоинтов).
    // UpdatedAt не двигаем: настройка чата — не активность (см. соглашение о настройках).
    public Session? RemoveAutoAllowTool(string sessionId, string tool)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (entry.Info.AutoAllowTools.RemoveAll(t => string.Equals(t, tool, StringComparison.OrdinalIgnoreCase)) > 0)
            SaveSessions();
        return entry.Info;
    }

    // «Стоп» человека (хаб, доска агентов). Штаб останавливает исполнителя через
    // ITeamTurnIntake.InterruptTurn — та же механика, но без отметки в истории.
    public void Interrupt(string sessionId) => InterruptCore(sessionId, byUser: true);

    private void InterruptCore(string sessionId, bool byUser)
    {
        if (_sessions.TryGetValue(sessionId, out var entry))
        {
            // Живой локальный голосовой ход: отменяем и выходим ДО stuck-детекта и
            // заморозки очереди — exited придёт из самой ветки (RunLocalVoiceTurnAsync),
            // вернёт статус Active и разберёт очередь (drainOnDeadRun). Ход короткий
            // (1-3 с), composer_restore для него не нужен.
            if (entry.LocalVoiceCts is { } localCts)
            {
                if (byUser) RecordUserInterrupt(sessionId, entry);
                localCts.Cancel();
                return;
            }

            // Стоп пользователя прерывает и цикл «до готово»: снимаем СИНХРОННО,
            // чтобы exited прерванного хода не запустил автопродолжение
            if (entry.Info.WorkLoop is not null)
            {
                entry.Info.WorkLoop = null;
                // Прерванный ход result не пришлёт — погасим маркер итерации, иначе он
                // повис бы и заблокировал разбор очереди по концу следующего хода.
                entry.LoopTurnInFlight = false;
                SaveSessions();
                _ = BroadcastWorkLoopAsync(sessionId, entry);
            }
            // Чат числится занятым, а живого прогона нет: ход убил ватчдог/сбой, а статус
            // остался (или его выставил протухший ответ на карточку). Такой Working терминален —
            // сообщения уходят в Pending, разбор которой ждёт конца несуществующего хода.
            // «Стоп» — единственная кнопка пользователя в этом состоянии, поэтому вместо
            // прежнего no-op реанимируем чат (ниже, после общей уборки состояния хода).
            // HasQueuedTurn обязателен наравне с HasLiveTurn: прогон появляется только после
            // старта процесса CLI (секунды), и «Стоп», нажатый сразу после отправки, попадал
            // ровно в это окно — чат объявлялся зависшим, адаптер выбрасывался из-под живого
            // хода, и тот падал ObjectDisposedException'ом в ленту (диагноз 2026-08-15).
            var stuck = entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting
                && entry.Process is null or { HasLiveTurn: false, HasQueuedTurn: false };
            // Отметка в историю — только когда было что останавливать (чат занят, в том числе
            // зависший: человек нажал «Стоп» и видит отметку в живой ленте)
            if (byUser && entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting)
                RecordUserInterrupt(sessionId, entry);
            // «Стоп» замораживает очередь (не чистит): сообщения остаются ждать возобновления,
            // а последнее пользовательское возвращается в композер (composer_restore).
            // При реанимации не замораживаем: размораживающего конца хода уже не будет,
            // и отложенные сообщения застряли бы в очереди насовсем.
            if (!stuck)
                _ = FreezePendingAsync(sessionId, entry);
            // M5: тот же сброс буфера маркеров, что при прерывании очередью (SendMessageAsync):
            // убитый ход даёт exited без result, буфер не потребляется — маркер мёртвого хода
            // (<escalate:*>, <team:work>, <no-reply/>) доклеился бы к следующему и применился
            // задним числом (фантомная эскалация, «волна-призрак»).
            lock (entry.TeamTurnLock)
            {
                entry.TeamTurnText.Clear();
                entry.TeamTurnShownLength = 0;
                entry.TurnSawAngleBracket = false;
                entry.TeamTurnAsked = false;
            }
            if (stuck)
                ReviveStuckSession(sessionId, entry);
            else
            {
                _log.LogInformation("Interrupt адаптера {Session}: callsite=stop (Interrupt(sessionId), stuck={Stuck})", sessionId, stuck);
                entry.Process?.Interrupt();
            }
        }
    }

    // Отметка «Ход остановлен пользователем» в history.json. Пишется ТОЛЬКО на прерывании
    // человеком — в точке, где его намерение известно наверняка: ниже по стеку (адаптер,
    // паспорт хода с исходом interrupted) «Стоп» человека уже не отличить от остановки
    // исполнителя штабом, а падение процесса и внутренние перезапуски хода эту точку не
    // проходят вовсе. Живая лента ставит такую же отметку сама, оптимистично.
    private void RecordUserInterrupt(string sessionId, SessionEntry entry)
    {
        if (entry.Accumulator is not { } acc || !acc.OnUserInterrupted()) return;
        FireAndForget(acc.SaveSnapshotAsync(_history), $"отметка прерывания хода ({sessionId})");
    }

    // Возврат зависшего чата в рабочее состояние: снимаем ожидающую карточку, выбрасываем
    // отравленный адаптер и переводим статус в Active. Просто сбросить статус мало — у адаптера
    // мог остаться захваченный зависшей финализацией _turnLock, и первое же сообщение встало бы
    // намертво снова; DisposeAsync (в нём _cts.Cancel) разблокирует ожидателей. Следующий ход
    // поднимет свежий адаптер через EnsureProcessAsync с --resume по ClaudeSessionId —
    // контекст переписки цел.
    private void ReviveStuckSession(string sessionId, SessionEntry entry)
    {
        entry.PendingInteraction = null;
        if (entry.Process is { } dead)
        {
            entry.Process = null;
            FireAndForget(dead.DisposeAsync().AsTask(),
                $"уборка адаптера зависшего хода ({sessionId})");
        }
        FireAndForget(ReviveStuckSessionAsync(sessionId, entry),
            $"реанимация зависшего чата ({sessionId})");
    }

    private async Task ReviveStuckSessionAsync(string sessionId, SessionEntry entry)
    {
        await ApplyStatusAsync(sessionId, entry, SessionStatus.Active);
        await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "stuck_reset",
            "Зависший ход сброшен — чат снова доступен.");
    }

    // === КР-наблюдаемость, этап 3: перезапуск хода штаба без потери работы ===

    // Повреждённый транскрипт: resume запрещён, человеку предлагаем начать ход заново.
    // Отдельный тип, а не текст в InvalidOperationException: контроллер различает отказ
    // гейта (просто текст) и этот случай (code=transcript_damaged + кнопка «начать заново»).
    public sealed class TurnTranscriptDamagedException(string message) : InvalidOperationException(message);

    // Результат перезапуска хода. Resumed=true — следующий ход продолжит разговор по
    // транскрипту (--resume); false — ход начнётся заново без старого контекста (startFresh).
    public sealed record StuckTurnRestartResult(bool Resumed, string Message);

    // Ожидание смерти процесса после kill: Kill асинхронен, и «вызвали kill» ≠ «процесс
    // мёртв». internal — тесты сужают до сотен миллисекунд; прод живёт 20 с (Kill плюс
    // WaitForExitAsync внутри финализации адаптера занимает до ~10 с).
    internal TimeSpan KillWaitTimeout { get; set; } = TimeSpan.FromSeconds(20);

    // Порог «живой ход начался недавно — убивать рано» (защита честной долгой работы от
    // случайного перезапуска). Тот же ключ конфига и дефолт, что у пульса волны
    // (TeamWaveService._quietThreshold): гейт и индикация обязаны соглашаться по порогу.
    private readonly TimeSpan _freshTurnThreshold;

    // Гейт повторного вызова: перезапуск — это kill → уборка адаптера → revive, второй
    // параллельный вызов того же чата (двойной клик) плодил бы процессы и карточки.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _turnRestarts = new();

    // Перезапуск зависшего хода (главный сценарий этапа 3): чат занят, процесс молчит,
    // написать в него нельзя. Строго по шагам: проверка живости → kill → дождаться смерти →
    // валидация транскрипта → revive → новый ход с --resume (разбор отложенной очереди:
    // EnsureProcessAsync поднимёт процесс с --resume по ClaudeSessionId). Рецепт уборки —
    // тот же, что ReviveStuckSession (карточка → адаптер → статус), но отдельной сборкой:
    // та шлёт СВОЮ карточку «зависший ход сброшен» fire-and-forget, а здесь статус обязан
    // встать в Active до разбора очереди и с карточкой перезапуска. Стадия, бюджет и версия
    // плана живут на Session.TeamImplement и этим путём не трогаются — режим переживает.
    // Ходов модели сам путь не запускает и квоту пробуждений не тратит: до модели доехают
    // только сообщения, уже стоявшие в очереди до зависания.
    public async Task<StuckTurnRestartResult> RestartStuckTurnAsync(string sessionId, bool startFresh)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            throw new InvalidOperationException("Сессия не найдена");
        // Гейт режима: кнопка и тексты — про штаб «Командной реализации», семантика
        // перезапуска (kill → resume хода штаба) работает только там. Обобщение на
        // все чаты — отдельное решение, не побочный эффект.
        if (entry.Info.TeamImplement is null)
            throw new InvalidOperationException(
                "Перезапуск хода доступен только в чате штаба «Командной реализации»");
        if (!_turnRestarts.TryAdd(sessionId, 0))
            throw new InvalidOperationException("Ход уже перезапускается — дождитесь результата");
        try
        {
            // 1. Живость: чат обязан быть занят — свободному чату перезапуск не нужен.
            // Спасательная ветка (major ревью): первый вызов успел убить процесс и
            // вернуть 409 transcript_damaged, финализация убитого прогона перевела
            // чат в Active, а отложенная очередь умерла на --resume по тому же битому
            // файлу. Чат свободен, но resume-якорь отравлен — без пропуска здесь
            // повторный startFresh вечно упирался бы в «Чат не занят», и у чата не
            // было бы штатного выхода из повреждённого транскрипта.
            if (entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting))
            {
                var rescue = startFresh
                    && FindResumeTranscript(entry) is { } damaged
                    && !Llm.Claude.TranscriptProbe.IsTailIntact(damaged);
                if (!rescue)
                    throw new InvalidOperationException(
                        "Чат не занят: ход не идёт. Просто напишите сообщение — оно продолжит разговор");
            }
            // Штаб ждёт ответа человека на карточку (разрешение/вопрос/план) — это не
            // зависание: ответ лежит в ленте чата, прерывать ожидание рестартом нельзя
            if (entry.PendingInteraction is not null)
                throw new InvalidOperationException(
                    "Штаб ждёт вашего ответа на карточку в чате — перезапуск не нужен, ответьте на неё");
            // Живой прогон, начавшийся недавно, — честная работа, а не зависание (тот же
            // порог тишины, что у пульса волны). Долгий немой ход прервать можно — с
            // сохранением контекста через resume.
            var quiet = DateTime.UtcNow - entry.Info.UpdatedAt;
            if (HasLiveTurnProcess(sessionId) && quiet < _freshTurnThreshold)
                throw new InvalidOperationException(
                    $"Штаб работает — {(int)Math.Max(0, quiet.TotalMinutes)} мин назад была активность. " +
                    "Дождитесь результата или остановите ход кнопкой «Стоп»");

            // 2-3. Kill и ОЖИДАНИЕ смерти: HasLiveTurn гаснет в финализации прогона — уже
            // после WaitForExit самого процесса, поэтому поллинг по нему и есть ожидание
            // настоящей смерти, а не факта вызова kill.
            if (entry.Process is { HasLiveTurn: true })
            {
                entry.Process.Interrupt();
                var deadline = DateTime.UtcNow + KillWaitTimeout;
                while (entry.Process is { HasLiveTurn: true })
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new InvalidOperationException(
                            "Не удалось остановить зависший процесс — попробуйте ещё раз через минуту");
                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                }
            }

            // 4. Валидация транскрипта: повреждённый (оборванная последняя строка) через
            // --resume не продолжится. Файл не нашёлся — проверку пропускаем: профиль
            // подписки пула может не входить в AllowedRoots, и ложный «повреждён» на
            // здоровом разговоре хуже пропущенной проверки (CLI сам скажет при resume).
            // startFresh идёт мимо — транскрипт всё равно выбрасываем.
            if (!startFresh && FindResumeTranscript(entry) is { } transcript
                && !Llm.Claude.TranscriptProbe.IsTailIntact(transcript))
                throw new TurnTranscriptDamagedException(
                    "Файл разговора повреждён — продолжить с прежним контекстом нельзя. " +
                    "Можно начать ход заново: старый контекст будет потерян, чат снова заработает");

            // 5. Revive: убираем ожидающую карточку и отравленный адаптер (у него мог
            // остаться захваченным _turnLock зависшей финализацией — DisposeAsync с
            // _cts.Cancel разблокирует ожидателей), статус в Active.
            entry.PendingInteraction = null;
            if (entry.Process is { } dead)
            {
                entry.Process = null;
                FireAndForget(dead.DisposeAsync().AsTask(),
                    $"уборка адаптера перезапущенного хода ({sessionId})");
            }
            // M5 — тот же сброс буфера маркеров, что у Interrupt: убитый ход result не
            // пришлёт, буфер не потребляется, и маркер мёртвого хода (<escalate:*>,
            // <team:work>) доклеился бы к следующему и применился задним числом.
            lock (entry.TeamTurnLock)
            {
                entry.TeamTurnText.Clear();
                entry.TeamTurnShownLength = 0;
                entry.TurnSawAngleBracket = false;
                entry.TeamTurnAsked = false;
            }
            // «Начать заново»: снимаем resume-якорь — следующий адаптер стартует без
            // --resume, CLI заведёт новую сессию и пришлёт новый id (init ниже допишет)
            if (startFresh && entry.Info.ClaudeSessionId is not null)
            {
                entry.Info.ClaudeSessionId = null;
                SaveSessions();
            }
            await ApplyStatusAsync(sessionId, entry, SessionStatus.Active);
            // Честный текст (minor ревью): продолжение разговора обещаем, только если
            // в очереди правда что-то стоит или активный цикл продолжит себя сам —
            // при пустой очереди нового хода не будет, и «разговор продолжится
            // с того же места» обещало бы его напрасно.
            bool willContinue;
            lock (entry.PendingLock) willContinue = !entry.QueueFrozen && entry.Pending.Count > 0;
            willContinue |= entry.Info.WorkLoop is not null;
            await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "team_restart",
                startFresh
                    ? "Ход перезапущен вручную с чистого листа: прежний контекст утерян — напишите, что делать дальше"
                    : willContinue
                        ? "Ход перезапущен вручную — разговор продолжится с сохранённым контекстом"
                        : "Ход перезапущен вручную — чат снова доступен, контекст сохранён");

            // Новый ход с --resume: сообщения, отложенные зависшим ходом, уходят обычной
            // доставкой (DrainInFlight гасит параллельные разборы). Замороженная «Стоп»
            // очередь не разбирается — её держал человек, не мы.
            await DrainNextPendingAsync(sessionId);

            return new StuckTurnRestartResult(
                Resumed: !startFresh,
                Message: startFresh
                    ? willContinue
                        ? "Ход начнётся заново — контекст сброшен, чат снова доступен"
                        : "Контекст сброшен, чат снова доступен — напишите сообщение, чтобы начать новый ход"
                    : willContinue
                        ? "Ход перезапущен: чат снова доступен, разговор продолжится с того же места"
                        : "Ход перезапущен: чат снова доступен — напишите сообщение, чтобы продолжить разговор");
        }
        finally { _turnRestarts.TryRemove(sessionId, out _); }
    }

    // Рабочая папка хода для поиска транскрипта: дерево чата, иначе корень проекта.
    // Точность тут не критична — TranscriptProbe при промахе по конвенции сканирует
    // все проекты зарегистрированных корней, cwd лишь сужает первый поиск.
    private string ResolveTurnCwd(Session info) =>
        info.WorktreePath
        ?? (info.ProjectId is { } pid ? _projects.GetById(pid)?.RootPath : null)
        ?? "";

    // Транскрипт resume-якоря чата: null — якоря нет или файл не найден (проверку
    // целостности тогда пропускаем). Обе точки RestartStuckTurnAsync — гейт
    // спасательной ветки и валидация перед resume — ищут файл одним путём.
    // У локального проекта всегда null: транскрипт на устройстве, его целость проверит сам CLI
    // на --resume и честно уронит ход (TurnFailureText.ResumeTranscriptMissing), а промах
    // поиска по серверному пути нельзя путать с «транскрипта нет».
    private string? FindResumeTranscript(SessionEntry entry) =>
        entry.Info.ClaudeSessionId is { } csid && TranscriptOnServer(entry.Info)
            ? Llm.Claude.TranscriptProbe.FindMainTranscript(ResolveTurnCwd(entry.Info), csid)
            : null;

    // Дефолт лимитов цикла «до готово». Невалидное значение конфига (не число или ≤ 0)
    // сваливается в дефолт, а не молча отрубает цикл: MaxIterations=0 иначе даёт
    // немедленную остановку по лимиту. internal — тестируется напрямую (SessionManagerTests).
    internal static int LoopLimitOrDefault(string? raw, int defaultValue = 20) =>
        int.TryParse(raw, out var v) && v > 0 ? v : defaultValue;

    // Включение/выключение цикла «до готово» (флаг work-loop). Включение сбрасывает
    // счётчик итераций; лимит — из конфига Loop:MaxIterations (дефолт 20, при ≤0 — тоже
    // дефолт, см. LoopLimitOrDefault).
    // userId задан (вызов из API) — сверяется с владельцем; null — внутренний вызов.
    // Режим прав, выбранный в Composer. Раньше он доезжал до сессии только вместе с
    // сообщением (см. SendMessageAsync), и выбор, сделанный до первого хода, терялся при
    // уходе со страницы: UI перечитывал Session.Mode и показывал прежний режим.
    // На сам ход это не влияет — процесс claude всё равно пересоздаётся в RunTurnAsync
    // и читает --permission-mode из Info.Mode.
    public Session? SetMode(string sessionId, string mode, string? userId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (userId is not null && ResolveOwnerId(entry.Info) != userId) return null;
        if (!Enum.TryParse<ClaudeMode>(mode, true, out var parsed))
            throw new InvalidOperationException($"Неизвестный режим: {mode}");
        // Штаб думает (Э8): на стадиях интервью и планирования чат держится в план-режиме, а
        // селектор у человека заблокирован — менять режим мимо него нельзя, иначе правки
        // перестали бы упираться в permission-механику там, где план ещё не согласован.
        // Признак — навязанный режим (SavedMode), а не стадия: у провайдера без поддержки
        // плана мы деградировали и ничего не навязывали, блокировать там нечего.
        if (entry.Info.TeamImplement is { SavedMode: not null } && parsed != ClaudeMode.Plan)
            throw new InvalidOperationException(
                "Штаб планирует. Режим вернётся после согласования плана");
        // «План» у провайдера без поддержки не принимаем — та же защита, что и на ходе
        var caps = _llmProviders.CapabilitiesFor(entry.Info.Model);
        if (parsed == ClaudeMode.Plan && !caps.SupportsPlanMode)
            throw new InvalidOperationException("Провайдер не поддерживает режим «План»");
        // M1: гард «координатор не пишет код» держится на permission-запросах CLI, а в
        // acceptEdits/bypassPermissions CLI не спрашивает — такой режим штабу запрещён в
        // любой точке смены, не только при включении режима (аудит 2026-08-01: обход
        // селектором в стадии волны → координатор писал файлы мимо задач).
        if (entry.Info.TeamImplement is { } teamForGuard)
            parsed = PermissionModeGuard.GuardCompatibleMode(parsed, teamForGuard.CoordinatorNoCode);
        if (entry.Info.Mode == parsed) return entry.Info;
        entry.Info.Mode = parsed;
        SaveSessions();
        // Живой ход перенастраиваем на лету (control-протокол set_permission_mode):
        // новый режим применяется уже к идущему ходу, а не только со следующего.
        entry.Process?.TrySetPermissionModeLive(parsed);
        return entry.Info;
    }

    // manual=true — отключение по кнопке «Остановить цикл» в UI (в отличие от внутренних
    // отключений из ContinueWorkLoopAsync по лимиту/ошибке — те шлют СВОЁ сообщение сами,
    // до вызова этого метода, и manual=false, чтобы не задваивать ленту).
    public async Task<Session?> SetWorkLoopAsync(string sessionId, bool enabled, string? userId = null,
        bool manual = false)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (userId is not null && ResolveOwnerId(entry.Info) != userId) return null;

        // Гард B4: автопилот и «Командная реализация» одновременно ломают ход — см.
        // SessionModeConflictException.
        if (enabled && entry.Info.TeamImplement is not null)
            throw new SessionModeConflictException(
                "Автопилот недоступен в чате «Командной реализации» — здесь работа идёт через задачи исполнителям.");

        // ADR-008 («Два уровня, которые нельзя смешивать»): автопродолжение work-loop
        // в десктопном чате запрещено. Цикл ведёт агента по итерациям без человека, а вся
        // модель грани держится на том, что человек подтверждает каждое действие на
        // устройстве. Выключение не запрещаем — вернуть false всегда можно.
        if (enabled && entry.Info.DesktopChat)
            throw new SessionModeConflictException(
                "Цикл «до готово» недоступен в десктопном чате: агент не должен действовать на " +
                "вашем компьютере без подтверждения каждого действия.");

        var wasEnabled = entry.Info.WorkLoop is not null;
        // Присвоение WorkLoop и очистку буфера хода держим под одним локом: иначе обнуление
        // поля состязается с чтением в ContinueWorkLoopAsync (там же инкремент Iteration
        // под локом), и потребитель мог бы увидеть уже выключенный объект и уйти в мусор.
        var newLoop = enabled
            ? new SessionWorkLoop
            {
                MaxIterations = LoopLimitOrDefault(_config["Loop:MaxIterations"]),
            }
            : null;
        lock (entry.LoopTurnLock)
        {
            entry.Info.WorkLoop = newLoop;
            entry.LoopTurnText.Clear();
            // При выключении гасим маркер итерации: висящий LoopTurnInFlight заблокировал бы
            // разбор очереди по концу следующего хода (гейт !LoopTurnInFlight).
            if (newLoop is null)
                entry.LoopTurnInFlight = false;
        }
        SaveSessions();
        await BroadcastWorkLoopAsync(sessionId, entry);

        // Явное сообщение в ленту (B5): иначе гаснет только бейдж, и непонятно, доделана
        // работа или брошена — вторая фраза важна, текущий ход после снятия цикла продолжается.
        if (manual && wasEnabled && !enabled)
            await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "manual",
                "Цикл остановлен вами. Текущий ход продолжает работу.");

        // Агентские сообщения, скопившиеся за время цикла (они ждали конца ВСЕГО цикла),
        // при выключении не должны дожидаться следующего пользовательского хода — запускаем
        // разбор очереди. В фоне: SetWorkLoopAsync вызывается в т.ч. из ContinueWorkLoopAsync
        // по result. НО только когда текущий ход цикла уже завершился (статус не Working/Waiting):
        // ручной стоп приходит ВО ВРЕМЯ хода, и drain миновал бы гейт занятости SendMessageAsync(auto)
        // → второй ход в живой процесс. Ничего не теряется — по result текущего хода отработает
        // штатный drain (там WorkLoop уже null).
        if (wasEnabled && !enabled && entry.Info.Status is not (SessionStatus.Working or SessionStatus.Waiting))
            _ = Task.Run(async () =>
            {
                try { await DrainNextPendingAsync(sessionId); }
                catch (Exception ex) { Console.Error.WriteLine($"[SessionManager] Разбор очереди после стопа цикла ({sessionId}): {ex.Message}"); }
            });

        return entry.Info;
    }

    // Персистит и рассылает явную остановку цикла «до готово» (B5) — лимит итераций, ошибка
    // хода или ручной стоп. Reason ∈ limit|error|manual — контракт для фронта (Кира, п.5).
    private async Task AddWorkLoopStoppedNoticeAsync(string sessionId, SessionEntry entry, string reason, string text)
    {
        if (entry.Accumulator is not { } acc) return;
        acc.Append(new StoredWorkLoopStoppedMessage(reason, text));
        await acc.SaveSnapshotAsync(_history);
        await BroadcastAsync(sessionId, new WorkLoopStoppedMessage(reason, text));
    }

    private Task BroadcastWorkLoopAsync(string sessionId, SessionEntry entry)
    {
        var loop = entry.Info.WorkLoop;
        return BroadcastAsync(sessionId, new WorkLoopMessage(
            loop is not null, loop?.Iteration ?? 0, loop?.MaxIterations ?? 0, loop?.Phase,
            loop?.WaitingReason, loop?.WaitingTicks ?? 0));
    }

    // Режим «Командная реализация»: вкл/выкл режима чата-штаба. Тело переехало в
    // TeamEnableService (волна Ж): гарды B2/B4, снимок «оборванной волны», возврат режима
    // человека при выключении, правка настроек поверх активного режима (M4), тройная
    // синхронизация Mode/CLI/AdapterStale через шов — собственное дело вертикали,
    // единое тело держит все развилки «выкл посреди интервью/волны/проверки». Обёртка
    // сохранена ради публичной сигнатуры: контроллеры и тесты зовут по этому контракту.
    public Task<Session?> SetTeamImplementAsync(string sessionId, bool enabled,
        bool autoWaves = true, string? coordinatorPersonaId = null, string? plannerPersonaId = null,
        IReadOnlyCollection<string>? executorPersonaIds = null, string? userId = null,
        bool coordinatorNoCode = true)
        => _teamEnable.SetTeamImplementAsync(sessionId, enabled, autoWaves, coordinatorPersonaId,
            plannerPersonaId, executorPersonaIds, userId, coordinatorNoCode);

    // Причина, по которой режим включать нельзя — до единого хода интервью (B2 приёмки).
    // Тело переехало в TeamStateService (волна А): тонкая обёртка сохраняет публичную
    // сигнатуру для тестов и внешних вызывающих. Публичный (волна Ж): TeamEnableService
    // зовёт при включении режима — гард B2 приёмки (нет координатора / пустой состав)
    // должен срабатывать ДО того, как сессия становится режимной.
    public (string Code, string Message)? TeamImplementSetupError(Session session,
        string? coordinatorPersonaId, IReadOnlyCollection<string>? executorPersonaIds) =>
        _teamState.TeamImplementSetupError(session, coordinatorPersonaId, executorPersonaIds);

    // Переключение авто-волн на ходу (из бейджа режима): не включает/выключает режим,
    // только флаг внутри. Режим не активен → поля не трогает, возвращает сессию как есть.
    // Тело в TeamEnableService (волна Ж); обёртка сохранена ради публичной сигнатуры.
    public Task<Session?> SetTeamImplementAutoAsync(string sessionId, bool autoWaves, string? userId = null)
        => _teamEnable.SetTeamImplementAutoAsync(sessionId, autoWaves, userId);

    // Режим прав, совместимый с гардом «координатор не пишет код», переехал в Core
    // (`ClaudeHomeServer.Services.PermissionModeGuard`) — там же, где тип `ClaudeMode`. Ядро
    // SessionManager и штаб TeamWaveService зовут его из Core по новому пути.

    // Вход в план-режим стадий интервью и планирования (Э8). Тело переехало в
    // TeamStateService (волна Б): управление режимом хода — собственное дело вертикали,
    // а ядро держит только рантайм-поля и доступ к Process. Обёртка сохранена ради
    // сигнатуры (вызовы идут из нескольких точек этого класса).
    // Публичный (волна Г): TeamDecisionService.StartTeamWorkAsync зовёт при входе
    // в план-фазу из Idle, не дёргая приватный шов.
    internal void EnterPlanPhaseMode(string sessionId)
        => _teamState.EnterPlanPhaseMode(sessionId);

    // Возврат режима человека после согласования плана (Confirming → Wave), при
    // выключении режима и в добавочном плане авто-волн (B1). Тело переехало в
    // TeamStateService (волна Б); обёртка сохранена по тем же причинам, что
    // EnterPlanPhaseMode. Публичный (волна В): TeamPlanService зовёт из публикации
    // добавочного плана, не дёргая приватный шов.
    internal void RestoreUserMode(string sessionId)
        => _teamState.RestoreUserMode(sessionId);

    // Бюджет итерации из дефолтов плана с optional override из конфига TeamImplement:Max*
    // Публичный (волна Г): TeamDecisionService.StartTeamWorkAsync зовёт при открытии
    // свежей итерации в Idle.
    internal TeamImplementBudget NewTeamImplementBudget() => _teamState.NewTeamImplementBudget();

    // Рассылка TeamImplementMessage по группе чата. Тело в TeamStateService. Публичный
    // (волна В): TeamPlanService публикует состояние режима после правки PlanCardId/
    // PlanVersion/Replanning — через тот же канал, что и TeamWaveService.
    internal Task BroadcastTeamImplementAsync(string sessionId, Session session) =>
        _teamState.BroadcastTeamImplementAsync(sessionId, session);

    private Task BroadcastTeamImplementAsync(string sessionId, SessionEntry entry) =>
        _teamState.BroadcastTeamImplementAsync(sessionId, entry.Info);

    // --- Э2: планирование по компетенциям и карточка плана ---
    //
    // Тела RunTeamPlanningAsync/CreateTeamPlanAsync/PublishTeamPlanAsync/
    // SupersedeCurrentPlanCardAsync/ResolveStalePlanCardAsync переехали
    // в TeamPlanService (волна В). Обёртки сохранены, чтобы не переписывать тесты:
    // 18 мест зовут _sut.CreateTeamPlanAsync через тот же контракт.

    // Построить план по вводной и опубликовать карточкой в ленту штаба.
    // Тело — в TeamPlanService. Обёртка сохранена ради публичной сигнатуры.
    public Task<(TeamImplementPlan? Plan, string? Reason)> CreateTeamPlanAsync(
        string sessionId, string request, string? userId = null, CancellationToken ct = default,
        bool fromHuman = true, string? feedback = null) =>
        _teamPlan.CreateTeamPlanAsync(sessionId, request, userId, ct, fromHuman, feedback);

    // Ответ человека по карточке плана (SessionHub.RespondTeamPlan).
    // Тело переехало в TeamDecisionService (волна Г): решение по карточке — собственное
    // дело вертикали (включая развилку Accumulator/диск, которую спрятал
    // публичный ApplyPlanDecisionAsync в ядре). Обёртка сохранена ради публичной
    // сигнатуры — SessionHub зовёт её по этому контракту.
    public Task<TeamImplementPlan?> RespondTeamPlanAsync(string sessionId, string planId,
        TeamPlanDecision decision, string? subtaskId = null, string? executorPersonaId = null,
        string? userId = null, string? feedback = null) =>
        _teamDecision.RespondTeamPlanAsync(sessionId, planId, decision, subtaskId,
            executorPersonaId, userId, feedback);

    // Гасим устаревшую карточку и объясняем человеку, почему решение по ней не сработало.
    // Тело — в TeamPlanService (волна В). Обёртка сохранена ради сигнатуры и единого
    // канала: RespondTeamPlanAsync вызывает её в той же ветке, что и раньше.
    // Публичный (волна Г): TeamDecisionService.RespondTeamPlanAsync вызывает её
    // по тому же контракту.
    internal Task ResolveStalePlanCardAsync(string sessionId, Session session,
        SessionTeamImplement team, string planId, TeamImplementPlan plan) =>
        _teamPlan.ResolveStalePlanCardAsync(sessionId, session, team, planId, plan);

    // Погасить ТЕКУЩУЮ карточку плана как заменённую версией nextVersion. Тело — в
    // TeamPlanService (волна В). Обёртка сохранена ради сигнатуры: из PublishTeamPlanAsync
    // и EnterInterviewAsync вызывается по тому же контракту.
    // Публичный (волна Г): TeamDecisionService.StartTeamWorkAsync/RespondTeamPlanAsync
    // (ветка Edit) вызывают её по тому же контракту.
    internal Task SupersedeCurrentPlanCardAsync(string sessionId, Session session, int nextVersion) =>
        _teamPlan.SupersedeCurrentPlanCardAsync(sessionId, session, nextVersion);

    // Обработчики волны переехали в вертикаль (шаг 2г-4, волна 1): их держит
    // TeamCoordinator, а ядро отдаёт его одной ссылкой вместо четырёх публичных
    // Func-свойств. Ставит обработчики TeamWaveService, читают четыре сервиса вертикали и
    // четыре ветки ядра ниже. Экземпляр координатора один — тот, что создан в конструкторе,
    // поэтому в тестах связь работает ровно как раньше (сборка объектов не менялась).
    // Прежние комментарии обещали здесь «разрыв цикла TaskExecutionService → SessionManager»;
    // фактически цикла не было — вертикаль клала обработчик в ядро и сама же его оттуда
    // забирала. Разбор — docs/research/team-di-migration-2026-09.md §3.
    // null-семантика прежняя: обработчик не назначен (тесты ядра без штаба) — ветка молчит.
    internal TeamCoordinator TeamHandlers => _teamCoordinator;

    // --- Э4: автономный цикл, бюджет и эскалации ---

    // Сессии с включённым режимом — сторожу зависших волн (TeamWaveWatchdog) и сводкам.
    public IReadOnlyList<Session> GetTeamImplementSessions() =>
        [.. _sessions.Values.Select(e => e.Info).Where(s => s.TeamImplement is not null)];

    // Нерешённая карточка плана по id — путь для чата без аккумулятора (сервер перезапустился,
    // ход ещё не начинался). Фильтр по Resolved — тот же, что у Accumulator.FindTeamPlan у
    // активного чата: повторный клик по уже разрешённой карточке не должен пройти
    // (идемпотентность решения по карточке). Выделено из прежнего LoadPendingStoredPlanAsync
    // волной Г, чтобы вертикаль TeamDecisionService получила одну точку вместо размазанной
    // логики «Accumulator.FindTeamPlan + LoadPendingStoredPlanAsync».
    public async Task<TeamImplementPlan?> FindActivePlanAsync(string sessionId, string planId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (entry.Accumulator is { } acc) return acc.FindTeamPlan(planId);
        if (entry.Info.ClaudeSessionId is not string key) return null;
        return await ReadStoredTeamPlanAsync(key, planId, onlyUnresolved: true);
    }

    // Применить решение человека по карточке плана (Run/Reassign/Cancel/Edit-отмена).
    // Развилка «активный аккумулятор против диска» была размазана по двум веткам
    // RespondTeamPlanAsync в SessionManager; волна Г собирает её в один публичный метод
    // ядра. Активный чат → Accumulator.OnTeamPlanUpdated + FireAndForget SaveSnapshotAsync;
    // неактивный → LoadAsync + правка под _falPersistLock + SaveAsync. true — карточка
    // обновлена; false — карточки нет либо чата/транскрипта нет (причина уходит в лог).
    public async Task<bool> ApplyPlanDecisionAsync(string sessionId, string planId,
        TeamImplementPlan plan, bool resolved)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        if (entry.Accumulator is { } acc)
        {
            acc.OnTeamPlanUpdated(planId, plan, resolved ? plan.Approved : null);
            FireAndForget(acc.SaveSnapshotAsync(_history),
                $"сохранение истории после решения по плану команды ({sessionId})");
            return true;
        }
        if (entry.Info.ClaudeSessionId is not string key) return false;
        try
        {
            return await WithFalPersistLockAsync(async () =>
            {
                var stored = await _history.LoadAsync(key);
                var card = stored.OfType<StoredTeamPlanMessage>()
                    .LastOrDefault(m => m.PlanId == planId && !m.Resolved);
                if (card is null) return false;
                card.Plan = plan;
                card.Resolved = resolved;
                if (resolved) card.Approved = plan.Approved;
                await _history.SaveAsync(key, stored);
                return true;
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Правка карточки в истории на диске ({SessionId}) не удалась", sessionId);
            return false;
        }
    }

    // Правка карточки истории у НЕактивного чата (аккумулятора нет): загрузить, изменить,
    // сохранить. У активного чата тем же занимается аккумулятор — там снимок хода в памяти
    // и правка под его локом. Здесь read-modify-write файла, поэтому идём под _falPersistLock —
    // тем же, что сериализует остальные внеходовые записи истории. Без него двойной клик по
    // карточке (обычное дело сразу после рестарта, когда аккумулятор ещё не оживлён) проходил
    // бы дважды: удвоенная прибавка бюджета и двойная раздача волны.
    private async Task<bool> MutateStoredAsync<T>(SessionEntry entry, string sessionId,
        Func<T, bool> match, Action<T> mutate) where T : StoredMessage
    {
        if (entry.Info.ClaudeSessionId is not string key) return false;
        try
        {
            return await WithFalPersistLockAsync(async () =>
            {
                var stored = await _history.LoadAsync(key);
                var card = stored.OfType<T>().LastOrDefault(match);
                if (card is null) return false;
                mutate(card);
                await _history.SaveAsync(key, stored);
                return true;
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Правка карточки в истории на диске ({SessionId}) не удалась", sessionId);
            return false;
        }
    }

    // Транзакция над состоянием режима: ЕДИНСТВЕННЫЙ способ править счётчики бюджета и
    // попытки под-задач. Точки записи разнесены по потокам (раздача волны из колбэка
    // завершения задачи, перевыдача из колбэка провала хода, квота из HTTP-фильтра), а
    // `int++` не атомарен — частичный лок означал бы потерянные инкременты и нечестный счёт
    // ровно там, ради чего Э4 и делался. Внутри — только синхронная работа с моделью.
    internal T? WithTeamState<T>(string sessionId, Func<SessionTeamImplement, T> mutate) =>
        _teamState.WithTeamState(sessionId, mutate);

    // Публичный (волна Д): TeamDecisionService зовёт его вместо прямой работы с
    // entry.Accumulator. Активный чат — живые объекты аккумулятора; неактивный (после
    // рестарта) — копии с диска. У активного чата возвращённые объекты — те же, что в
    // истории: их правки фиксируются снимком.
    public async Task<IReadOnlyList<TeamEscalation>> ListOpenEscalationsAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return [];
        if (entry.Accumulator is { } acc)
            return acc.GetAll().OfType<StoredTeamEscalationMessage>()
                .Where(m => !m.Escalation.Resolved)
                .Select(m => m.Escalation).ToList();
        if (entry.Info.ClaudeSessionId is not string key) return [];
        try
        {
            var stored = await _history.LoadAsync(key);
            return stored.OfType<StoredTeamEscalationMessage>()
                .Where(m => !m.Escalation.Resolved)
                .Select(m => m.Escalation).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Чтение карточек остановки с диска ({SessionId}) не удалось", sessionId);
            return [];
        }
    }

    // Синхронный собрат ListOpenEscalationsAsync (S6, фикс-волна). Нужен там, где
    // async на запросном потоке недопустим (MVC-фильтр DenyOnDelegatedTurn → TeamBudgetService)
    // или где мы хотим объединить чтение с мутацией в одной WithTeamState-транзакции
    // (S4 — гонка возврата стадии). Реализация безопасна для синхронного вызова: у
    // активного чата читает Accumulator напрямую (исторически тот же путь без явного
    // lock), у неактивного — LoadAsync(...).GetAwaiter().GetResult() (метод фактически
    // синхронен: Task.FromResult, см. ChatHistoryService.LoadAsync).
    IReadOnlyList<TeamEscalation> ITeamHistoryStore.GetOpenTeamEscalationsSync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return [];
        if (entry.Accumulator is { } acc)
            return acc.GetAll().OfType<StoredTeamEscalationMessage>()
                .Where(m => !m.Escalation.Resolved)
                .Select(m => m.Escalation).ToList();
        if (entry.Info.ClaudeSessionId is not string key) return [];
        try
        {
            var stored = _history.LoadAsync(key).GetAwaiter().GetResult();
            return stored.OfType<StoredTeamEscalationMessage>()
                .Where(m => !m.Escalation.Resolved)
                .Select(m => m.Escalation).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Чтение карточек остановки с диска ({SessionId}) не удалось", sessionId);
            return [];
        }
    }

    // Публичный (волна Д): TeamDecisionService зовёт его вместо прямой работы с
    // entry.Accumulator. Счётчик и момент последнего оклика пишутся на карточку в истории —
    // переживают рестарт сервера, чтобы после перезапуска не начать оклик заново.
    // false — карточка уже закрыта либо её нет.
    public async Task<bool> MarkEscalationRemindedAsync(string sessionId, string escalationId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        if (entry.Accumulator is { } acc)
        {
            if (!acc.OnTeamEscalationReminded(escalationId)) return false;
            try { await acc.SaveSnapshotAsync(_history); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Сохранение истории после напоминания ({SessionId}) не удалось", sessionId);
            }
            return true;
        }
        // Чат неактивен (после рестарта аккумулятор ещё не оживлён) — правим историю на диске
        return await _teamHistory.MutateCardAsync<StoredTeamEscalationMessage>(sessionId,
            m => m.EscalationId == escalationId && !m.Escalation.Resolved,
            m =>
            {
                m.Escalation.RemindersSent++;
                m.Escalation.LastReminderAt = DateTime.UtcNow;
            });
    }

    // Публичный (волна Д): TeamDecisionService.RespondTeamEscalationAsync зовёт его
    // вместо прямой работы с entry.Accumulator. Помечает карточку resolved на активном
    // чате (и пишет снимок), либо правит на диске у неактивного. Возвращает объект
    // карточки — координатору нужны её поля (Kind/Actions/TaskId/Wave/PersonaId).
    // null — карточки нет / не резолвилась.
    // resolutionNote (M3, фикс-волна): подпись снятия штабом пишется в карточку ОДНИМ
    // вызовом вместе с Resolved/ChosenActionId — раньше правка шла отдельным
    // MutateCardAsync, а у активного чата MutateCardAsync ходил через диск и затирался
    // ближайшим снимком Accumulator-а; теперь текст снятия живёт до перезагрузки.
    public async Task<TeamEscalation?> ResolveEscalationAsync(string sessionId, string escalationId,
        string? actionId, string? resolutionNote = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (entry.Accumulator is { } acc)
        {
            var escalation = acc.FindTeamEscalation(escalationId);
            var resolved = acc.OnTeamEscalationResolved(escalationId, actionId);
            if (!resolved) return null;
            if (escalation is null) return null;
            // Подпись снятия вписывается в тот же объект до снимка: снимок зальёт её на диск
            // одним коммитом, отдельного MutateCardAsync с гонкой уже нет
            if (resolutionNote is not null) escalation.ResolutionNote = resolutionNote;
            FireAndForget(acc.SaveSnapshotAsync(_history),
                $"сохранение истории после решения по карточке остановки ({sessionId})");
            return escalation;
        }
        // Чат неактивен — карточка лежит только на диске
        StoredTeamEscalationMessage? card = null;
        var ok = await _teamHistory.MutateCardAsync<StoredTeamEscalationMessage>(sessionId,
            m => m.EscalationId == escalationId && !m.Escalation.Resolved,
            m =>
            {
                m.Escalation.Resolved = true;
                m.Escalation.ChosenActionId = actionId;
                if (resolutionNote is not null) m.Escalation.ResolutionNote = resolutionNote;
                card = m;
            });
        if (!ok) return null;
        return card?.Escalation;
    }

    // Вердикт квоты запуска исполнителя со штабного хода-реакции (Э4). Enum в Models
    // (TeamRunQuota) живёт под тем же именем, что и nested SessionManager.TeamRunQuota:
    // тесты и фильтр DenyOnDelegatedTurn пользуются nested (контракт публичной сигнатуры
    // ядра), TeamBudgetService — Models (вертикаль не должна ссылаться на ядро через
    // nested-тип). Каст через (int) безопасен: оба enum'а имеют одинаковые значения.
    public enum TeamRunQuota { NotTeamMode, Allowed, Exhausted }

    // Гейт лавины запусков: на реакционном ходу координатора (ответ на доклад исполнителя)
    // запуск задачи разрешён, ПОКА цел бюджет итерации — запрет заменён квотой, а не снят.
    // Тело переехало в TeamBudgetService (волна Е). Обёртка сохранена ради публичной
    // сигнатуры: DenyOnDelegatedTurn.Decide и фильтр OnActionExecuted зовут по этому контракту.
    public (TeamRunQuota Verdict, string? Reason) TryConsumeTeamImplementRun(string sessionId, string ownerId)
    {
        var (v, r) = _teamBudget.TryConsumeTeamImplementRun(sessionId, ownerId);
        return ((TeamRunQuota)(int)v, r);
    }

    // Компенсация квоты запуска (m3, второй проход Глеба). Тело в TeamBudgetService.
    // Обёртка сохранена ради сигнатуры DenyOnDelegatedTurn.OnActionExecuted.
    public void RefundTeamImplementRun(string sessionId, string ownerId) =>
        _teamBudget.RefundTeamImplementRun(sessionId, ownerId);

    // Квота пробуждения штаба агентом (Э4). Тело переехало в TeamBudgetService (волна Е):
    // квота — собственное дело вертикали. Обёртки сохранены ради публичных сигнатур:
    // ReportBlockerAsync (метод уезжает в волну Ж) и SessionMessagingService.SendAsync
    // ходят по этому контракту.
    // TeamMode=false — чат не штаб: ограничение не наше дело, пропускаем как раньше.
    public (bool TeamMode, bool Allowed, string? Reason) TryConsumeTeamWakeup(string sessionId) =>
        _teamBudget.TryConsumeTeamWakeup(sessionId);

    // Компенсация квоты пробуждения (m3, второй проход Глеба). Тело в TeamBudgetService.
    public void RefundTeamWakeup(string sessionId) =>
        _teamBudget.RefundTeamWakeup(sessionId);

    // Решение человека по карточке остановки (SessionHub.RespondTeamEscalation).
    // Тело переехало в TeamDecisionService (волна Д): addBudget / runNext / resume / retryPlan
    // и остальные ветки — собственное дело вертикали (включая развилку Accumulator/диск,
    // которую спрятал публичный ResolveEscalationAsync в ядре). Обёртка сохранена ради
    // публичной сигнатуры — SessionHub зовёт её по этому контракту.
    public Task<bool> RespondTeamEscalationAsync(string sessionId, string escalationId,
        string? actionId, string? comment = null, string? userId = null) =>
        _teamDecision.RespondTeamEscalationAsync(sessionId, escalationId, actionId, comment, userId);

    // «Остановить» (кнопка человека): текущие исполнители дорабатывают, новые волны не
    // стартуют. Единая точка остановки для кнопки режима (ChatsController) и кнопки
    // «Остановить» информационной карточки волны (RespondTeamEscalationAsync): состояние И
    // карточка возврата живут здесь — без карточки продолжать остановленную практику
    // было бы нечем (сбой 28.08.2026). Тело переехало в TeamDecisionService (волна Д):
    // вертикаль публикует карточку возврата и поднимает состояние. Обёртка сохранена ради
    // публичной сигнатуры — ChatsController и RespondTeamEscalationAsync зовут её
    // по этому контракту.
    public Task<Session?> StopTeamImplementAsync(string sessionId, string? userId = null) =>
        _teamDecision.StopTeamImplementAsync(sessionId, userId);

    // Признак «у чата sessionId есть живая делегированная задача, по которой ждём доклада
    // исполнителя». Вешает сторона задач при регистрации (TaskManager.GetById и проверка
    // полей SourceSessionId/Status/CompletionDelivered/ClaudeStartedAt/ExecutorStoppedAt).
    // Здесь Func остаётся осознанно, в отличие от четырёх штабных, и по ДВУМ причинам, ни
    // одна из которых не про «SessionManager не знает TaskManager» (тот про SessionManager и
    // правда не знает, циклом это не делает). Первая: ставит хук TaskExecutionService
    // (TaskExecutionService.cs:136) — сторона ЧУЖАЯ, а не сама вертикаль, и вот у неё
    // зависимость на SessionManager есть, то есть цикл настоящий. Вторая: TaskManager живёт
    // в вертикали Services.Tasks, и прямая ссылка на него из спины уронила бы сторож границ.
    // null — признак не задан (тесты, либо стора задач нет): ждать нечего.
    public Func<string, bool>? HasLiveDelegatedTasks { get; set; }

    // Резолв названия задачи по id (волна 1 team-blocker-honest, дефект 1430b732): штаб
    // публикует карточку остановки с TaskId, и подпись диалога снятия должна показать
    // название задачи, а не заголовок карточки. Тот же узкий шов, что у HasLiveDelegatedTasks:
    // SessionManager не знает TaskManager (цикл), а зовущая сторона (TeamDecisionService)
    // зависимость на ядро имеет — поэтому Func, а не прямая ссылка. null — задача
    // не найдена (удалена) или стора задач нет (тесты): подставляется null, фронт падает
    // обратно на заголовок карточки.
    public Func<string, string?>? GetTaskTitle { get; set; }

    // Тонкие обёртки на Core-хелпер TeamProtocolMarkers. Реализации уехали в спину
    // (`ClaudeHomeServer.Core.Services.TeamProtocolMarkers`): их зовёт и ядро SessionManager,
    // и штаб TeamWaveService, и живая трансляция любого чата в OnMessageAsync, и
    // TaskExecutionService. Обёртки оставлены ровно для обратной совместимости тестов
    // SessionManagerTests/TaskExecutionServiceTests — тесты обращаются к этим методам
    // напрямую (`SessionManager.ParseEscalationMarker(text)`), и под-шаг 2а явно требует
    // «зелёные без правок тестов». Семантика и поведение не меняются.
    internal static (TeamEscalationKind Kind, string Text)? ParseEscalationMarker(string text)
        => TeamProtocolMarkers.ParseEscalationMarker(text);

    internal static string? ParseWorkMarker(string text)
        => TeamProtocolMarkers.ParseWorkMarker(text);

    internal static bool HasTalkMarker(string text)
        => TeamProtocolMarkers.HasTalkMarker(text);

    internal const string NoReplyMarker = TeamProtocolMarkers.NoReplyMarker;

    internal static bool HasNoReplyMarker(string text)
        => TeamProtocolMarkers.HasNoReplyMarker(text);

    internal static string StripTeamProtocolMarkers(string text)
        => TeamProtocolMarkers.StripTeamProtocolMarkers(text);

    internal static bool IsAmbiguousMarkerTail(string tail)
        => TeamProtocolMarkers.IsAmbiguousMarkerTail(tail);

    internal static string TrimAmbiguousMarkerTail(string text)
        => TeamProtocolMarkers.TrimAmbiguousMarkerTail(text);

    internal static string TrimUnresolvedMarkerOpen(string strippedText)
        => TeamProtocolMarkers.TrimUnresolvedMarkerOpen(strippedText);

    // Отсечки сторожа волн, погашенные вопросом ASK (OnAskQuestionStabAsync), возвращаются
    // по завершении хода — ответ получен, либо ход прерван (прерывание без result приходит
    // сюда же, на исходах interrupted | crashed, см. HandleTeamTurnCompletedShim). Без
    // возврата волна осталась бы без надзора: настоящий stall никто бы не поймал, а
    // «молчаливых пауз не бывает». Волна должна быть живой: закрытая
    // (ClosedWave == WaveNumber) или нулевая — не в счёт.
    // Owning-обёртка для вертикали TeamTurnCompletionService: вертикаль зовёт с одним
    // sessionId и не видит SessionEntry (40-польная персистентная модель с пятью
    // примитивами синхронизации — общая память двух подсистем не нужна).
    internal void RestoreWaveWatchdogIfPaused(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        RestoreWaveWatchdogIfPaused(sessionId, entry);
    }

    private void RestoreWaveWatchdogIfPaused(string sessionId, SessionEntry entry)
    {
        if (entry.Info.TeamImplement is not { } team) return;
        if (team.Stage != TeamImplementStage.Wave || team.WaveStartedAt is not null) return;
        if (team.WaveNumber == 0 || team.ClosedWave >= team.WaveNumber) return;

        WithTeamState(sessionId, t =>
        {
            t.WaveStartedAt = DateTime.UtcNow;
            t.WaveActivityAt = DateTime.UtcNow;
            return true;
        });
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
    }

    // Конец хода штаба (Э4 + Э5): тело переехало в TeamTurnCompletionService (волна Ж) —
    // разбор маркеров координатора, гард молчаливого тупика, обработка проверки — всё это
    // собственное дело штабного цикла, и вертикаль владеет единым тестом, чтобы не плодить
    // отдельные развилки «Accumulator vs диск» и обратные рёбра в ядро. Обёртка сохранена
    // ради публичной сигнатуры: HandleTeamTurnCompletedShim и тесты SessionManagerTests
    // зовут HandleTeamTurnEndAsync напрямую.
    public Task HandleTeamTurnEndAsync(string sessionId, string turnText, bool failed, bool asked = false)
        => _teamTurnCompletion.HandleTeamTurnEndAsync(sessionId, turnText, failed, asked);

    // Волна 3 задачи b63fd8ea: хук на завершение фонового async-агента (BgAgentDoneMessage).
    // Внутри вертикали читается session, стадия, планирование — то, что вертикаль уже умеет.
    // asked и hasAsync пробрасываем из ядра, потому что оба значения нужны В ОДНОМ решении
    // ядра (case BgAgentDoneMessage: сброс метки AsyncAgentStallSince под TeamTurnLock на
    // проверке !hasAsync, и тут же публикация карточки по тому же hasAsync). Если бы
    // hasAsync читался через ITeamRunState уже в вертикали (вызовом HasAsyncAgent или иным
    // способом поверх AsyncAgentInFlight), между её вызовом и вызовом этой обёртки пришёл
    // бы другой BgAgentDoneMessage, переписал state и ядро приняло решение по устаревшему
    // снимку. Единый параметр из ядра — общий снимок на оба решения (метка + карточка).
    // В самой вертикали (TeamTurnCompletionService.HandleTeamTurnEndAsync, путь гарда
    // молчаливого тупика) проверка живого async-агента идёт через
    // _run.ShouldSuppressAsyncAgentStallGuard — он под капотом берёт AsyncAgentInFlight(entry)
    // МИМО шва ITeamRunState.HasAsyncAgent (последний на момент ревью оказался невостребован:
    // Глеб проверил удалением объявления и реализации, `dotnet build` прошёл с 0 ошибок).
    // Решения по hasAsync (Major + Minor 2) описаны в шапке HandleBgAgentDoneAsync.
    public Task HandleBgAgentDoneAsync(string sessionId, bool aborted, bool hasAsync, bool asked)
        => _teamTurnCompletion.HandleBgAgentDoneAsync(sessionId, aborted, hasAsync, asked);

    // P23: авто-гашение карточки блокера, когда координатор сам снял её предмет. Тело остаётся
    // в ядре (работает с приватным состоянием entry.Accumulator и приватным _history —
    // 40-польная персистентная модель, пять примитивов синхронизации), вертикаль получает
    // только сессию/текст. Публичный owning-обёртка (волна Ж) для вертикали
    // TeamTurnCompletionService.HandleTeamTurnEndAsync, которая зовёт после разбора
    // маркера эскалации (P23).
    public Task<bool> TryAutoResolveTeamBlockerAsync(string sessionId, string turnText)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return Task.FromResult(false);
        return TryAutoResolveTeamBlockerInternalAsync(sessionId, entry, turnText);
    }

    // Owning-обёртка для вертикали и тулсетов: единая точка снятия блокера по факту
    // (волна 1 team-blocker-honest). Тело в TeamDecisionService — там же, где и сам
    // механизм гашения. Сигналы (tasks_update/tasks_complete/tasks_run_executor/chats_send/
    // SubtaskDropHandler/<team:resolved>) идут через этот метод.
    public Task<bool> TryResolveBlockerByFactAsync(string sessionId, string taskId, string reason) =>
        _teamDecision.TryResolveBlockerByFactAsync(sessionId, taskId, reason);

    // Тело в ядре — здесь лезем в Accumulator/History, а вертикаль видит только результат.
    private async Task<bool> TryAutoResolveTeamBlockerInternalAsync(string sessionId, SessionEntry entry, string turnText)
    {
        if (entry.Info.TeamImplement is not { } team) return false;
        if (team.Stage != TeamImplementStage.AwaitingDecision) return false;

        // Последняя открытая карточка блокера в истории чата (вехи остановки живут дольше хода).
        var openBlocker = entry.Accumulator?.GetAll()
            .OfType<StoredTeamEscalationMessage>()
            .Where(m => !m.Escalation.Resolved && m.Escalation.Kind == TeamEscalationKind.Blocker)
            .LastOrDefault();
        if (openBlocker is null) return false;

        // Координатор снял блокер действием, а не молчанием: либо продолжает работу маркером
        // team:work, или итерация финиширована (все плановые волны закрыты). Иначе карточка
        // уместна — координатор реально ждёт решения человека, оставляем как есть.
        var hasWork = TeamProtocolMarkers.ParseWorkMarker(turnText) is not null;
        var allWavesClosed = AllPlannedWavesClosed(team);
        if (!hasWork && !allWavesClosed) return false;

        // Сведение к общей точке (волна 1 team-blocker-honest): taskId берём из самой карточки
        // (к этому моменту карточка в аккумуляторе заведомо есть — иначе мы бы вышли на :6410).
        // reason — какой именно путь снятия сработал, чтобы подпись «Снят штабом: …» была честной.
        // Возвращаем РЕАЛЬНЫЙ результат гашения: вызывающий в TeamTurnCompletionService при
        // false пропускает перечитывание team-состояния, а при true — перечитывает. Раньше
        // тут стоял безусловный `return Task.FromResult(true)`, который заставлял перечитывать
        // состояние зря, когда гасить было нечего (старый код успевал вернуть true до того,
        // как _teamDecision успевал сказать false).
        var reason = hasWork ? "координатор снял блокер маркером работы"
            : "координатор подвёл итог — все волны плана закрыты";
        return await _teamDecision.TryResolveBlockerByFactAsync(sessionId, openBlocker.Escalation.TaskId ?? "", reason);
    }

    // Новая вводная разложена планировщиком и уходит в волну (Э5). Тело переехало в
    // TeamDecisionService (волна Г): подготовка состояния перед планированием —
    // собственное дело вертикали (гард по стадии, переключение Interview→Planning,
    // открыление свежей итерации в Idle, погашение устаревшей карточки на Confirming).
    // Owning-обёртка для вертикали TeamTurnCompletionService (волна Ж): HandleTeamTurnEndAsync
    // вызывает из разбора маркера team:work по тому же контракту — internal, чтобы
    // вертикаль звала, а снаружи API ядра не открывало.
    internal Task StartTeamWorkAsync(string sessionId, string request, string? feedback = null) =>
        _teamDecision.StartTeamWorkAsync(sessionId, request, feedback);

    // Собственно планирование: вводная (и правка к плану) сохраняются для повтора, зовётся
    // планировщик, а при отказе публикуется карточка с причиной и кнопкой повтора. Тело —
    // в TeamPlanService (волна В). Обёртка сохранена ради сигнатуры: StartTeamWorkAsync,
    // retryPlan и RespondTeamPlanAsync (ветка Edit) зовут её по тому же контракту.
    // Публичный (волна Г): TeamDecisionService.StartTeamWorkAsync/RespondTeamPlanAsync
    // (ветка Edit) зовут её по тому же контракту.
    internal Task RunTeamPlanningAsync(string sessionId, string request, string? feedback,
        bool fromHuman) =>
        _teamPlan.RunTeamPlanningAsync(sessionId, request, feedback, fromHuman);

    // Выход из интервью без работы (M6, маркер `<team:talk/>`): координатор честно разобрал
    // сообщение — это разговор, практику на пустом месте не разворачиваем. Тело переехало
    // в TeamDecisionService (волна Г): выход из интервью и третья дверь в мёртвую зону
    // конвейера — собственное дело вертикали. Owning-обёртка для вертикали
    // TeamTurnCompletionService (волна Ж): HandleTeamTurnEndAsync вызывает из разбора
    // маркера team:talk по тому же контракту — internal, чтобы вертикаль звала.
    internal Task CloseTeamTalkAsync(string sessionId) =>
        _teamDecision.CloseTeamTalkAsync(sessionId);

    // Новая вводная человека (Э5): итерация начинается заново — бюджет обнуляется, счёт волн
    // и остановка сбрасываются. Сбросить может ТОЛЬКО человек: путь сюда один — сообщение
    // через хаб, у агента его нет (chats_send, доклады и авто-ходы идут другими методами).
    // M6: на приёме открываем лишь СВЕЖУЮ итерацию (режим включён, плана ещё не было) — она
    // по определению вводная и по Э8 всегда проходит интервью. В ожидании (Idle) сообщение
    // может оказаться разговором: сброс там делает классификация координатора (маркер работы
    // в StartTeamWorkAsync), а не приём — иначе вопрос «что вы сделали?» обнулял потолки,
    // навязывал план-режим и ловил ложный stall-гард (подтверждено живьём, аудит 2026-08-01).
    // Перепланирование (план уже есть) и ответы на вопросы интервью сюда тоже не попадают.
    private void ResetTeamIterationOnUserInput(string sessionId, SessionEntry entry)
    {
        if (entry.Info.TeamImplement is not { } team) return;
        // Stage по-прежнему исключает волну/ожидание/проверку (как раньше — тест «вводная
        // посреди волны бюджет не сбрасывает» ставит их напрямую, без PlanCardId). Но одного
        // Stage мало: с волны 3 дефолтная стадия свежего режима — тоже Interview (спека Э8),
        // и сам по себе Stage больше не отличает «ни одного сообщения ещё не было» от «уже
        // второй раунд интервью» — оба со Stage=Interview, PlanCardId=null. Различает
        // FirstIterationOpened — взводится только этим методом, один раз за итерацию.
        if (team.FirstIterationOpened || team.PlanCardId is not null
            || team.Stage is not (TeamImplementStage.Planning or TeamImplementStage.Interview)) return;

        WithTeamState(sessionId, t =>
        {
            t.Budget = NewTeamImplementBudget();
            t.WaveNumber = 0;
            t.ClosedWave = 0;
            t.PlannedWaves = 0;
            t.WaveStartedAt = null;
            t.WaveActivityAt = null;
            // «Остановить» относилось к прошлой итерации — новая вводная человека её снимает
            t.Stopped = false;
            // Э8: вход в итерацию — это интервью. Первая вводная проходит его ВСЕГДА: даже
            // кристальная постановка даёт «вопросов нет» и допущения в карточке плана.
            // Счётчик раундов и признак перепланирования — с нуля: они живут на вводную.
            t.Stage = TeamImplementStage.Interview;
            t.InterviewRounds = 0;
            t.Replanning = false;
            t.FirstIterationOpened = true;
            // Э8-фикс (прод 2026-08-03): счёт вводных — под файловые пути плана, растёт
            // на каждой новой (см. IterationNumber).
            t.IterationNumber++;
            return true;
        });
        // План-режим ставим ПОСЛЕ смены стадии: ход по этой самой вводной уже уйдёт в CLI
        // с --permission-mode plan, а не со следующего сообщения (ResetTeamIterationOnUserInput
        // зовётся на приёме сообщения, до очереди и до запуска процесса).
        EnterPlanPhaseMode(sessionId);
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        FireAndForget(BroadcastTeamImplementAsync(sessionId, entry),
            $"рассылка состояния режима после новой вводной ({sessionId})");
    }

    // Ответ человека из стадии «ждёт решения» обычным сообщением (M3): практика возвращается
    // в работу так же, как по кнопке карточки (RespondTeamEscalationAsync, ветка по умолчанию) —
    // до первой волны в стадию, из которой ушла в ожидание, иначе в волну со свежими отсечками
    // сторожа. Бюджет и счёт волн НЕ трогаем: итерация та же самая, обнуляет их только новая
    // вводная (ResetTeamIterationOnUserInput) — иначе достаточно было бы отвечать на карточки,
    // чтобы бесконечно продлевать потолок идущей практике.
    // P23: если все плановые волны уже закрыты — возвращать в Wave некуда (вечная «волна N из
    // N»), идём в Idle: итерация завершена, режим ждёт новой вводной.
    // Волна 1 team-blocker-honest: открытые решающие карточки гасятся тем же путём, что кнопка
    // человека — иначе полоса «Практика ждёт вашего решения» продолжает висеть (прод 2026-09).
    // Проходим по всем открытым карточкам с этим TaskId (включая блокеры) и гасим через
    // ResolveEscalationAsync; у блокеров — резолюшн-нота «Ответ сообщением».
    // Карточки, чьё решение по сути и есть текст (S5, фикс-волна). Их гасим текстовым
    // ответом из «ждёт решения». Прочие решающие карточки (BudgetExhausted, WaveGate,
    // Stopped) оставляем человеку: их кнопка делает серверное действие (поднять потолки,
    // раздать волну, снять «Стоп»), которое текст заменить не может. У BudgetExhausted
    // кнопка «Добавить бюджет» — единственный способ поднять потолки, и при погашенной
    // карточке человек остался бы без неё (прод 2026-09 — зафиксировано Глебом).
    private static bool ExtinguishedByMessage(TeamEscalationKind kind) =>
        kind is TeamEscalationKind.Blocker
                or TeamEscalationKind.TaskFailed
                or TeamEscalationKind.PlanDeviation
                or TeamEscalationKind.CheckFailed
                or TeamEscalationKind.ProductDecision;

    private async Task ResumeTeamFromDecisionOnUserInput(string sessionId, SessionEntry entry)
    {
        if (entry.Info.TeamImplement is not { Stage: TeamImplementStage.AwaitingDecision }) return;

        // S5 (фикс-волна): гасим сообщением ТОЛЬКО карточки, чьё решение и есть текст.
        // Не-гасимые (BudgetExhausted, WaveGate, Stopped) оставляем открытыми — иначе
        // человек теряет серверные кнопки.
        var openCards = await ListOpenEscalationsAsync(sessionId);
        foreach (var card in openCards.Where(c => !c.Kind.IsInformational()
                && ExtinguishedByMessage(c.Kind)))
        {
            // M3 (фикс-волна): ResolutionNote пишется в карточку ОДНИМ вызовом вместе с
            // Resolved/ChosenActionId — раньше отдельный MutateCardAsync у активного чата
            // ходил через диск и затирался ближайшим снимком аккумулятора; теперь подпись
            // «Ответ сообщением» переживает перечитывание истории
            var resolved = await ResolveEscalationAsync(sessionId, card.Id, "message", "Ответ сообщением");
            if (resolved is null) continue;
            await BroadcastAsync(sessionId, new TeamEscalationMessage(card.Id,
                card.Kind.ToWireToken(), card.Title, card.Details, card.Actions,
                card.TaskId, card.Wave, Resolved: true, ChosenActionId: "message",
                card.PersonaId, ResolutionNote: "Ответ сообщением",
                TaskTitle: card.TaskTitle));
        }

        WithTeamState(sessionId, t =>
        {
            t.Stage = t.WaveNumber == 0
                ? t.StageBeforeDecision ?? TeamImplementStage.Planning
                : AllPlannedWavesClosed(t)
                    ? TeamImplementStage.Idle
                    : TeamImplementStage.Wave;
            t.StageBeforeDecision = null;
            // Вернулись в волну — заводим страховку таймаута заново (как решение по карточке):
            // без отсечки сторож молчал бы, и повторное зависание осталось бы незамеченным
            if (t.Stage == TeamImplementStage.Wave && t.WaveNumber > 0 && t.ClosedWave < t.WaveNumber)
            {
                t.WaveStartedAt = DateTime.UtcNow;
                t.WaveActivityAt = DateTime.UtcNow;
            }
            return true;
        });
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastTeamImplementAsync(sessionId, entry);

        // Мёртвая зона конвейера (прод 2026-08-17): у текстового ответа нет actionId, белым
        // списком кнопок он не покрывался вовсе — после закрытой волны практика возвращалась
        // в Wave без раздачи следующей и стояла часами. Признак тот же, что у кнопок
        // (WaveStartPendingAfterDecision); саму раздачу и её гейты по-прежнему решает
        // TeamWaveService. Повод StateCatchUp (D1): текстовый ответ — не кнопка «Запустить»,
        // при снятых авто-волнах человек получает гейт-карточку, а не молчаливую раздачу.
        if (entry.Info.TeamImplement is { } teamNow
            && teamNow.PlanCardId is { } planId
            && _teamCoordinator.WaveStarter is { } starter)
        {
            var plan = await ((ITeamHistoryStore)this).GetTeamPlanAsync(sessionId, planId);
            if (plan is not null && WaveStartPendingAfterDecision(teamNow, plan))
            {
                try { await starter(entry.Info, plan, TeamWaveTrigger.StateCatchUp); }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Раздача волны после ответа человека (чат {SessionId}) не удалась", sessionId);
                }
            }
        }
    }

    // Все плановые волны итерации закрыты — работа по плану закончена. P23: это сигнал того,
    // что карточка блокера стала неактуальна финалом итерации (координатор подвёл итог, а
    // стадия зависла в AwaitingDecision), и критерий терминального состояния при возврате из
    // «ждёт решения» (RespondTeamEscalation / ResumeTeamFromDecisionOnUserInput) — иначе
    // практика формально «в волне N из N» после полностью закрытых волн. PlannedWaves == 0
    // (план не запускался) никогда не считаем закрытым набором.
    // Дубликат TeamDecisionService.AllPlannedWavesClosed: используется и ядром (в этой же
    // функции для возврата стадии), и вертикалью (волна Г). Стадия по StageBeforeDecision
    // зависит от вертикального решения о возврате в планирование, но критерий закрытия
    // волн — чистая арифметика, и тащить ради неё шов нерационально.
    private static bool AllPlannedWavesClosed(SessionTeamImplement team) =>
        team.PlannedWaves > 0 && team.ClosedWave >= team.PlannedWaves;

    // Мёртвая зона конвейера (прод 2026-08-17): карточка остановки висела ПОСЛЕ закрытия
    // волны — авто-раздача следующей уже была подавлена («практика ждёт человека»), а белый
    // список actionId ответа её не покрывал: конвейер замолкал до ручного «Остановить →
    // Продолжить». Признак «раздачу нужно позвать» после решения человека: практика вернулась
    // в Wave, все РОЗДАННЫЕ волны закрыты (ClosedWave == WaveNumber), а в плане есть
    // нерозданные под-задачи. Решение «раздать или поднять карточку» остаётся за
    // TeamWaveService (бюджет, версии плана, «Остановить») — здесь только «пора ли звать».
    // Дубликат TeamDecisionService.WaveStartPendingAfterDecision: та же логика «пора ли
    // звать» для текстового ответа (D1) и кнопок — вызывается и из ядра (здесь), и из
    // вертикали (волна Г). Дубликат намеренный: это предикат без сайд-эффектов, и тащить
    // ради него шов нерационально.
    private static bool WaveStartPendingAfterDecision(SessionTeamImplement team, TeamImplementPlan plan) =>
        team.Stage == TeamImplementStage.Wave
        && team.WaveNumber > 0
        && team.ClosedWave == team.WaveNumber
        && plan.Subtasks.Any(s => s.TaskId is null);

    // У координатора есть живой фоновый субагент (Tool Agent и т.п.) — ход, завершённый
    // текстом без маркера, не тупик: координатор ждёт собственного результата. P16: по этому
    // признаку гард молчаливого тупика молчит (аналог TeamPlanningInFlight). entry.Process —
    // адаптер текущего прогона CLI; HasPendingBg истинно, пока прогон доживает фоновых агентов.
    // Дубликат TeamDecisionService.AsyncAgentInFlight? Нет: вертикаль не ходит в entry.Process
    // (40-польная персистентная модель). Ядро пользуется HasPendingBg из Process напрямую —
    // приватное поле, и сюда шов не нужен.
    private static bool AsyncAgentInFlight(SessionEntry entry) => entry.Process?.HasPendingBg ?? false;

    // Возврат в интервью (Э8). Два входа: координатор сказал маркером `clarify`, что дальше
    // действовать не может (тупик в волне), либо просто задал человеку вопрос ASK-карточкой —
    // и то и другое означает, что требования неясны. Волны встают на паузу, чат уходит в
    // план-режим, а человек получает карточку «Нужны уточнения» с уведомлением и push:
    // молчаливых пауз в режиме не бывает. После ответов будет план vN на подтверждение.
    // withTurn — поднять координатору ход с просьбой задать вопросы: нужен только маркеру
    // (его ставят в КОНЦЕ ответа, спросить в том же ходу координатор уже не может).
    // При ASK-вопросе ход не нужен — вопросы человек уже видит.
    internal async Task EnterInterviewAsync(string sessionId, string reason, bool withTurn)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is not { } team) return;
        var wave = team.WaveNumber;
        // Уже в интервью — второй карточки и второго хода не надо, но план-режим подтвердим:
        // сюда можно попасть и после рестарта сервера, и повторным вопросом того же раунда.
        var alreadyInterview = team.Stage == TeamImplementStage.Interview;

        var hadPlan = team.PlanCardId is not null;
        var nextVersion = team.PlanVersion + 1;
        WithTeamState(sessionId, t =>
        {
            t.Stage = TeamImplementStage.Interview;
            // Перепланирование: план в итерации уже был, значит следующий — новая версия,
            // и подтверждение карточкой обязательно даже при включённых авто-волнах.
            if (t.PlanCardId is not null) t.Replanning = true;
            // Волна больше не идёт: сторож зависших волн в интервью не тикает — ожидание
            // ответа человека это не зависание.
            t.WaveStartedAt = null;
            t.WaveActivityAt = null;
            return true;
        });
        // План в итерации уже был — его карточка гаснет как заменённая: пока готовится версия
        // vN+1, по старой нельзя ни запустить волну, ни решить что-либо (кнопок у неё нет).
        if (hadPlan) await SupersedeCurrentPlanCardAsync(sessionId, entry.Info, nextVersion);
        EnterPlanPhaseMode(sessionId);
        entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastTeamImplementAsync(sessionId, entry);
        if (alreadyInterview) return;

        _log.LogInformation("Чат-штаб {SessionId} вернулся в интервью: {Reason}", sessionId, reason);

        var card = new TeamEscalation
        {
            Kind = TeamEscalationKind.NeedsClarification,
            Title = TeamImplementPrompts.EscalationTitle(TeamEscalationKind.NeedsClarification, reason),
            Details = TeamImplementPrompts.NeedsClarificationDetails(reason, wave),
            Wave = wave,
            Actions = TeamEscalationActions.For(TeamEscalationKind.NeedsClarification),
        };
        if (_teamCoordinator.EscalationRaiser is { } raise) await raise(entry.Info, card);
        else await ((ITeamHistoryStore)this).PublishTeamEscalationAsync(sessionId, card);

        if (withTurn)
            await _teamIntake.SendOrEnqueueAsync(sessionId, TeamImplementPrompts.ClarifyInterviewTurn(reason, team),
                senderPersonaId: null, silent: true, suppressTasksExecute: true,
                staffNote: TeamStaffNotes.InterviewReturn);
    }

    // === Шов «ядро → штаб» (этап 4, шаг 2б плана выноса штаба) ===
    // Явные реализации ITeamNotifier: наружу недоступны, наружу только через _teamNotifier.
    // Тела повторяют прежние приватные методы один в один — поведение не меняется. В шаге 2г
    // реализация переедет в вертикаль штаба целиком, эти методы уйдут вместе с телом.

    void ITeamNotifier.OnHumanInput(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        ResetTeamIterationOnUserInput(sessionId, entry);
    }

    async Task ITeamNotifier.OnHumanInputAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        await ResumeTeamFromDecisionOnUserInput(sessionId, entry);
    }

    Task ITeamNotifier.OnAskQuestionStabAsync(string sessionId)
        => OnAskQuestionStabAsync(sessionId);

    void ITeamNotifier.RestoreUserMode(string sessionId)
    {
        // Волна Б: реализация переехала в TeamStateService, шов развернулся в
        // правильную сторону — ядро делегирует в вертикаль, а не наоборот.
        _teamState.RestoreUserMode(sessionId);
    }

    void ITeamRunState.TrySetPermissionModeLive(string sessionId, ClaudeMode mode)
    {
        // Шов «вертикаль → ядро» для смены режима живому CLI-прогону: реализация в ядре
        // потому, что именно ядро держит `entry.Process` (живой ILlmSessionAdapter).
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Process?.TrySetPermissionModeLive(mode);
    }

    void ITeamRunState.TrySetEntryModeLiveAndStaleAdapter(string sessionId, ClaudeMode mode)
    {
        // Шов «вертикаль → ядро» (достройка ITeamRunState, волна Ж): тройная синхронизация
        // Mode/CLI/AdapterStale для SetTeamImplementAsync. Три поля лежат в SessionEntry
        // (Info.Mode, Process?, AdapterStale), и вертикаль без этого шова получала бы
        // доступ к 40-полейной персистентной модели. Гард «координатор не пишет код»
        // режет Bash/PowerShell на приёме permission, --disallowedTools — на создании
        // адаптера: без AdapterStale правка второго доехала бы только до следующего
        // пересоздания, а живой ход остался бы с прежним набором инструментов.
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.Info.Mode = mode;
        entry.Process?.TrySetPermissionModeLive(mode);
        if (entry.Process is not null) entry.AdapterStale = true;
    }

    T? ITeamRunState.WithTeamState<T>(string sessionId, Func<SessionTeamImplement, T> mutate) where T : default
    {
        // Шов «вертикаль → ядро» для транзакции над SessionTeamImplement: TeamBudgetService
        // (волна Е) правит счётчики бюджета из обёрток фильтра/контроллера, и единственный
        // путь — через явную реализацию ITeamRunState. Сам лок-словарь живёт в TeamStateService
        // (единственная транзакция), наружу выходит только операция целиком.
        return _teamState.WithTeamState(sessionId, mutate);
    }

    // Шов «вертикаль → ядро» для плана вызова штабного разбора хода (волна Ж). Подписчик
    // turn/completed переехал в TeamTurnCompletionService и не должен видеть SessionEntry
    // (40-польная персистентная модель с пятью примитивами синхронизации — общая память двух
    // подсистем). Реализация делегирует в entry — это всё ещё в ядре, потому что OnMessageAsync
    // (ядро) пишет план по тому же ключу и без этого шва держал бы LastTeamTurnEnds приватным
    // полем класса. Без явной реализации вертикаль не нашла бы точку для записи.
    void ITeamRunState.RecordTeamTurnEnd(string sessionId, int turnSeq,
        string? text, bool failed, bool asked)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        entry.RecordTeamTurnEnd(turnSeq, text, failed, asked);
    }

    bool ITeamRunState.TryTakeTeamTurnEnd(string sessionId, int turnSeq,
        out string? text, out bool failed, out bool asked)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
        {
            text = null;
            failed = asked = false;
            return false;
        }
        // Делегируем в entry.TryTakeTeamTurnEnd: внутри — TeamTurnLock вокруг ContainsKey
        // + Remove, чтобы забор был атомарен с публикацией turn/completed в шине.
        if (entry.TryTakeTeamTurnEnd(turnSeq, out var t, out var call))
        {
            text = t;
            failed = call is not null && call.Value.Failed;
            asked = call is not null && call.Value.Asked;
            return true;
        }
        text = null;
        failed = asked = false;
        return false;
    }

    // Атомарный pre-claim публикации карточки молчаливого тупика (волна 3 задачи b63fd8ea).
    // Под TeamStateService.WithTeamState (тот же лок, что в PublishTeamEscalationAsync,
    // TeamDecisionService.cs:428) проверяем stalledStage (Interview или Planning && WaveNumber==0);
    // если выполнен — переводим стадию в AwaitingDecision (побочный эффект) и отдаём СНИМОК
    // ПОЛНОГО состояния ДО мутации. Возвращаемый bool — единственный источник правды: либо
    // вызывающий код публикует карточку, либо уже опубликовал параллельный путь.
    // Снимок нужен BuildSilentStallEscalation (читает Stage/WaveNumber) для заголовка карточки,
    // а сам объект уже мутирован внутри лока — снапшок берётся ДО мутации и возвращается через
    // out. Полный набор полей (Stage/StageBeforeDecision/WaveStartedAt/WaveActivityAt) нужен
    // паре: этот метод + RollbackSilentStallClaim — откат клейма при сбое публикации карточки
    // (AppendAsync бросил исключение). Случай «чат удалён» здесь не покрыт — там ранний return
    // БЕЗ исключения, и клеймо остаётся до следующего гарда (отдельный нечастый кейс).
    // Без отката чат зависал бы в AwaitingDecision без карточки: stalledStage для этой стадии
    // больше не true, гард больше никогда не сработает.
    // out-параметры нельзя писать внутри лямбды (CS1628), поэтому захватываем через
    // локальный массив из одного элемента: внутри лямбды — присвоение по индексу,
    // снаружи — чтение после возврата.
    bool ITeamRunState.TryClaimSilentStall(string sessionId, out SilentStallClaim claim)
    {
        claim = default;
        var captured = new SessionTeamImplement?[] { null };
        var claimed = _teamState.WithTeamState(sessionId, t =>
        {
            var stalledStage = t.Stage == TeamImplementStage.Interview
                || (t.Stage == TeamImplementStage.Planning && t.WaveNumber == 0);
            if (!stalledStage)
                return false;
            captured[0] = new SessionTeamImplement
            {
                Stage = t.Stage,
                StageBeforeDecision = t.StageBeforeDecision,
                WaveNumber = t.WaveNumber,
                WaveStartedAt = t.WaveStartedAt,
                WaveActivityAt = t.WaveActivityAt,
            };
            t.StageBeforeDecision = t.Stage;
            t.Stage = TeamImplementStage.AwaitingDecision;
            t.WaveStartedAt = null;
            t.WaveActivityAt = null;
            return true;
        });
        if (claimed && captured[0] is { } snap)
        {
            claim = new SilentStallClaim(
                Stage: snap.Stage,
                WaveNumber: snap.WaveNumber,
                StageBeforeDecision: snap.StageBeforeDecision,
                WaveStartedAt: snap.WaveStartedAt,
                WaveActivityAt: snap.WaveActivityAt);
        }
        return claimed;
    }

    // Откат успешного TryClaimSilentStall. CAS-подобная проверка: восстанавливаем снимок
    // ТОЛЬКО если состояние всё ещё соответствует тому, что оставил клейм (стадия AwaitingDecision
    // и StageBeforeDecision равен исходной стадии из снимка). Прод-сценарий «хвост публикации
    // упал после того, как карточка уже в ленте»: без CAS-гейта стадия бы откатилась в
    // Planning/Interview, и следующий заход гарда поднял бы ВТОРУЮ карточку поверх уже
    // видимой пользователю. С гейтом — false на выходе, состояние не трогается, дублирования
    // нет. Под тем же локом WithTeamState, что и сам claim: между откатом и параллельным
    // новым claim нет гонки — оба сериализуются. Сторона вызова не имеет дела с другими
    // полями SessionTeamImplement и не должна их трогать: меняются ровно те, что заявлены
    // в SilentStallClaim.
    bool ITeamRunState.RollbackSilentStallClaim(string sessionId, SilentStallClaim claim)
    {
        var rolled = false;
        _teamState.WithTeamState(sessionId, t =>
        {
            // Клеймо «живое»: стадия ровно та, что мы поставили в TryClaimSilentStall,
            // и StageBeforeDecision хранит исходную стадию (Planning/Interview) из снимка.
            // Любое другое состояние означает, что либо карточка уже опубликована, либо
            // параллельный путь уже отменил клеймо — откатывать НЕЛЬЗЯ.
            if (t.Stage != TeamImplementStage.AwaitingDecision
                || t.StageBeforeDecision != claim.Stage)
            {
                return true;
            }
            t.Stage = claim.Stage;
            t.StageBeforeDecision = claim.StageBeforeDecision;
            t.WaveStartedAt = claim.WaveStartedAt;
            t.WaveActivityAt = claim.WaveActivityAt;
            rolled = true;
            return true;
        });
        return rolled;
    }

    bool ITeamNotifier.IsSessionBusy(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        return entry.TeamPlanningInFlight;
    }

    // === Четыре шва данных «штаб → ядро» (этап 4, шаг 2г-3б) ===
    // Явные реализации контрактов из Services/Team/TeamCoreSeams.cs: наружу недоступны, штаб
    // ходит через поля-интерфейсы. Тела — обёртки над прежними методами ядра, поведение один
    // в один; в шаге 2г тело штаба уедет в вертикаль, и эти реализации останутся ЕДИНСТВЕННЫМ
    // местом, где вертикаль касается SessionEntry и словаря сессий.
    // ITeamRunState.HasViewers отдельной обёртки не имеет: публичный HasViewers ядра совпадает
    // с контрактом по сигнатуре и реализует его неявно.

    TeamSessionInfo? ITeamSessionDirectory.Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) ? Snapshot(entry.Info) : null;

    // Сброс каталога на диск. Зовёт вертикаль штаба после правки полей режима.
    // Идемпотентен — внутренний лок SaveSessions сериализует записи и под concurrent.
    // Связки «правка + бродкаст» идут не сюда, а в PersistAndBroadcastAsync того же шва.
    void ITeamSessionDirectory.Persist() => SaveSessions();

    // Точка «правка состояния режима + бродкаст»: тело прежнего
    // TeamStateService.SaveTeamImplementStateAsync, перенесено в шов. public-обёртка
    // SessionManager.SaveTeamImplementStateAsync снята вместе с методом TeamStateService —
    // вертикаль (включая публичный TeamWaveService) ходит через _dir напрямую.
    async Task ITeamSessionDirectory.PersistAndBroadcastAsync(string sessionId)
    {
        var session = GetById(sessionId);
        if (session is null) return;
        session.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastTeamImplementAsync(sessionId, session);
    }

    IReadOnlyList<TeamSessionInfo> ITeamSessionDirectory.ListChildren(string parentSessionId) =>
        [.. _sessions.Values.Select(e => e.Info)
            .Where(s => SessionTaskLinks.ParentSessionId(s, _taskLookup) == parentSessionId)
            .Select(Snapshot)];

    ILookup<string, TeamSessionInfo> ITeamSessionDirectory.ChildrenByParent() =>
        _sessions.Values.Select(e => e.Info)
            .Where(s => SessionTaskLinks.ParentSessionId(s, _taskLookup) is not null)
            .ToLookup(s => SessionTaskLinks.ParentSessionId(s, _taskLookup)!, Snapshot);

    // Снимок под нужды штаба: узкий набор полей вместо 40-польной Session (см. TeamSessionInfo).
    // ParentSessionId — вычисляемый (SessionTaskLinks), поэтому снимок строит метод, а не статика.
    private TeamSessionInfo Snapshot(Session s) =>
        new(s.Id, SessionTaskLinks.ParentSessionId(s, _taskLookup), s.ProjectId, s.OwnerId, s.Status, s.UpdatedAt);

    Task<bool> ITeamHistoryStore.MutateCardAsync<T>(string sessionId, Func<T, bool> match, Action<T> mutate)
        => _sessions.TryGetValue(sessionId, out var entry)
            ? MutateStoredAsync(entry, sessionId, match, mutate)
            : Task.FromResult(false);

    Task ITeamHistoryStore.AppendAsync(string sessionId, StoredMessage stored, ServerMessage broadcast)
        => AppendStoredAsync(sessionId, stored, broadcast);

    // Единая точка правки/добавления карточки плана (волна В). Сюда ушла размазанная
    // развилка из шести мест — теперь развилка спрятана внутри шва. Семантика request
    // описана в xml-комментарии ITeamHistoryStore.SavePlanCardAsync.
    async Task<bool> ITeamHistoryStore.SavePlanCardAsync(string sessionId, PlanCardWriteRequest req)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;

        // Ветка аккумулятора: правка через Accumulator (живой ход) + SaveSnapshot.
        // Один путь и для append (OnTeamPlan), и для mutate (OnTeamPlanUpdated /
        // OnTeamPlanSuperseded) — Accumulator разруливает по состоянию карточки сам.
        if (entry.Accumulator is { } acc)
        {
            if (req.SupersededBy is { } sb)
                acc.OnTeamPlanSuperseded(req.Plan.Id, sb);
            else if (req.Resolved)
                acc.OnTeamPlanUpdated(req.Plan.Id, req.Plan, req.Approved);
            else
                acc.OnTeamPlan(req.Plan);
            FireAndForget(acc.SaveSnapshotAsync(_history),
                $"сохранение истории после правки карточки плана ({sessionId})");
            return true;
        }

        // Чат неактивен — пишем карточку прямо в историю на диске под _falPersistLock:
        // та же сериализация, что у внеходовых записей и правок карточек (см. ITeamHistoryStore).
        if (entry.Info.ClaudeSessionId is not string key) return false;
        try
        {
            return await WithFalPersistLockAsync(async () =>
            {
                var stored = await _history.LoadAsync(key);
                // Append — публикация карточки плана.
                if (!req.Resolved && req.SupersededBy is null)
                {
                    stored.Add(new StoredTeamPlanMessage
                    {
                        PlanId = req.Plan.Id,
                        Plan = req.Plan,
                        Resolved = false,
                        Approved = req.Approved,
                        PersonaId = req.Plan.PlannerPersonaId,
                    });
                }
                else
                {
                    // Mutate существующей неразрешённой карточки — иначе двойной клик по
                    // карточке применился бы дважды (та же защита, что у Filter by Resolved
                    // у Accumulator.FindTeamPlan).
                    var card = stored.OfType<StoredTeamPlanMessage>().LastOrDefault(
                        m => m.PlanId == req.Plan.Id && !m.Resolved);
                    if (card is null) return false;
                    if (req.SupersededBy is { } sb)
                    {
                        card.Resolved = true;
                        card.Approved = false;
                        card.SupersededBy = sb;
                    }
                    else
                    {
                        card.Plan = req.Plan;
                        if (req.Resolved)
                        {
                            card.Resolved = true;
                            card.Approved = req.Approved;
                        }
                    }
                }
                await _history.SaveAsync(key, stored);
                return true;
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Прямая запись карточки плана ({SessionId}) не удалась", sessionId);
            return false;
        }
    }

    bool ITeamRunState.HasLiveTurn(string sessionId) => HasLiveTurnProcess(sessionId);

    bool ITeamRunState.HasAsyncAgent(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) && AsyncAgentInFlight(entry);

    bool ITeamRunState.TurnStartedByHuman(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) && entry.TeamTurnFromHuman;

    bool ITeamRunState.IsPlanningInFlight(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) && entry.TeamPlanningInFlight;

    void ITeamRunState.SetPlanningInFlight(string sessionId, bool inFlight)
    {
        if (_sessions.TryGetValue(sessionId, out var entry)) entry.TeamPlanningInFlight = inFlight;
    }

    bool ITeamRunState.ShouldSuppressAsyncAgentStallGuard(string sessionId)
    {
        // Баг b63fd8ea: голое HasAsyncAgent подавляло гард молчаливого тупика бессрочно,
        // пока async-агент писал хоть что-то в stdout (BgLingerTimeout — грейс тишины,
        // а не потолок длительности). Метка подавления AsyncAgentStallSince на SessionEntry
        // живёт под TeamTurnLock (дисциплина, что у TeamTurnText/TeamTurnAsked/TeamTurnFromHuman
        // рядом). entry.Process не лочим: он меняется в других точках ядра, и AsyncAgentInFlight
        // даёт согласованный снимок через HasPendingBg (lock в CliRun).
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        var hasAsync = AsyncAgentInFlight(entry);
        var now = DateTime.UtcNow;
        lock (entry.TeamTurnLock)
        {
            if (!hasAsync)
            {
                // Async-агент ушёл — метку обнуляем, чтобы следующий всплеск не унаследовал
                // длительность от НЕсвязанного прошлого агента.
                if (entry.AsyncAgentStallSince is not null) entry.AsyncAgentStallSince = null;
                return false;
            }
            if (entry.AsyncAgentStallSince is null) entry.AsyncAgentStallSince = now;
            return TeamAsyncAgentStallGuard.ShouldSuppress(true, entry.AsyncAgentStallSince, now,
                TeamAsyncAgentStallGuard.DefaultSuppressionTimeout);
        }
    }

    Task<bool> ITeamTurnIntake.SendOrEnqueueAsync(string sessionId, string text,
        string? senderPersonaId, bool silent, bool suppressTasksExecute, string? staffNote)
        => SendOrEnqueueAsync(sessionId, text, senderPersonaId,
            silent: silent, suppressTasksExecute: suppressTasksExecute, staffNote: staffNote);

    void ITeamTurnIntake.InterruptTurn(string sessionId) => InterruptCore(sessionId, byUser: false);

    // Публикация карточки остановки: запись в ленту + WS + стадия «ждёт решения».
    // Тело в TeamDecisionService (волна Д).
    Task ITeamHistoryStore.PublishTeamEscalationAsync(string sessionId, TeamEscalation escalation) =>
        _teamDecision.PublishTeamEscalationAsync(sessionId, escalation);

    // Открытые (не resolved) карточки остановки чата.
    Task<IReadOnlyList<TeamEscalation>> ITeamHistoryStore.GetOpenTeamEscalationsAsync(string sessionId) =>
        _teamDecision.GetOpenTeamEscalationsAsync(sessionId);

    // Пометка отправленного напоминания по карточке остановки.
    Task<bool> ITeamHistoryStore.MarkTeamEscalationRemindedAsync(string sessionId, string escalationId) =>
        _teamDecision.MarkTeamEscalationRemindedAsync(sessionId, escalationId);

    // План итерации по id: Accumulator.FindTeamPlanAny для активного чата,
    // TeamStateService.GetTeamPlanFromHistoryAsync для неактивного.
    async Task<TeamImplementPlan?> ITeamHistoryStore.GetTeamPlanAsync(string sessionId, string planId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        if (entry.Accumulator is { } acc) return acc.FindTeamPlanAny(planId);
        if (entry.Info.ClaudeSessionId is not string key) return null;
        return await _teamState.GetTeamPlanFromHistoryAsync(key, planId);
    }

    // Координатор задал вопрос ASK-карточкой (Э8). В интервью это очередной раунд (их не
    // больше двух на вводную — счёт ведёт бэкенд, модель своих раундов не помнит).
    // Вне интервью вопрос живёт внутри хода и практику НЕ останавливает (решение по запросу
    // владельца 2026-08-04 — единый канал вопросов): продуктовая развилка или уточнение
    // задаётся ASK, ответ приходит в тот же ход и работа продолжается с того же места.
    // Возврат в интервью с паузой волн и перепланированием остался только за явным маркером
    // <escalate:clarify> («требования неясны и действовать нельзя») — прежний вход сюда из
    // ASK делал из любого вопроса пересборку плана.
    internal async Task OnAskQuestionStabAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is not { } team) return;

        if (team.Stage == TeamImplementStage.Interview)
        {
            WithTeamState(sessionId, t => { t.InterviewRounds++; return true; });
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
            await BroadcastTeamImplementAsync(sessionId, entry);
        }
        else if (team.Stage == TeamImplementStage.Wave)
        {
            // Пока человек отвечает на ASK, гасим отсечки сторожа волн — ожидание человека
            // не зависание (тот же приём, что у стадии «ждёт решения» в
            // PublishTeamEscalationAsync). Иначе при долгом ответе карточка WaveStalled легла
            // бы поверх живого вопроса. Отсечки вернёт конец хода (HandleTeamTurnEndAsync).
            WithTeamState(sessionId, t =>
            {
                t.WaveStartedAt = null;
                t.WaveActivityAt = null;
                return true;
            });
            entry.Info.UpdatedAt = DateTime.UtcNow;
            SaveSessions();
        }

        // Вопрос ждёт человека: уведомление и push, если его нет в чате — звать человека
        // надо в любой стадии, иначе ход молча ждёт клика («молчаливых пауз не бывает»).
        if (_teamCoordinator.QuestionNotifier is { } notify)
        {
            try { await notify(entry.Info); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Уведомление о вопросе интервью ({SessionId}) не отправлено", sessionId);
            }
        }
    }

    // Эскалация, поднятая самим координатором маркером в ходе (расхождение с планом, красная
    // проверка; продуктовый вопрос ушёл в ASK — маркер decision здесь только фолбэк).
    // Заголовки — из таблицы «Эскалация и остановки».
    // Публичный (волна Ж): TeamTurnCompletionService.HandleTeamTurnEndAsync вызывает при
    // разборе маркера эскалации из turnText и при инфраструктурном обрыве хода проверки —
    // тонкая публикация карточки по EscalationRaiser координатора (иначе без push/уведомления).
    internal async Task RaiseCoordinatorEscalationAsync(string sessionId, TeamEscalationKind kind, string details)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is not { } team) return;

        var escalation = new TeamEscalation
        {
            Kind = kind,
            Title = TeamImplementPrompts.EscalationTitle(kind, details),
            Details = details,
            Wave = team.WaveNumber,
            Actions = TeamEscalationActions.For(kind),
        };
        if (_teamCoordinator.EscalationRaiser is { } raise) await raise(entry.Info, escalation);
        else await ((ITeamHistoryStore)this).PublishTeamEscalationAsync(sessionId, escalation);
    }

    // Отдельное git worktree чата: вкл — создать дерево на новой ветке от HEAD проекта и
    // перевести туда рабочую папку сессии; выкл — вернуть чат в корень проекта и снять дерево.
    // Начатый чат переезжает С КОНТЕКСТОМ: транскрипт CLI копируется в папку нового cwd
    // (--resume ищет его по уплощённому cwd); не удалось скопировать — операция отменяется,
    // контекст дороже фичи. У container-пользователя и профиль, и cwd берутся в песочной
    // раскладке (ConfigRootFor/CwdForOwner) — переезд работает так же, как на хосте.
    // Процесс не трогаем: между ходами его нет, AdapterStale пересоберёт контекст со
    // следующего хода.
    public async Task<Session?> SetWorktreeAsync(string sessionId, bool enabled,
        string? branch = null, bool force = false, string? userId = null)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return null;
        var ownerId = ResolveOwnerId(entry.Info);
        if (userId is not null && ownerId != userId) return null;
        if (_git is null)
            throw new InvalidOperationException("Git-операции недоступны");
        if (entry.Info.ProjectId is not string projectId)
            throw new InvalidOperationException("Отдельное дерево доступно только в чате проекта");
        var project = _projects.GetById(projectId)
            ?? throw new InvalidOperationException("Проект не найден");
        // Дерево и перенос транскрипта под новый cwd — git и диск проекта: у локального
        // проекта они на устройстве, отказ до обращения к диску
        Composition.ProjectCapabilityGuard.EnsureAllowed(project, Composition.ProjectCapabilityArea.FileBound);

        // Идемпотентность: повторное включение/выключение — no-op
        if (enabled == (entry.Info.WorktreePath is not null)) return entry.Info;

        if (enabled)
        {
            if (!Path.Exists(Path.Combine(project.RootPath, ".git")))
                throw new Git.GitCommandException("В папке проекта нет git-репозитория");

            // Ветка: заданная вручную либо wt/<slug имени чата>; коллизии решаем суффиксом
            var slug = PersonaManager.Slugify(entry.Info.Name ?? "");
            if (slug.Length == 0) slug = sessionId[..Math.Min(8, sessionId.Length)];
            var branchName = string.IsNullOrWhiteSpace(branch) ? $"wt/{slug}" : branch.Trim();
            var taken = (await _git.BranchesAsync(ownerId, project.RootPath))
                .Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unique = branchName;
            for (var n = 2; taken.Contains(unique); n++) unique = $"{branchName}-{n}";
            branchName = unique;

            // Папка: {home}/.worktrees/<проект>/<ветка> — вне дерева главной репы и
            // гарантированно внутри Sandbox:ProjectsRoot у container-пользователей
            var user = ownerId is null ? null : _users.GetById(ownerId);
            var home = (user is null ? null : _homes.Resolve(user))
                ?? throw new InvalidOperationException("Не удалось определить домашнюю папку владельца");
            var projSlug = PersonaManager.Slugify(project.Name);
            if (projSlug.Length == 0) projSlug = project.Id[..Math.Min(8, project.Id.Length)];
            var wtPath = Path.Combine(home, ".worktrees", projSlug, branchName.Replace('/', '-'));
            Directory.CreateDirectory(Path.GetDirectoryName(wtPath)!);

            // Рабочие папки ГЛАЗАМИ CLI считаем ДО создания дерева: у container-пользователя
            // ToRuntime отвергает путь вне монтирований, а исключение после WorktreeAddAsync
            // обошло бы rollback ниже и оставило дерево-сироту. ClaudeSessionId читаем ОДИН
            // РАЗ здесь же: WorktreeAddAsync ниже содержит await, и повторное чтение после
            // него могло бы увидеть другое значение (null → появился, если параллельно
            // стартовал первый ход чата), а srcCwd/dstCwd остались бы не посчитаны
            (string Csid, string Src, string Dst)? migration = entry.Info.ClaudeSessionId is string csid0
                ? (csid0, CwdForOwner(ownerId, project.RootPath), CwdForOwner(ownerId, wtPath))
                : null;

            await _git.WorktreeAddAsync(ownerId, project.RootPath, wtPath, branchName);

            if (migration is { } m
                && !Llm.TranscriptMigrator.TryRelocateCwd(
                    ConfigRootFor(ownerId, entry.Info.Provider), m.Src, m.Dst, m.Csid, out var err))
            {
                // Дерево без контекста бесполезно — откатываем и отдаём причину наружу
                try { await _git.WorktreeRemoveAsync(ownerId, project.RootPath, wtPath, force: true); }
                catch { /* уборка best-effort */ }
                throw new Git.GitCommandException($"Не удалось перенести контекст разговора: {err}");
            }

            entry.Info.WorktreePath = wtPath;
            entry.Info.WorktreeBranch = branchName;
            _warmup.TryStart(project, wtPath);
        }
        else
        {
            var wtPath = entry.Info.WorktreePath!;
            // Как и при включении — перевод путей до снятия дерева (см. выше), ClaudeSessionId
            // читаем один раз здесь же (ниже есть await StatusAsync/WorktreeRemoveAsync)
            (string Csid, string Src, string Dst)? migration = entry.Info.ClaudeSessionId is string csid0
                ? (csid0, CwdForOwner(ownerId, wtPath), CwdForOwner(ownerId, project.RootPath))
                : null;

            // Гейт: незакоммиченные правки в дереве пропадут вместе с ним (ветка остаётся)
            if (!force)
            {
                var st = await _git.StatusAsync(ownerId, wtPath);
                if (st.Staged.Count > 0 || st.Unstaged.Count > 0 || st.Untracked.Count > 0)
                    throw new Git.GitCommandException(
                        "В отдельном дереве есть несохранённые изменения — зафиксируйте их или подтвердите принудительное удаление");
            }

            if (migration is { } m
                && !Llm.TranscriptMigrator.TryRelocateCwd(
                    ConfigRootFor(ownerId, entry.Info.Provider), m.Src, m.Dst, m.Csid, out var err))
                throw new Git.GitCommandException($"Не удалось перенести контекст разговора: {err}");

            await _git.WorktreeRemoveAsync(ownerId, project.RootPath, wtPath, force);
            entry.Info.WorktreePath = null;
            entry.Info.WorktreeBranch = null;
            ReleaseWorktreeGraph(sessionId, wtPath);
        }

        // Следующий ход пересоздаст адаптер с новым cwd (между ходами процесса нет)
        entry.AdapterStale = true;
        SaveSessions();
        return entry.Info;
    }

    /// <summary>
    /// Привязать чат к УЖЕ СУЩЕСТВУЮЩЕМУ дереву (чат-исполнитель задачи с worktree в её поле):
    /// проставляет Session.WorktreePath/WorktreeBranch без создания дерева и переноса
    /// транскрипта — этим он и отличается от SetWorktreeAsync. Вызывать до первого хода:
    /// cwd процесса подставит EffectiveRoot сам, а начатый чат переезжает только с контекстом
    /// (SetWorktreeAsync). Дерево должно лежать на диске и числиться в «git worktree list»
    /// репы проекта. false — привязка не состоялась: вызывающий стартует в корне проекта.
    /// </summary>
    public async Task<bool> AttachWorktreeAsync(string sessionId, string worktreePath, string? branch = null)
    {
        if (string.IsNullOrWhiteSpace(worktreePath)) return false;
        if (!_sessions.TryGetValue(sessionId, out var entry)) return false;
        // Дерево бывает только у чата проекта; начатый чат не трогаем — его контекст
        // привязан к прежнему cwd (--resume ищет транскрипт по уплощённому пути)
        if (entry.Info.ProjectId is not string projectId || entry.Info.ClaudeSessionId is not null) return false;
        if (_projects.GetById(projectId) is not { } project || _git is null) return false;

        var path = Path.GetFullPath(worktreePath.Trim());
        if (!Directory.Exists(path)) return false;

        // Числится ли дерево в главной репе: чужая (или уже снятая) папка увела бы ход мимо
        // проекта. Сравнение нормализованными путями — как у worktree-чатов в CodeGraphController.
        var wanted = WorkspaceKnowledgeStore.NormalizePath(path);
        GitWorktreeInfo? known;
        try
        {
            known = (await _git.WorktreeListAsync(ResolveOwnerId(entry.Info), project.RootPath))
                .FirstOrDefault(w => WorkspaceKnowledgeStore.NormalizePath(w.Path) == wanted);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось прочитать git worktree list проекта {Project}", project.Id);
            return false;
        }
        if (known is null) return false;

        entry.Info.WorktreePath = path;
        // Ветка задана вызывающим либо берётся из самого дерева (метка в git-баре чата)
        entry.Info.WorktreeBranch = string.IsNullOrWhiteSpace(branch) ? known.Branch : branch.Trim();
        entry.AdapterStale = true;
        SaveSessions();
        // Дерево задачи заводит не сервер, а человек/агент, — прогрев здесь, при первой привязке;
        // уже собранное (obj/ есть) или уже прогретое дерево прогрев пропускает сам
        _warmup.TryStart(project, path);
        return true;
    }

    // Потолок добиваний подряд. Две попытки — сознательный предел: бесконечный цикл добивания
    // хуже честного отказа, после него судьбу работы решает человек.
    internal const int MaxSubagentNudges = 2;

    /// <summary>
    /// Слать ли добивание оборванного сабагента. Чистая функция — вся политика в одном месте
    /// (под тестом). Уступаем всем, у кого свой протокол продолжения: цикл «до готово» и штаб
    /// продолжат ход сами, непустая очередь продолжит его сообщением, а параллельный ход цикла
    /// (LoopTurnInFlight) уже в пути — второй systemDirective ушёл бы в тот же процесс.
    ///
    /// <paramref name="isInterruptedRun"/> — признак обрыва класса «убит прерыванием хода»
    /// (паспорт FinishedBy == "interrupted"): такие обрывы счётчик MaxSubagentNudges не
    /// расходуют (причина в нашем коде и не свидетельствует о неисправном агенте), а
    /// автоматически слать сообщение «продолжи» означало бы действовать за человека — он
    /// сам нажал «Стоп», решение о продолжении принадлежит ему.
    /// </summary>
    internal static bool ShouldNudgeSubagent(int nudgesSent, bool workLoopActive, bool teamActive,
        bool hasPending, bool loopTurnInFlight, bool isInterruptedRun = false) =>
        !isInterruptedRun && nudgesSent < MaxSubagentNudges
        && !workLoopActive && !teamActive && !hasPending && !loopTurnInFlight;

    /// <summary>
    /// Ход координатора в полёте: сообщение ему уже отдано в процесс и result ещё не пришёл.
    /// Пока это так, второй systemDirective-ход слать нельзя (он уйдёт в тот же процесс) —
    /// добивание ждёт конца хода, координатору достаётся только пометка.
    /// </summary>
    internal static bool TurnInFlight(SessionStatus status) =>
        status is SessionStatus.Starting or SessionStatus.Working or SessionStatus.Waiting;

    /// <summary>
    /// Обрыв ФОНОВОГО агента: пометка координатору обязательна всегда, добивание — только
    /// если ход координатора не идёт (иначе ждём result, как раньше).
    /// </summary>
    private void NoteTruncatedBgAgent(string sessionId, SessionEntry entry,
        Llm.SubagentRunPassport run)
    {
        // Пометка уедет префиксом ближайшего хода — чем бы он ни был поднят (человеком,
        // очередью, добиванием): координатор обязан узнать, что обрывок не итог, даже когда
        // добивать мы не стали.
        entry.TruncatedBgNote = run;

        if (entry.Process is null || TurnInFlight(entry.Info.Status)) return;
        // Оборвался другой агент — у него своя серия попыток (счётчик per-agentId)
        if (StartsNudgeSeries(entry.NudgeAgentId, run.AgentId)) entry.SubagentNudges = 0;
        if (!ShouldNudgeSubagent(entry.SubagentNudges, entry.Info.WorkLoop is not null,
                entry.Info.TeamImplement is not null, HasPending(entry), entry.LoopTurnInFlight,
                isInterruptedRun: run.FinishedBy == "interrupted")) return;

        // Разбираем отметку здесь — иначе по ближайшему result добивание ушло бы вторым разом
        entry.TruncatedSubagent = null;
        entry.NudgeAgentId = run.AgentId;
        var attempt = ++entry.SubagentNudges;
        _ = Task.Run(async () =>
        {
            try
            {
                // Ход мог стартовать, пока планировалась отправка (очередь, автоматизация,
                // цикл): откатываем попытку и возвращаем отметку — добивание уедет по его
                // result, штатным путём. Второй ход в живой процесс не отправляем никогда.
                if (TurnInFlight(entry.Info.Status))
                {
                    entry.SubagentNudges = Math.Max(0, entry.SubagentNudges - 1);
                    entry.TruncatedSubagent = run;
                    return;
                }
                await NudgeTruncatedSubagentAsync(sessionId, run, attempt);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Добивание фонового сабагента ({sessionId}): {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Снимает ли штатный отчёт агента серию добиваний. Счётчик общий на сессию, а агентов
    /// в ходе несколько — поэтому серию закрывает ТОТ ЖЕ агент, которого добивали (либо любой,
    /// пока серии нет). Чужой отчёт счётчик не трогает: иначе потолок MaxSubagentNudges
    /// не достигается вовсе и чат крутит системные директивы автономно.
    /// </summary>
    internal static bool ResetsNudgeSeries(string? nudgeAgentId, string reportedAgentId) =>
        nudgeAgentId is null || nudgeAgentId == reportedAgentId;

    /// <summary>
    /// Начинает ли оборвавшийся агент новую серию: попытки считаются per-agentId, поэтому
    /// другой агент получает свои две, а не остаток чужих.
    /// </summary>
    internal static bool StartsNudgeSeries(string? nudgeAgentId, string truncatedAgentId) =>
        nudgeAgentId != truncatedAgentId;

    /// <summary>
    /// Гасит ли штатный отчёт агента пометку обрыва. Пометка бывает ложной: bg_agent_done
    /// обгоняет дозапись финала в транскрипт, и по хвосту tool_use агент числится оборванным,
    /// хотя дописал end_turn. Опровергает пометку только отчёт ТОГО ЖЕ агента.
    /// </summary>
    internal static bool RefutesTruncation(string? markedAgentId, string reportedAgentId) =>
        markedAgentId == reportedAgentId;

    // Добивание: директива координатору дослать оборванному сабагенту продолжение. Тем же
    // способом, что и цикл «до готово» (systemDirective-ход после result), и с тем же
    // потолком попыток — см. SubagentPrompts.ResumeTruncated. Для класса «убит прерыванием»
    // (FinishedBy == "interrupted") политика сюда не пустит (ShouldNudgeSubagent с
    // isInterruptedRun: true), но если когда-то дойдёт — текст берётся по-другому
    // (ResumeInterrupted: не «дослать продолжить», а «ход прерван, транскрипт цел»).
    private async Task NudgeTruncatedSubagentAsync(string sessionId,
        Llm.SubagentRunPassport run, int attempt)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry) || entry.Process is null) return;
        // Обрыв опровергнут, пока добивание планировалось (финал агента доехал до транскрипта
        // после перепроверки): последний паспорт агента уже штатный — директиву не шлём.
        // Источник истины — стор паспортов, а не пометки на сессии: их разобрали до планирования
        if (_subagentRuns?.Latest(run.AgentId) is { Truncated: false }) return;
        // Директива добивания уже несёт все факты обрыва — дублировать их пометкой
        // в том же ходе незачем (пометка чужого агента остаётся ждать своего хода)
        if (entry.TruncatedBgNote?.AgentId == run.AgentId) entry.TruncatedBgNote = null;
        _subagentRuns?.NoteNudge(run.AgentId);
        _log.LogWarning("Сабагент {AgentId} ({AgentType}) оборвался на {Tool} после {Tools} вызовов " +
            "и {Seconds} с (контекст {Context} токенов) — добивание {Attempt}/{Max}, класс {Class}, чат {SessionId}",
            run.AgentId, run.AgentType, run.LastTool, run.ToolUses, run.DurationSeconds,
            run.ContextTokens, attempt, MaxSubagentNudges, run.FinishedBy, sessionId);
        var directive = run.FinishedBy == "interrupted"
            ? Prompts.SubagentPrompts.ResumeInterrupted(run, attempt, MaxSubagentNudges)
            : Prompts.SubagentPrompts.ResumeTruncated(run, attempt, MaxSubagentNudges);
        await SendMessageAsync(sessionId, directive,
            [], systemDirective: true, cause: DeliveryCause.SubagentNudge);
    }

    // Автопродолжение цикла «до готово»: вызывается по result хода, нёсшего протокол цикла.
    // Маркер найден → верификационный ход, затем стоп; нет → продолжение до лимита итераций
    // либо уход в фазу waiting, если у чата есть живые делегированные задачи.
    private async Task ContinueWorkLoopAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (entry.Info.WorkLoop is not { } loop) return;

        // Возврат из waiting: предыдущая итерация ушла в ожидание исполнителя, и теперь
        // (доставлен доклад, человек вмешался, либо пришёл алерт молчания) цикл реально
        // продолжает работу. Снимаем фазу ДО гейтов, чтобы фаза не «застряла» при уступке.
        // Заодно обнуляем счётчик тиков и причину: новая фаза waiting (если будет) стартует
        // с чистого счётчика, а старая причина в логе/бейдже «ожидание» уже неактуальна.
        //
        // ИНВАРИАНТ: ++ за result хода тратится РОВНО ОДИН РАЗ — здесь (если был возврат
        // waiting→working) ИЛИ в конце метода (если блок не сработал). Две точки кода,
        // взаимоисключающие по флагу wasReturnFromWaiting: иначе result после возврата
        // засчитывался бы дважды. Тики ожидания НЕ считаются итерациями — сама фаза
        // waiting бесплатна по счёту. Проверка лимита — общий хелпер
        // StopIfWorkLoopLimitReachedAsync: один текст остановки и один путь reason="limit",
        // без двух копий.
        var wasReturnFromWaiting = false;
        if (loop.Phase == "waiting")
        {
            loop.Phase = "working";
            loop.WaitingTicks = 0;
            loop.WaitingReason = null;
            loop.WaitingSince = null;
            wasReturnFromWaiting = true;
            loop.Iteration++;
            SaveSessions();
            await BroadcastWorkLoopAsync(sessionId, entry);
            if (await StopIfWorkLoopLimitReachedAsync(sessionId, entry, loop)) return;
        }

        // Без двойной отправки (гонка «result → директива продолжения» vs «очередь доставляет
        // сообщение пользователя»): если пользователь успел прислать сообщение в этом ходе или
        // между итерациями, оно само продолжит цикл как следующая итерация (доставку выполнит
        // drain). Доклад исполнителя (Report) — аналогично: drain его вытащит, и ход-реакция
        // вернёт цикл к работе. Системную директиву продолжения/верификации в этом случае не
        // шлём — иначе два хода подряд ушли бы в один процесс. Проверка атомарна с извлечением
        // drain'а: LoopTurnInFlight=true означает, что drain уже вытащил сообщение и запускает
        // итерацию. Маркер взводим ПОД ТЕМ ЖЕ PendingLock, что и гейт (Major 3): иначе в окне до
        // BuildCliTurnText (SaveSessions/Broadcast/EnsureProcess, сотни мс) параллельный drain
        // успевал вытащить пользовательское сообщение и пустить второй ход в тот же процесс.
        // До всей логики цикла (phase/iteration) — чтобы не оставлять изменённое состояние при уступке.
        lock (entry.PendingLock)
        {
            if (entry.Pending.Any(p => p.Kind is PendingKind.User or PendingKind.Report)) return;
            if (entry.LoopTurnInFlight) return;
            entry.LoopTurnInFlight = true;
        }

        try
        {
            // Буфер потребляем и чистим здесь (а не при постановке хода): очистка на постановке
            // стирала бы текст стримящегося хода при параллельной отправке пользователя
            string turnText;
            lock (entry.LoopTurnLock)
            {
                turnText = entry.LoopTurnText.ToString();
                entry.LoopTurnText.Clear();
            }

            if (entry.LoopTurnFailed)
            {
                await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "error", "Цикл остановлен: ход завершился ошибкой.");
                await SetWorkLoopAsync(sessionId, false);
                return;
            }

            var promiseFound = ContainsPromiseMarker(turnText, loop.Promise);

            if (loop.Phase == "verifying")
            {
                // Верификационный ход отработал — цикл завершён независимо от исхода (штатное
                // окончание, свидетельства уже в самом верификационном посте — отдельное
                // сообщение-остановка тут не нужно, в отличие от лимита/ошибки/ручного стопа).
                // Блокер тут НЕ проверяем намеренно: верификация уже отвечает «да/нет», и
                // второй слой семантики поверх неё не нужен.
                await SetWorkLoopAsync(sessionId, false);
                return;
            }

            // ПОРЯДОК ВАЖЕН: блокер проверяем раньше промиса. Если модель вывела оба, это
            // противоречие («готово, но встал») — дешевле встать сразу по блокеру, чем
            // гонять верификационный ход по сомнительному «готово» и плодить ещё одну
            // итерацию до лимита. Не переставляй местами «как более логичный» порядок.
            if (TryExtractBlockedMarker(turnText, out var blockedReason))
            {
                var notice = blockedReason is null
                    ? "Цикл остановлен: работа встала на блокере."
                    : $"Цикл остановлен: работа встала на блокере — {blockedReason}";
                await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "blocked", notice);
                // SetWorkLoopAsync(false) сама сбрасывает LoopTurnInFlight и размораживает
                // очередь — своей уборки рядом не добавляем, иначе отстаём от инварианта.
                await SetWorkLoopAsync(sessionId, false);
                return;
            }

            if (promiseFound)
            {
                loop.Phase = "verifying";
                SaveSessions();
                await BroadcastWorkLoopAsync(sessionId, entry);
                if (entry.Info.WorkLoop is null) return; // Стоп успел снять цикл — ход-сироту не шлём
                await SendMessageAsync(sessionId, OmoPrompts.WorkLoopVerification, [], systemDirective: true);
                return;
            }

            // ПОРЯДОК ВАЖЕН: маркер `<waiting>` проверяем ПОСЛЕ `<promise>`. Если модель вывела
            // оба, это противоречие («готово» и одновременно «жду»), и верификационный ход
            // разрешит его дешевле, чем лишний круг ожидания. Симметрично блокеру выше.
            //
            // ФАЗА ОЖИДАНИЯ: у чата есть живые делегированные задачи (координатор запустил
            // исполнителя и не получил доклада) ИЛИ координатор сам вывел `<waiting>`
            // (ждёт внешнего события, о котором система знать не может — ответ на chats_send,
            // чужой процесс, человек вне чата). Пока ждём — итерации не тратим, директиву
            // продолжения НЕ шлём. ВАЖНО: LoopTurnInFlight снимаем под PendingLock — иначе
            // drain (DrainNextPendingAsync) вечно уступает на гейте, и доклад не доедет
            // НИКОГДА. Цикл повиснет намертво. Возврат — на приходе Report/user-сообщения
            // (DrainNextPendingAsync) и в начале самого ContinueWorkLoopAsync: фаза
            // «waiting» переключается обратно в «working» ДО гейтов выше.
            var waitingReason = TryExtractWaitingMarker(turnText, out var waitingFromMarker);
            var liveDelegated = HasLiveDelegatedTasks?.Invoke(sessionId) == true;
            if (waitingFromMarker != null || liveDelegated)
            {
                var reason = waitingFromMarker; // null → ожидание по живой задаче (доклад придёт сам)
                // Переустанавливаем счётчик ТОЛЬКО при смене причины ожидания: маркер
                // пришёл впервые (или сменился текст) — стартуем с чистого счётчика;
                // тот же маркер в повторной итерации (модель тика не вывела, вернулась
                // с тем же текстом) — продолжаем счёт. Иначе координатор, который
                // каждые 5 минут выводит ОДНО И ТО ЖЕ, тикал бы вечно без шанса дойти
                // до лимита.
                var newWaiting = waitingFromMarker != null && waitingFromMarker != loop.WaitingReason;
                if (newWaiting)
                {
                    loop.WaitingReason = waitingFromMarker;
                    loop.WaitingSince = DateTime.UtcNow;
                    loop.WaitingTicks = 0;
                }
                lock (entry.PendingLock) entry.LoopTurnInFlight = false;
                loop.Phase = "waiting";
                SaveSessions();
                await BroadcastWorkLoopAsync(sessionId, entry);
                if (entry.Info.WorkLoop is null) return; // Стоп успел снять цикл — без хода
                _log.LogInformation("Цикл {Session} ушёл в фазу ожидания (причина: {Reason}, итераций {Iter}/{Max})",
                    sessionId,
                    liveDelegated ? "живая делегированная задача" : (waitingFromMarker ?? "не указана"),
                    loop.Iteration, loop.MaxIterations);
                return;
            }

            // Чистый working-ход (без возврата из ожидания): Iteration тратится здесь — счётчик
            // считает все ходы цикла. Возврат из ожидания уже инкрементировал выше (флаг
            // wasReturnFromWaiting=true), так что ++ здесь срабатывает ТОЛЬКО для обычных
            // ходов. Тики ожидания бесплатны — они не доходят до этой ветки. Проверка лимита
            // и текст остановки — общий хелпер.
            if (!wasReturnFromWaiting) loop.Iteration++;
            SaveSessions();
            await BroadcastWorkLoopAsync(sessionId, entry);
            if (await StopIfWorkLoopLimitReachedAsync(sessionId, entry, loop)) return;
            if (entry.Info.WorkLoop is null) return; // Стоп успел снять цикл — ход-сироту не шлём
            await SendMessageAsync(sessionId,
                OmoPrompts.WorkLoopContinuation(loop.Promise, loop.Iteration, loop.MaxIterations), [], systemDirective: true);
        }
        catch
        {
            // Директива так и не ушла (сбой до/в отправке) — ход result не пришлёт, и взведённый
            // выше маркер повис бы, заблокировав разбор очереди (drain уступает, пока он взведён).
            // Пути остановки снимают цикл через SetWorkLoop(false) — та сбрасывает маркер сама;
            // здесь ловим только исключение до отправки. Перебрасываем — вызывающий (Task.Run по
            // result) залогирует.
            if (entry.Info.WorkLoop is not null)
                entry.LoopTurnInFlight = false;
            throw;
        }
    }

    // Общая проверка лимита итераций для цикла «до готово». Вызывается ПОСЛЕ каждого
    // инкремента Iteration (в блоке возврата waiting→working и в конце обычного working-хода) —
    // одна точка правды для reason="limit" и текста остановки. Возвращает true, если лимит
    // достигнут и цикл остановлен (вызывающий обязан сделать return). Хелпер общий, а не
    // две копии, чтобы текст и путь стопа не разъехались при будущих правках.
    private async Task<bool> StopIfWorkLoopLimitReachedAsync(string sessionId, SessionEntry entry, SessionWorkLoop loop)
    {
        if (loop.Iteration < loop.MaxIterations) return false;
        await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "limit",
            $"Цикл остановлен: исчерпан лимит в {loop.MaxIterations} ходов. " +
            "Работа могла остаться незавершённой — проверьте результат.");
        await SetWorkLoopAsync(sessionId, false);
        return true;
    }

    // Маркер завершения ищем вне код-блоков и с точным регистром: модель часто цитирует
    // протокол в начале хода («когда закончу — выведу `<promise>…</promise>`») — бэктики
    // и ``` не считаются исполнением обещания
    internal static bool ContainsPromiseMarker(string text, string promise)
    {
        var stripped = StripCodeBlocks(text);
        return stripped.Contains($"<promise>{promise}</promise>", StringComparison.Ordinal);
    }

    // Маркер блокера ищем по тем же правилам, что и промис (вне код-блоков и инлайн-кода,
    // регистр тега точный): модель цитирует протокол в начале хода — бэктики/``` не считаются
    // реальной остановкой. Причину возвращаем в первом вхождении (после схлопывания \r\n и
    // обрезки до 300 символов — она едет в ленту текстом уведомления). Пустой тег
    // `<blocked></blocked>` — валидная остановка без причины: TryExtract вернёт true и
    // reason == null.
    internal static bool TryExtractBlockedMarker(string text, out string? reason)
    {
        reason = null;
        var stripped = StripCodeBlocks(text);
        var match = System.Text.RegularExpressions.Regex.Match(
            stripped, "<blocked>([\\s\\S]*?)</blocked>");
        if (!match.Success) return false;

        var raw = match.Groups[1].Value ?? string.Empty;
        // Схлопываем переводы строк (в т.ч. \r\n) в пробел и подрезаем края.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(raw, "\\s+", " ").Trim();
        if (collapsed.Length == 0) return true;
        if (collapsed.Length > 300) collapsed = collapsed[..300];
        reason = collapsed;
        return true;
    }

    // Маркер ожидания — симметричен блокеру: вне код-блоков, точный регистр, пустой тег
    // валиден. Это второй источник ухода в фазу waiting (первый — живая делегированная задача,
    // HasLiveDelegatedTasks): модель ждёт внешнего события, о котором система знать не может
    // (ответ на chats_send, чужой процесс, человек вне чата). Без этого координатор либо
    // жжёт итерации, либо выводит `<blocked>` и валит цикл — оба варианта врут про состояние.
    internal static bool TryExtractWaitingMarker(string text, out string? reason)
    {
        reason = null;
        var stripped = StripCodeBlocks(text);
        var match = System.Text.RegularExpressions.Regex.Match(
            stripped, "<waiting>([\\s\\S]*?)</waiting>");
        if (!match.Success) return false;

        var raw = match.Groups[1].Value ?? string.Empty;
        var collapsed = System.Text.RegularExpressions.Regex.Replace(raw, "\\s+", " ").Trim();
        if (collapsed.Length == 0) return true;
        if (collapsed.Length > 300) collapsed = collapsed[..300];
        reason = collapsed;
        return true;
    }

    // Тик ожидания по маркеру `<waiting>`. Обходит все активные сессии раз в
    // Loop:WaitingTickSeconds и шлёт координатору системную директиву-тик, если:
    //   - цикл включён И фаза == "waiting" И WaitingReason != null (по маркеру, не по задаче);
    //   - цикл включён И фаза == "waiting" И WaitingReason != null (по маркеру, не по задаче);
    //   - с момента входа в фазу прошло ≥ интервала (первый тик — через полный интервал);
    //   - чат СВОБОДЕН: нет живого прогона (HasLiveTurn=false), нет маркера хода-итерации
    //     (LoopTurnInFlight=false), статус не Working/Waiting (нет текущего хода).
    // Тикаем ТОЛЬКО ожидание по маркеру: ожидание по живой делегированной задаче
    // (HasLiveDelegatedTasks=true, WaitingReason=null) тикать НЕ надо — доклад придёт сам,
    // а смерть исполнителя ловит алерт молчания. Разводим явно: ветка `WaitingReason != null`.
    // На потолке (WaitingTicks >= _maxWaitingTicks) — стоп с reason="waiting_timeout" и
    // причиной ожидания в тексте уведомления.
    // internal: тесты SessionManagerTests зовут напрямую (через рефлексию для подмены
    // _waitingTickInterval). В боевом коде вызывается только фоновым таймером _waitingTickTimer.
    internal async Task TickWaitingLoopsAsync()
    {
        // Копия id под перебор: тик может уводить цикл в стоп и чистить entry —
        // _sessions меняется «под нами», итерировать оригинал нельзя.
        var sessionIds = _sessions.Keys.ToArray();
        foreach (var sessionId in sessionIds)
        {
            if (!_sessions.TryGetValue(sessionId, out var entry)) continue;
            if (entry.Info.WorkLoop is not { } loop) continue;
            if (loop.Phase != "waiting") continue;
            if (loop.WaitingReason is null) continue; // ожидание по задаче — не тикаем

            // Чат занят — пропускаем. Следующий тик через интервал догонит.
            if (entry.LoopTurnInFlight) continue;
            if (entry.Info.Status is SessionStatus.Working or SessionStatus.Waiting) continue;
            if (HasLiveTurnProcess(sessionId)) continue;

            // С момента входа в фазу прошло ≥ интервала? WaitingSince ставится на входе
            // и не сбрасывается на тиках — отсюда и первый тик через полный интервал, и
            // все последующие через интервал (таймер сам по себе периодичен).
            var sinceUtc = (loop.WaitingSince ?? DateTime.UtcNow).ToUniversalTime();
            if (DateTime.UtcNow - sinceUtc < _waitingTickInterval) continue;

            loop.WaitingTicks++;
            if (loop.WaitingTicks >= _maxWaitingTicks)
            {
                var notice = $"Цикл остановлен: ожидание по маркеру «{loop.WaitingReason}» " +
                             $"не завершилось за {loop.WaitingTicks * (int)_waitingTickInterval.TotalSeconds / 60} минут " +
                             $"({loop.WaitingTicks} из {_maxWaitingTicks} тиков).";
                _log.LogWarning("Цикл {Session}: исчерпан потолок тиков ожидания ({Ticks}/{Max})",
                    sessionId, loop.WaitingTicks, _maxWaitingTicks);
                // AddWorkLoopStoppedNoticeAsync сам персистит и рассылает; стоп идёт
                // через SetWorkLoopAsync, которая сбрасывает LoopTurnInFlight и фазу.
                await AddWorkLoopStoppedNoticeAsync(sessionId, entry, "waiting_timeout", notice);
                await SetWorkLoopAsync(sessionId, false);
                continue;
            }

            // Тик НЕ тратит Iteration — только WaitingTicks. Директива уходит системной:
            // координатор увидит подсказку и сам решит, выводить ли `<waiting>` снова.
            // BuildCliTurnText взводит LoopTurnInFlight (под LoopTurnLock), а ContinueWorkLoopAsync
            // по result хода-тика проверит, остался ли маркер, и вернёт фазу.
            _log.LogInformation("Цикл {Session}: тик ожидания {Ticks}/{Max} (причина: {Reason})",
                sessionId, loop.WaitingTicks, _maxWaitingTicks, loop.WaitingReason);
            SaveSessions();
            await BroadcastWorkLoopAsync(sessionId, entry);
            if (entry.Info.WorkLoop is null) continue; // стоп успел снять цикл
            await SendMessageAsync(sessionId,
                OmoPrompts.WorkLoopWaitingTick(loop.WaitingReason,
                    loop.WaitingTicks, _maxWaitingTicks,
                    (int)_waitingTickInterval.TotalSeconds),
                [], systemDirective: true);
        }
    }

    // Общая чистка код-блоков и инлайн-кода — используется детекторами маркеров протокола
    // цикла «до готово». Бэктики и ``` не считаются исполнением обещания/блокера: модель
    // часто цитирует протокол в начале хода («выведу `<promise>…</promise>` когда закончу»).
    private static string StripCodeBlocks(string text)
    {
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, "```[\\s\\S]*?(```|$)", "");
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, "`[^`\n]*`", "");
        return stripped;
    }

    public void AnswerQuestion(string sessionId, string toolUseId, string answerText)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (IsStaleInteractionAnswer(sessionId, entry, $"вопрос {toolUseId}")) return;
        entry.Process?.AnswerQuestion(toolUseId, answerText);
        entry.PendingInteraction = null;
        object? answers = null;
        try
        {
            using var doc = JsonDocument.Parse(answerText);
            if (doc.RootElement.TryGetProperty("answers", out var a))
                answers = JsonSerializer.Deserialize<object>(a.GetRawText());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SessionManager] Ответ на вопрос ({sessionId}) не распарсился: карточка погаснет без сводки выбора (и в историю, и в рассылку уйдёт без answers): {ex.Message}");
        }
        // Фиксируем ответ в истории, чтобы карточка вопроса пережила перезагрузку
        if (entry.Accumulator is not null)
        {
            entry.Accumulator.OnQuestionAnswered(toolUseId, answers);
            FireAndForget(entry.Accumulator.SaveSnapshotAsync(_history),
                $"сохранение истории после ответа на вопрос ({sessionId})");
        }
        FireAndForget(BroadcastAsync(sessionId,
                new InteractionResolvedMessage("question", toolUseId, Answers: answers)),
            $"рассылка ответа на вопрос ({sessionId})");
        FireAndForget(ApplyStatusAsync(sessionId, entry, SessionStatus.Working),
            $"смена статуса после ответа на вопрос ({sessionId})");
    }

    public void RespondPlan(string sessionId, string requestId, bool approve, string? feedback)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        if (IsStaleInteractionAnswer(sessionId, entry, $"план {requestId}")) return;
        entry.Process?.RespondPlan(requestId, approve, feedback);
        entry.PendingInteraction = null;
        // Фиксируем решение по плану в истории, чтобы карточка пережила перезагрузку
        if (entry.Accumulator is not null)
        {
            entry.Accumulator.OnPlanResolved(requestId, approve, feedback);
            FireAndForget(entry.Accumulator.SaveSnapshotAsync(_history),
                $"сохранение истории после решения по плану ({sessionId})");
        }
        FireAndForget(BroadcastAsync(sessionId,
                new InteractionResolvedMessage("plan", requestId, Approved: approve, Feedback: feedback)),
            $"рассылка решения по плану ({sessionId})");
        FireAndForget(ApplyStatusAsync(sessionId, entry, SessionStatus.Working),
            $"смена статуса после решения по плану ({sessionId})");
    }

    // Решение (Minor, волна 3, осознанно оставлено как есть): удаление чата-штаба НЕ отменяет
    // живые волны, НЕ трогает дочерние чаты исполнения (Session.ParentSessionId вычисляется из
    // Task.SourceSessionId — после удаления штаба они всплывают в корень дерева чатов) и не
    // закрывает под-задачи явно — эскалации незакрытых волн (TeamWaveService.RaiseEscalationAsync
    // → PublishTeamEscalationAsync) молча падают в лог: `_sessions.TryGetValue` не находит
    // удалённую запись и пишет предупреждение, дальше эскалация никуда не уходит.
    // Почему не чиним сейчас: полный каскад (остановить живые процессы исполнителей, решить,
    // что делать с их чатами — удалять, переносить в корень явной карточкой или оставлять как
    // есть, закрыть/пометить под-задачи) — самостоятельная фича с продуктовыми развилками
    // (UX «осиротевших» чатов), а не точечный фикс. Удаление чата с фоновой работой уже и
    // так ничего не отменяет каскадно нигде в системе (обычная задача с исполнителем — тот
    // же класс). Ниже — минимальная страховка: явный лог, чтобы находка не терялась молча.
    public async Task DeleteAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var entry)) return;
        if (entry.Info.TeamImplement is { WaveNumber: > 0 } liveTeam && liveTeam.WaveNumber > liveTeam.ClosedWave)
            _log.LogWarning("Чат-штаб {SessionId} удалён посреди незакрытой волны {Wave} — " +
                "живые задачи и дочерние чаты исполнения не отменяются и не переносятся " +
                "(осознанное решение волны 3, см. комментарий у DeleteAsync)", sessionId, liveTeam.WaveNumber);
        // Стоп снапшотам ДО удаления файлов: финализация прогона (drain сабагентов,
        // поздние tool_result) доигрывается после dispose и пересоздала бы history.json
        entry.Accumulator?.MarkDeleted();
        if (entry.Process is not null)
            await entry.Process.DisposeAsync();
        // Дочищаем историю на диске — иначе data/sessions/{id} копится мусором
        if (entry.Info.ClaudeSessionId is string csid)
        {
            // И история, и транскрипт CLI могут быть ОБЩИМИ у двух чатов: сессия, созданная с
            // resumeSessionId (POST /sessions, /chats, /personas/{id}/chats), несет тот же
            // ClaudeSessionId. Пока на него ссылается другой чат, не трогаем ни то, ни другое:
            // иначе у него пропадет лента в UI (история) и вся память разговора (транскрипт —
            // его читает --resume). Свою запись из реестра мы вынули выше, поэтому Any видит
            // только чужие ссылки.
            if (_sessions.Values.Any(e => e.Info.ClaudeSessionId == csid))
                _log.LogInformation(
                    "История и транскрипт чата {SessionId} оставлены: на сессию {ClaudeSessionId} ссылается другой чат",
                    entry.Info.Id, csid);
            else
            {
                _history.Delete(csid);
                // Локальный проект: транскрипт на устройстве, архивной копии сервер не делал —
                // искать и удалять файл по своему пути значило бы задеть чужой
                if (TranscriptOnServer(entry.Info))
                {
                    DeleteTranscript(entry.Info, csid);
                    // Архивная копия транскрипта уходит вместе с чатом — иначе переписка
                    // переживёт его и в data, и в бэкапе. Гейт тот же (общий csid у чата-двойника)
                    _archivedTranscripts.Delete(csid);
                }
            }
        }
        // Снимки промпта ключуются id ЧАТА, а не транскриптом, — гейт общего разговора выше
        // к ним не относится: у чата-двойника свои снимки, и его лента их не потеряет
        _promptSnapshots?.DeleteAll(sessionId);
        // История ЛОКАЛЬНЫХ голосовых ходов пишется под id чата (до первого CLI-хода у чата
        // нет ClaudeSessionId) — чистим всегда: id чата уникален, общим с другим чатом быть
        // не может. Убиратся и файл-дубль после перехода локаль→CLI (история тогда уже
        // пишется под csid, а старая папка оставалась бы мусором до удаления чата). Гейт
        // снизу — единственный экзотический случай:resume-чата с ClaudeSessionId, равным
        // id ЭТОГО чата (resumeSessionId валидируется белым списком, но может совпадать).
        var localHistoryKey = entry.Info.Id.ToString();
        if (!_sessions.Values.Any(e => e.Info.ClaudeSessionId == localHistoryKey))
            _history.Delete(localHistoryKey);

        // Отдельное worktree чата сносим вместе с чатом (best-effort; ветка остаётся в репе)
        if (entry.Info.WorktreePath is string wt && entry.Info.ProjectId is string wpid && _git is not null)
        {
            try
            {
                if (_projects.GetById(wpid) is { } wproj)
                    await _git.WorktreeRemoveAsync(ResolveOwnerId(entry.Info), wproj.RootPath, wt, force: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionManager] Worktree чата не удалён ({sessionId}): {ex.Message}");
            }
            ReleaseWorktreeGraph(sessionId, wt);
        }
        SaveSessions();
        try { OnSessionDeleted?.Invoke(entry.Info); } catch { /* наблюдатель не должен ронять удаление */ }
        await BroadcastChatDeletedAsync(sessionId, entry.Info);
    }

    // Транскрипт claude CLI удаленного чата ({профиль}/projects/{уплощенный cwd}/{csid}.jsonl).
    // Своя история (data/sessions/{csid}) уходит через ChatHistoryService, а этот файл раньше
    // не убирал никто — переписка удаленного чата лежала на диске до плановой уборки CLI
    // (~30 дней), хотя воспользоваться ей уже нельзя: --resume делать некому.
    //
    // Ищем во ВСЕХ профилях, а не только в текущем: переезды между профилями (TryMigrate при
    // смене провайдера) и между рабочими папками (TryRelocateCwd при worktree) намеренно
    // оставляют копии. Точность обеспечивает сам ключ — файл называется uuid'ом сессии, так
    // что чужие чаты, другие инстансы сервера и интерактивные сессии пользователя, живущие в
    // том же ~/.claude, задеть невозможно.
    //
    // Best-effort: удаление чата важнее уборки, поэтому любой сбой остается в логе.
    private void DeleteTranscript(Session info, string claudeSessionId)
    {
        try
        {
            var removed = Llm.TranscriptMigrator.DeleteEverywhere(
                TranscriptSearchRoots(info), TryResolveCwd(info), claudeSessionId);
            if (removed > 0)
                _log.LogInformation("Транскрипт чата {SessionId} удален ({Count} файлов)", info.Id, removed);
            else
                // Штатных причин две: транскрипт уже вычистил сам CLI (плановая уборка ~30 дней)
                // либо ходов в чате не было. Но сюда же попадает «файл нашелся, а удалить не
                // дали» — поэтому не утверждаем, что убирать было нечего, и отправляем за
                // подробностями в лог TranscriptMigrator
                _log.LogInformation(
                    "Транскрипт чата {SessionId} не убран: не найден либо не удалился (подробности выше)",
                    info.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Не удалось убрать транскрипт чата {SessionId}", info.Id);
        }
    }

    // Подготовленная ветвлением пара «{csid}.jsonl + data/sessions/{csid}» осиротела: CLI на
    // первом ходе ветки выдал другой session_id, и подложенный транскрипт уже никто не
    // прочитает (история с этого момента пишется под новым ключом). Гейт общего разговора —
    // тот же, что в DeleteAsync: пока на csid ссылается другой чат (двойник, созданный с тем
    // же resumeSessionId), не трогаем ни историю, ни транскрипт. Сам чат-ветка под этот гейт
    // уже не попадает — его ClaudeSessionId к этому моменту переписан на пришедший от CLI.
    // Best-effort: уборка не должна ронять ход, все сбои — в лог (внутри DeleteTranscript).
    private void CleanupOrphanBranchTranscript(Session info, string orphanCsid)
    {
        if (_sessions.Values.Any(e => e.Info.ClaudeSessionId == orphanCsid))
        {
            _log.LogInformation(
                "Ветка {SessionId}: CLI сменил сессию на другую, но подготовленный {Csid} оставлен — на него ссылается другой чат",
                info.Id, orphanCsid);
            return;
        }
        _log.LogInformation(
            "Ветка {SessionId}: CLI выдал свой session_id — убираем осиротевшую подготовленную сессию {Csid}",
            info.Id, orphanCsid);
        try { _history.Delete(orphanCsid); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Историю осиротевшей сессии {Csid} убрать не удалось", orphanCsid);
        }
        DeleteTranscript(info, orphanCsid);
    }

    // Уведомить клиентов об удалении чата (в т.ч. авто-удалении временного) —
    // адресация как у BroadcastStatusChangeAsync: проект или владелец чата
    private async Task BroadcastChatDeletedAsync(string sessionId, Session info)
    {
        var msg = new ChatDeletedMessage() with { SessionId = sessionId };
        var tasks = new List<Task> { _broadcaster.ToSession(sessionId, msg) };
        if (info.ProjectId is string pid)
            tasks.Add(_broadcaster.ToProject(pid, msg));
        else if (info.OwnerId is string oid)
            tasks.Add(_broadcaster.ToOwner(oid, msg));
        await Task.WhenAll(tasks);
    }

    public async Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry))
            return [];

        IReadOnlyList<StoredMessage> list;
        if (entry.Accumulator != null)
            list = entry.Accumulator.GetAll();
        else if (entry.Info.ClaudeSessionId != null)
            list = await _history.LoadAsync(entry.Info.ClaudeSessionId);
        else
            list = [];

        // Догоняем стоимость старых fal-генераций, у которых её ещё нет (фоном, дедуп внутри)
        BackfillFalCosts(sessionId, list);
        // Догоняем учёт старых glif-генераций, у которых ещё нет glif_cost
        BackfillGlifCosts(sessionId, list);
        return list;
    }

    public IReadOnlyList<WorkflowProgressMessage> GetWorkflowProgress(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return [];
        // Снимок под локом: словарь конкурентно мутируют таймер-потоки ватчеров
        lock (entry.WorkflowProgress) return entry.WorkflowProgress.Values.ToList();
    }

    // Ожидающая карточка взаимодействия (разрешение/вопрос/план) — replay при JoinSession
    public ServerMessage? GetPendingInteraction(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) ? entry.PendingInteraction : null;

    // Последний манифест recall (F3) сессии — replay при подключении клиента (JoinSession),
    // как и у workflow_progress: без этого «использовано сейчас» видно только тем, кто был
    // на связи в момент самого хода.
    public RecallManifestMessage? GetLastRecallManifest(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var entry) ? entry.LastRecallManifest : null;

    public Session? GetSessionInfo(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var entry);
        return entry?.Info;
    }

    // --- Внутренняя логика ---

    // runId — прогон адаптера, чей read-loop прислал сообщение (0 у внутренних вызовов без
    // прогона): по нему отличаем exited ЭТОГО прогона от позднего exited доживающего
    private async Task OnMessageAsync(string sessionId, TurnAccumulator acc, ServerMessage msg, long runId = 0)
    {
        _sessions.TryGetValue(sessionId, out var entry);
        // Волна 6: живая трансляция хода штаба фильтрует маркеры протокола (см. кейс ниже) —
        // иногда дельте нечего транслировать (весь её текст — часть маркера/незавершённый
        // хвост), тогда исходное сообщение до низового BroadcastAsync не доходит вовсе.
        var sendBroadcast = true;

        // Аккумулятор — отдельный try, чтобы ошибка сохранения истории
        // не заблокировала обновление статуса и широковещание.
        try
        {
            switch (msg)
            {
                case SessionStartedMessage m:
                    // Страховка ветвления: CLI 2.1.276 сессию по --resume не форкает (разведка
                    // шага 0), но это поведение версионно-зависимое. Ветке мы подложили
                    // транскрипт и историю под СВОИМ csid — если CLI выдал другой session_id,
                    // подготовленная пара осиротела и её надо убрать, иначе она пролежит на
                    // диске до плановой уборки CLI, а в data/sessions останется навсегда.
                    // Гейт BranchedFromSessionId — у обычных чатов проверка бесплатна.
                    if (entry is { Info.BranchedFromSessionId: not null }
                        && acc.SaveKey is { Length: > 0 } preparedCsid
                        && !string.Equals(preparedCsid, m.ClaudeSessionId, StringComparison.Ordinal))
                        CleanupOrphanBranchTranscript(entry.Info, preparedCsid);
                    acc.SetSaveKey(m.ClaudeSessionId);
                    acc.OnSessionStarted(m.Model, m.Mode, m.TurnWorktree);
                    if (entry is not null)
                    {
                        entry.TurnInWorktree = m.TurnWorktree != null;
                        // Этап 4 / шаг 1а: фиксируем TurnSeq текущего хода для слота осушенного
                        // буфера. SubmittedTurnSeq инкрементируется в SubmitTurn
                        // (ClaudeSession.cs:2351), а SessionStartedMessage шлётся после неё
                        // (ClaudeSession.cs:4099) — к моменту нашего чтения номер уже актуален.
                        // 0 — локальный голосовой ход (адаптер не Claude, SubmittedTurnSeq = 0),
                        // turn/completed по нему не публикуется, слот не пополняется.
                        entry.LastTurnSeq = entry.Process is { } adapter
                            ? (int)adapter.SubmittedTurnSeq
                            : 0;
                    }
                    SaveSessions();
                    break;
                case TextDeltaMessage m:
                    acc.OnTextDelta(m.Text);
                    // Цикл «до готово»: копим текст хода для поиска маркера завершения
                    if (entry?.Info.WorkLoop is not null)
                        lock (entry.LoopTurnLock) entry.LoopTurnText.Append(m.Text);
                    // Маркеры протокола в живой ленте: копим текст хода и решаем, что из
                    // накопленного уже безопасно показать человеку (волна 6). Целиком
                    // завершённый маркер вырезаем, незавершённый хвост придерживаем до
                    // следующей дельты — иначе полтега мелькнёт на экране раньше, чем мы
                    // поймём, что это протокол, а не текст персоны. Режим штаба ещё и ловит
                    // отсюда маркер эскалации координатора (разбор — на конце хода).
                    // Стрижка идёт в ЛЮБОМ чате, не только в штабе: сохранённая история
                    // чистится безусловно (TurnAccumulator.FlushBuffers), и без этого маркер
                    // (в т.ч. `<no-reply/>` постановщика) мелькал бы в стриме и пропадал
                    // после перезагрузки страницы.
                    if (entry is not null)
                    {
                        string? displayDelta;
                        lock (entry.TeamTurnLock)
                        {
                            entry.TeamTurnText.Append(m.Text);
                            if (!entry.TurnSawAngleBracket && m.Text.Contains('<'))
                                entry.TurnSawAngleBracket = true;
                            if (!entry.TurnSawAngleBracket)
                            {
                                // Быстрый путь обычного чата: без '<' маркеру взяться неоткуда,
                                // стрижка вернула бы тот же текст — не платим за ToString()
                                // всего хода на каждой дельте.
                                entry.TeamTurnShownLength += m.Text.Length;
                                displayDelta = m.Text;
                            }
                            else
                            {
                                var safe = TeamProtocolMarkers.TrimAmbiguousMarkerTail(
                                    TeamProtocolMarkers.TrimUnresolvedMarkerOpen(TeamProtocolMarkers.StripTeamProtocolMarkers(entry.TeamTurnText.ToString())));
                                // Пока в очищенном тексте нет ни одного непробельного символа,
                                // показывать нечего: ход, ответивший ровно маркером, не должен
                                // родить в ленте пустой пузырь из «\n» вокруг маркера. Длину
                                // показанного не двигаем — придержанные пробелы уйдут вместе с
                                // первым настоящим текстом, если он появится.
                                if (safe.Length > entry.TeamTurnShownLength && safe.Trim().Length > 0)
                                {
                                    displayDelta = safe[entry.TeamTurnShownLength..];
                                    entry.TeamTurnShownLength = safe.Length;
                                }
                                else displayDelta = null;
                            }
                            // Всё накопленное показано и ничего не придержано — маркеру в
                            // буфере взяться неоткуда. В обычном чате буфер нужен ровно для
                            // показа, поэтому сжимаем его: иначе каждая следующая дельта
                            // хода с кодом (там '<' на каждом шагу) пересканировала бы весь
                            // ход целиком — квадрат по длине ответа. В штабе так нельзя:
                            // там весь текст хода разбирается на маркеры в его конце.
                            if (entry.Info.TeamImplement is null
                                && entry.TeamTurnShownLength == entry.TeamTurnText.Length)
                            {
                                entry.TeamTurnText.Clear();
                                entry.TeamTurnShownLength = 0;
                                entry.TurnSawAngleBracket = false;
                            }
                        }
                        if (string.IsNullOrEmpty(displayDelta)) sendBroadcast = false;
                        else msg = m with { Text = displayDelta };
                    }
                    break;
                case ThinkingDeltaMessage m: acc.OnThinkingDelta(m.Text); break;
                case AgentTextMessage m:
                    acc.OnAgentText(m.ParentToolUseId, m.Text);
                    await acc.SaveSnapshotAsync(_history); // reload посреди долгого сабагента видит уже написанное
                    break;
                case AgentThinkingMessage m: acc.OnAgentThinking(m.ParentToolUseId, m.Text); break;
                case ToolUseMessage m:
                    acc.OnToolUse(m.Id, m.Name, m.Input, m.ParentToolUseId);
                    TryUnmarkCommittedOnToolUse(sessionId, entry, m.Name, m.Input);
                    if (entry is not null && SpendMapping.TryExtractHiggsfieldGeneration(m.Name))
                        SpendMapping.RecordHiggsfieldGeneration(_spend, ResolveOwnerId, _log, entry.Info);
                    break;
                case ToolResultMessage m:
                    acc.OnToolResult(m.ToolUseId, m.Content, m.IsError);
                    await acc.SaveSnapshotAsync(_history); // промежуточное сохранение после каждого tool call
                    TryTrackFalCost(sessionId, m.Content); // fire-and-forget: стоимость придёт позже
                    TryTrackGlifCost(sessionId, m.Content); // синхронно: кредиты уже в tool_result
                    break;
                case WorkflowProgressMessage m:
                    if (entry is not null)
                    {
                        // Кэш мутируют таймер-потоки ватчеров (несколько workflow = несколько
                        // потоков), читает JoinSession — только под локом
                        lock (entry.WorkflowProgress)
                        {
                            if (m.IsDone) entry.WorkflowProgress.Remove(m.ToolUseId);
                            else entry.WorkflowProgress[m.ToolUseId] = m;
                        }
                    }
                    // Последний снапшот — в историю: карточка workflow и вкладка «Агенты»
                    // должны переживать перезагрузку страницы и рестарт сервера
                    acc.OnWorkflowProgress(m.ToolUseId, m.IsDone, m.Agents);
                    if (m.IsDone) await acc.SaveSnapshotAsync(_history);
                    break;
                case BgAgentDoneMessage m:
                    acc.OnBgAgentsDone(m.ToolUseIds);
                    await acc.SaveSnapshotAsync(_history);
                    // Волна 3 задачи b63fd8ea: хук на завершение фонового async-агента.
                    //
                    // Снимок «есть ли ещё живой async-агент» берётся ПОСЛЕ учёта текущего
                    // сообщения — гонки «читаем старое состояние» нет ни на одном из четырёх
                    // источников BgAgentDoneMessage. Все четыре ДО отправки сообщения уже
                    // убрали завершившуюся задачу из run.PendingBg (а для FinalizeRunAsync —
                    // из всего словаря):
                    //   • HandleStructuredTaskNotification (ClaudeSession.cs:5055) — снимает
                    //     задачу через `lock (run.PendingBg) run.PendingBg.Remove(taskId, ...)`
                    //     синхронно и публикует BgAgentDoneMessage через Task.Run из того же
                    //     пути, что HandleStructuredTaskNotification читает;
                    //   • HandleTaskNotification (ClaudeSession.cs:4954) — то же самое, по
                    //     текстовому <task-notification> вместо структурного события;
                    //   • HandleTaskOutputCompletion (ClaudeSession.cs:4989) — снимает через
                    //     `lock (run.PendingBg) run.PendingBg.Remove(agentId, ...)` и
                    //     публикует через Task.Run;
                    //   • FinalizeRunAsync (ClaudeSession.cs:3706) — единственный путь,
                    //     где публикация идёт inline await (не Task.Run): прогон уже умер,
                    //     `lock (run.PendingBg) { orphanedTools = run.PendingBg.Values...;
                    //     run.PendingBg.Clear(); }` происходит ДО вызова CompleteBgTasksAsync
                    //     и до возврата await, поэтому HasPendingBg к моменту чтения ниже
                    //     уже отражает очищенный словарь.
                    //
                    // Решения (Major + Minor 2, оба на одной проверке HasAsyncAgent):
                    //   • Метка AsyncAgentStallSince сбрасывается ТОЛЬКО когда после
                    //     текущего done агентов больше нет вообще (HasAsyncAgent == false).
                    //     Безусловный сброс (как в волне 2) перезапускал 10-минутный
                    //     потолок подавления на каждом агенте цепочки — суппрессия
                    //     тянулась неограниченно, ровно против того, что баг b63fd8ea
                    //     чинил.
                    //   • Карточка молчаливого тупика поднимается ТОЛЬКО когда (а) агент
                    //     умер абортивно (Aborted=true) и (б) других живых async-агентов
                    //     больше нет (HasAsyncAgent == false). Иначе — структурный
                    //     task_notification ставит Aborted для ОДНОГО агента, пока
                    //     параллельно работает ДРУГОЙ: карточка «Координатор не понял
                    //     вводную» сразу на первом аборте уводила стадию в AwaitingDecision,
                    //     хотя координатор ЖИВ — дословный регресс P16 (Major, найден
                    //     ревью Глеба).
                    //
                    // Карточка публикуется через TeamTurnCompletionService.HandleBgAgentDoneAsync
                    // — та же точка, что HandleTeamTurnEndAsync, и обе сериализуются на
                    // общем локе TeamStateService.WithTeamState через TryClaimSilentStall
                    // (Minor 1, идемпотентность): гонка «оба пути публикуют одновременно»
                    // закрывается атомарным pre-claim — первый путь переводит стадию в
                    // AwaitingDecision под локом, второй видит её и выходит.
                    if (entry is not null)
                    {
                        // Снимок HasAsyncAgent — ПОСЛЕ учёта текущего сообщения (см. шапку):
                        // HandleStructuredTaskNotification (ClaudeSession.cs:5055) уже удалил
                        // задачу из run.PendingBg синхронно до публикации BgAgentDoneMessage,
                        // entry.Process?.HasPendingBg отражает обновлённое состояние.
                        var hasAsync = AsyncAgentInFlight(entry);
                        bool asked;
                        lock (entry.TeamTurnLock)
                        {
                            // Minor 2: метка сбрасывается ТОЛЬКО когда после текущего done
                            // других async-агентов больше нет. Безусловный сброс перезапускал
                            // бы 10-минутный потолок подавления на КАЖДОМ агенте цепочки —
                            // суппрессия тянулась бы неограниченно (ровно то, против чего
                            // баг b63fd8ea был написан).
                            if (!hasAsync && entry.AsyncAgentStallSince is not null)
                                entry.AsyncAgentStallSince = null;
                            asked = entry.TeamTurnAsked;
                        }
                        await HandleBgAgentDoneAsync(sessionId, m.Aborted, hasAsync, asked);
                    }
                    break;
                // Присутствие фона — сигнал для СПИСКА чатов, а не для ленты: в историю не
                // пишем (состояние живёт ровно столько, сколько процесс) и статус сессии не
                // трогаем — ApplyStatusAsync двигал бы UpdatedAt, а по нему идут сортировка,
                // секции дерева и непрочитанность. Рассылка — в session + project/user-группы
                case BgAgentsPresenceMessage m:
                    await BroadcastSessionMessageAsync(sessionId, m);
                    break;
                case FileChangedMessage m:
                    acc.OnFileChanged(m.Path, m.Added, m.Removed, m.External);
                    // External не гейтим: сырое множество индекса содержит и внешние правки
                    // (фильтр «только файлы чата») — пометка снимается любой из них
                    if (entry is { TurnInWorktree: false })
                        UnmarkFileCommitted(sessionId, SessionChangedPaths.Normalize(m.Path));
                    break;
                case CompactBoundaryMessage m:
                    acc.OnCompactBoundary(m.Trigger, m.PreTokens, m.PostTokens);
                    await acc.SaveSnapshotAsync(_history); // авто-компакт бывает посреди хода — фиксируем сразу
                    break;
                case AskQuestionMessage m:
                    acc.OnAskQuestion(m.ToolUseId, m.Input);
                    await acc.SaveSnapshotAsync(_history);
                    // Э8: вопрос координатора штаба — раунд интервью. Из волны или ожидания
                    // он же возвращает практику в интервью (волны на паузе, карточка + push).
                    if (entry?.Info.TeamImplement is not null)
                    {
                        // Ход задал вопросы — гард молчаливого тупика по его концу молчит (M9)
                        lock (entry.TeamTurnLock) entry.TeamTurnAsked = true;
                        await _teamNotifier.OnAskQuestionStabAsync(sessionId);
                    }
                    break;
                case PlanReviewMessage m:
                    acc.OnPlanReview(m.RequestId, m.Plan);
                    await acc.SaveSnapshotAsync(_history);
                    break;
                case RecallManifestMessage m:
                    if (entry is not null) entry.LastRecallManifest = m;
                    break;
                case PromptSnapshotMessage m:
                    // Привязываем снимок к сообщению, которым начался ход: под ним живёт
                    // кнопка «какой промпт ушёл». Событие приходит ДО result — иначе
                    // FlushAsync уже унёс бы текущий ход из _currentTurn.
                    acc.SetPromptSnapshot(m.SnapshotId);
                    break;
                case ResultMessage m:
                    // Точный якорь границы хода для ветвления (фича chat-branch, §4): uuid
                    // последней записи транскрипта на КОНЕЦ хода. Снимается здесь, потому что
                    // result — последнее событие хода, и дальше файл уже не растёт до
                    // следующего. Не прочиталось — null, чат остаётся на текстовом пути.
                    await acc.OnResultAsync(m.Subtype, m.DurationMs, m.NumTurns, m.Usage, m.TotalCostUsd, m.ApiErrorStatus, m.PermissionDenials, _history, m.ContextTokens, m.UsageModel, m.DurationApiMs,
                        entry is not null ? Llm.Claude.TranscriptProbe.LastRecordUuid(FindResumeTranscript(entry)) : null);
                    if (entry is not null) entry.LoopTurnFailed = m.Subtype == "error";
                    SpendMapping.RecordTurnSpend(_spend, _llmProviders, ResolveOwnerId, _log, entry?.Info, m);
                    break;
                case ProviderSwitchedMessage m:
                    // Пометка автоподмены модели в историю — после F5/рестарта человек видит,
                    // что отвечала не та модель, что был выбран. Уровень 1 (ротация подписок)
                    // модель не трогает — пилюля там не нужна; адаптер шлёт Auto=true без
                    // Model, OnModelSwitched в таком случае no-op.
                    // ErrorDetails — сырой текст погашенной подменой ошибки провайдера: красной
                    // карточки в ленте нет, но текст доступен «Подробностями» внутри пометки,
                    // поэтому кладём его в историю вместе с ней.
                    if (m.Auto && !string.IsNullOrEmpty(m.Model))
                        acc.OnModelSwitched(m.Model, acc.LastStartedModel(), m.Reason, m.ErrorDetails);
                    break;
                case RateLimitMessage m:
                    {
                        // Учёт лимита — единая точка SubscriptionLimitRecorder (её же зовёт шлюз
                        // LLM по заголовкам anthropic-ratelimit-unified-*). Здесь — только реакция
                        // на свой ход. Подавленное событие выходит из обработчика целиком, как и
                        // до выноса: дальше для rate_limit делать нечего.
                        var limit = _limitRecorder.Record(entry?.Info.Provider, m, "turn",
                            () => entry?.Process is FallbackLlmSessionAdapter fb && fb.FallbackTurnActive,
                            $"ход {sessionId}");
                        if (limit == LimitRecordOutcome.Suppressed)
                            return;
                        if (limit == LimitRecordOutcome.Exhausted && entry is not null)
                        {
                            // Сразу перевозим чат на здоровый аккаунт пула — кнопка «Повторить»
                            // упавшего хода пойдёт уже через него. Если переключиться некуда,
                            // а ход реально отбит — предлагаем сторонний провайдер карточкой.
                            TryPoolFailover(sessionId, entry);
                            if (m.Status == "rejected")
                                await OfferProviderFallbackAsync(sessionId, m.ResetsAt);
                        }
                    }
                    break;
                case ErrorMessage m:
                    // Сюда доезжают только настоящие провалы: промежуточную ошибку попытки,
                    // за которой пошла подмена, адаптер наружу не выпускает (её текст едет
                    // в ErrorDetails маркера) — значит и LoopTurnFailed на ней не взводится.
                    // Details — сырой техтекст под «Подробностями» карточки.
                    await acc.OnErrorAsync(m.Text, _history, m.Details);
                    // Ошибка хода (в т.ч. упавший старт процесса) — цикл «до готово»
                    // не продолжаем; иначе ретрай-шторм до лимита итераций
                    if (entry is not null) entry.LoopTurnFailed = true;
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SessionManager] Ошибка аккумулятора ({sessionId}): {ex.Message}");
        }

        // Резолв ожидателя синхронного хода (SendMessageAndWaitAsync): result — штатное
        // завершение, error/exited — обрыв (резолвим тоже, чтобы вызывающий не завис).
        // Обнуляем безусловно — ожидатель не должен утечь в следующий ход.
        if (entry is not null && msg is ResultMessage or ErrorMessage or ExitedMessage
            && Interlocked.Exchange(ref entry.TurnWaiter, null) is { } waiter)
        {
            switch (msg)
            {
                case ResultMessage rm:
                    waiter.TrySetResult(new TurnResult(
                        LastAssistantText(acc, entry.TurnWaiterBaseline), rm.DurationMs, rm.TotalCostUsd));
                    break;
                case ErrorMessage em:
                    waiter.TrySetResult(new TurnResult(em.Text, 0, null));
                    break;
                case ExitedMessage:
                    // Прерван без result — отдаём то, что ассистент успел написать
                    waiter.TrySetResult(new TurnResult(
                        LastAssistantText(acc, entry.TurnWaiterBaseline), 0, null));
                    break;
            }
        }

        // Этап 4 / шаг 2в (MAJOR 1 этап 4, доработка швов): бэкстоп
        // `RestoreWaveWatchdogIfPaused` на любом `ExitedMessage` возвращён. Шим
        // `HandleTeamTurnCompletedShim` тоже зовёт его на `interrupted | crashed`,
        // а `HandleTeamTurnEndAsync:7593` — на `success | failed | egress_down |
        // local_down`: всего бэкстоп здесь выглядит избыточным. НО он покрывает случай,
        // когда план в `LastTeamTurnEnds` не нашёлся (вытеснение потолком 8, чужой
        // `TurnSeq`): шим уходит с WARN и без восстановления, и единственный путь
        // сохранить отсечки сторожа — вызвать здесь, не дожидаясь подписчика.
        // Двойной вызов с шимом/HandleTeamTurnEnd идемпотентен
        // (`WaveStartedAt = DateTime.UtcNow` поверх себя), контракт «один исход —
        // одна публикация» шины не нарушается: бэкстоп жёстко локальный и срабатывает
        // только после того, как downstream получил `ExitedMessage`.
        if (msg is ExitedMessage && entry is not null)
            RestoreWaveWatchdogIfPaused(sessionId, entry);

        // Обновление статуса — всегда, независимо от аккумулятора; SessionManager —
        // ЕДИНСТВЕННЫЙ владелец переходов Session.Status (ClaudeSession статус не пишет).
        // Если OnResultAsync выбросит, статус всё равно обновится.
        if (entry is not null)
        {
            SessionStatus? newStatus = null;

            if (msg is PermissionRequestMessage or AskQuestionMessage or PlanReviewMessage)
            {
                newStatus = SessionStatus.Waiting;
                // Кэш для replay при JoinSession: после F5 клиент должен снова увидеть
                // карточку, которую ждёт CLI
                entry.PendingInteraction = msg;
            }
            else if (msg is ResultMessage rm)
                // Active (не Finished): клиент по active перезагружает историю хода;
                // финальный Finished выставится по ExitedMessage ниже
                newStatus = rm.Subtype == "error" ? SessionStatus.Error : SessionStatus.Active;
            else if (msg is ErrorMessage)
                newStatus = SessionStatus.Error;
            else if (msg is ExitedMessage)
                newStatus = entry.Info.Status switch
                {
                    // прерван без result — возвращаем в рабочее состояние
                    SessionStatus.Working or SessionStatus.Waiting => SessionStatus.Active,
                    // ход завершился штатно (result уже перевёл в Active) — фиксируем Finished
                    SessionStatus.Active => SessionStatus.Finished,
                    _ => null,
                };

            // Конец хода/обрыв — ожидающей карточки больше нет
            if (msg is ResultMessage or ErrorMessage or ExitedMessage)
                entry.PendingInteraction = null;

            if (newStatus.HasValue)
            {
                // P12/P15: отметим момент, когда статус стал Active — sweep в SaveSessions по этой
                // метке доведёт до Finished, если exited прогона не придёт (доживающий прогон при
                // живых фоновых агентах до BgLingerTimeout, подавленный SuppressExited, висящий без
                // работы процесс). Любой иной статус (Working/Waiting — идёт ход/карточка; Finished/
                // Error — терминальный) — «завершённого Active» нет, метку гасим. Под PendingLock:
                // struct DateTimeOffset? читает sweep под тем же локом; lock короткий, без await.
                lock (entry.PendingLock)
                    entry.LastTurnEndedAt = newStatus == SessionStatus.Active ? DateTimeOffset.UtcNow : null;
                // exited — та же доводка, что у sweep: содержимое хода пришло раньше
                // (result/сообщения), а обрыв без result его не добавляет
                await ApplyStatusAsync(sessionId, entry, newStatus.Value,
                    touchUpdatedAt: msg is not ExitedMessage);
            }

            // Цикл «до готово»: решение о продолжении — по result/error хода, нёсшего
            // протокол цикла (LoopTurnInFlight). Раньше триггером был exited, но после
            // механики доживания exited приходит лишь со смертью прогона — цикл замирал
            // на десятки минут при живых фоновых агентах.
            // В фоне, чтобы не блокировать read-loop адаптера пересозданием процесса.
            // Режим «Командная реализация»: ход штаба закончился — разбираем его маркеры
            // (эскалация, новая работа) и переводим завершённую проверку в ожидание вводной.
            // В фоне по той же причине, что и цикл «до готово»: публикация карточки,
            // уведомление и планирование не должны держать read-loop адаптера.
            // Буфер стрижки маркеров потребляем в ЛЮБОМ чате (он и копится везде): ход
            // закончился — ждать больше нечего, и то, что живая трансляция придерживала как
            // «вдруг это начало маркера» (TrimAmbiguousMarkerTail), дальше не дорастёт ни во
            // что — довешиваем как обычный текст и обнуляем буфер под следующий ход.
            var turnText = "";
            var turnAsked = false;
            if (msg is ResultMessage or ErrorMessage)
            {
                string? catchUpDelta;
                lock (entry.TeamTurnLock)
                {
                    turnText = entry.TeamTurnText.ToString();
                    var finalSafe = TeamProtocolMarkers.StripTeamProtocolMarkers(turnText);
                    // Тот же гард, что в живой трансляции: ход, ответивший ровно маркером,
                    // не должен догнать ленту пробелами вокруг вырезанного маркера.
                    catchUpDelta = finalSafe.Length > entry.TeamTurnShownLength && finalSafe.Trim().Length > 0
                        ? finalSafe[entry.TeamTurnShownLength..] : null;
                    entry.TeamTurnShownLength = 0;
                    entry.TeamTurnText.Clear();
                    entry.TurnSawAngleBracket = false;
                    turnAsked = entry.TeamTurnAsked;
                    entry.TeamTurnAsked = false;
                    // Этап 4 / шаг 1в: текст и план вызова HandleTeamTurnEndAsync кладутся в LastTeamTurnEnds
                    // ниже отдельным вызовом RecordTeamTurnEnd (эта функция сама берёт
                    // TeamTurnLock через реентрантность lock()). Здесь — только осушение буфера.
                }
                if (!string.IsNullOrEmpty(catchUpDelta))
                    await BroadcastAsync(sessionId, new TextDeltaMessage(catchUpDelta));
            }

            if (msg is ResultMessage or ErrorMessage && entry.Info.TeamImplement is not null)
            {
                // Этап 4 / шаг 1в: кладём план вызова HandleTeamTurnEndAsync в LastTeamTurnEnds.
                // Подписчик turn/completed изымает план и асинхронно зовёт HandleTeamTurnEndAsync
                // ровно один раз на ход. Дедуп SkipNextTeamTurnEnd, стоявший здесь прежде, ушёл
                // вместе с переключением: первая запись по ключу TurnSeq выигрывает (двойной
                // терминал ErrorMessage{ExpectResultFollows=true} + ResultMessage того же хода
                // даёт один план, второй no-op), а шина гарантирует единственность публикации
                // turn/completed на ход — на этом держится защита от потери маркера.
                var teamTurnFailed = msg is ErrorMessage or ResultMessage { Subtype: "error" };
                entry.RecordTeamTurnEnd(entry.LastTurnSeq, turnText, teamTurnFailed, turnAsked);
            }

            // Сабагент этого хода оборвался на середине — добиваем его продолжением. Строго по
            // result (ход в процесс уже не идёт): systemDirective-отправка очередь не проходит
            // и запустила бы второй ход параллельно живому.
            //
            // ПОРЯДОК ВАЖЕН: этот блок стоит ПЕРЕД разбором цикла «до готово» ниже. Цикл поднимает
            // следующий ход из Task.Run, и пометка об обрыве (TruncatedBgNote) обязана лечь раньше —
            // иначе она опаздывает ровно на один ход, а на последней итерации цикла теряется совсем.
            // Перенос сделан поведенчески нейтральным: раньше блок стоял ПОСЛЕ сброса
            // LoopTurnInFlight и потому всегда видел флаг ложным — здесь он ещё взведён, поэтому
            // в ShouldNudgeSubagent идёт «ход цикла реально продолжится» (флаг И живой WorkLoop),
            // а не сырой флаг. Иначе снятый посреди хода цикл (WorkLoop=null при взведённом флаге)
            // потерял бы добивание, которое получал до переноса.
            // Ошибочный ход добивать нечем —
            // сначала разбирается ошибка, поэтому ErrorMessage только гасит отметку.
            if (msg is ResultMessage or ErrorMessage && entry.TruncatedSubagent is { } cutAgent)
            {
                entry.TruncatedSubagent = null;
                // Оборвался другой агент — у него своя серия попыток (счётчик per-agentId)
                if (StartsNudgeSeries(entry.NudgeAgentId, cutAgent.AgentId)) entry.SubagentNudges = 0;
                if (msg is ResultMessage && ShouldNudgeSubagent(entry.SubagentNudges,
                        entry.Info.WorkLoop is not null, entry.Info.TeamImplement is not null,
                        HasPending(entry), entry.LoopTurnInFlight && entry.Info.WorkLoop is not null,
                        isInterruptedRun: cutAgent.FinishedBy == "interrupted"))
                {
                    entry.NudgeAgentId = cutAgent.AgentId;
                    var attempt = ++entry.SubagentNudges;
                    _ = Task.Run(async () =>
                    {
                        try { await NudgeTruncatedSubagentAsync(sessionId, cutAgent, attempt); }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[SessionManager] Добивание сабагента ({sessionId}): {ex.Message}");
                        }
                    });
                }
                else
                {
                    // Добивать не стали (уступили циклу «до готово»/штабу, очередь непуста, потолок
                    // исчерпан, ход ошибочный) — но МОЛЧАТЬ об обрыве нельзя: координатор уже принял
                    // последнюю реплику агента за его итог. Раньше отметка здесь просто гасилась, и в
                    // чате с активным циклом обрыв не оставлял следа вообще — ни добивания, ни
                    // предупреждения (инцидент 25.08.2026, чат «Зависание чатов практики»: обрыв
                    // заметил сам координатор, потому что обрывок случайно не был похож на отчёт).
                    // Пометка уедет префиксом ближайшего хода — а его цикл и штаб поднимут сами,
                    // это и есть их «свой протокол продолжения», ради которого им уступает добивание.
                    entry.TruncatedBgNote = cutAgent;
                }
            }

            if (msg is ResultMessage or ErrorMessage && entry.LoopTurnInFlight)
            {
                entry.LoopTurnInFlight = false;
                if (entry.Info.WorkLoop is not null)
                    _ = Task.Run(async () =>
                    {
                        try { await ContinueWorkLoopAsync(sessionId); }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[SessionManager] Цикл «до готово» ({sessionId}): {ex.Message}");
                        }
                    });
            }

            // Ход закончился — выпускаем следующее сообщение из очереди (и агентские chats_send,
            // и пользовательские «честной очереди»). По одному за раз: следующее уйдёт по result
            // уже этого хода. Цикл «до готово» приоритетнее агентских — он продолжает работу,
            // начатую человеком, и его директива уже поставлена выше; замороженная «Стоп»
            // очередь Drain'ом не трогается (проверка внутри). Ход, прерванный ради очереди
            // (enqueue + interrupt в SendMessageAsync/SendMessageAndWaitAsync), result не шлёт —
            // процесс убит: его очередь разбирает exited ТОГО ЖЕ прогона (DrainOnExitedRun).
            // Привязка к прогону обязательна: exited доживающего чужого прогона приходит с
            // опозданием до ~30 мин и увёл бы сообщение в умирающий от interrupt адаптер.
            // На штатном конце хода метка гасится — иначе поздний exited этого же прогона
            // доставил бы второе сообщение параллельно уже запущенному ходу. В фоне — как и
            // work-loop, чтобы не держать read-loop адаптера.
            // Компакт-ход кончился (штатно или обрывом) — метка своё отработала. Гейт по
            // прогону обязателен, как у DrainOnExitedRun ниже: exited чужого доживающего
            // прогона (опаздывает до ~30 мин) иначе снял бы защиту с ЖИВОЙ компакции, и
            // PreemptForPending убил бы её — ровно тот вечный Working, ради которого метка и есть.
            if (msg is ResultMessage or ErrorMessage or ExitedMessage && runId != 0 && entry.CompactRun == runId)
                entry.CompactRun = 0;

            var drainOnExited = msg is ExitedMessage && runId != 0 && entry.DrainOnExitedRun == runId;
            if (msg is ResultMessage or ErrorMessage or ExitedMessage && entry.DrainOnExitedRun == runId)
                entry.DrainOnExitedRun = 0;
            // Прогон умер без result (кнопка «Стоп», смерть процесса, ватчдог), а очередь
            // непуста. Раньше эту дыру закрывала метка: её ставила КАЖДАЯ постановка
            // пользовательского сообщения, потому что она же и убивала ход. Теперь обычная
            // отправка ход не трогает, и без разбора здесь сообщение висело бы призраком до
            // следующей отправки (ходовой случай: «Стоп» → сразу отправить исправленный текст,
            // пока exited убитого хода ещё в пути). Гейты: свой прогон (поздний exited чужого
            // доживающего очередь не трогает), у адаптера нет живого прогона CLI и очередь не
            // заморожена — заморозку «Стоп» снимает только возобновление пользователем.
            //
            // Смотрим ИМЕННО HasLiveTurn, не статус и не Busy — оба здесь ложны:
            //  • статус exited уже сбросил Working→Active выше по методу (ApplyStatusAsync);
            //  • Busy включает OrchestrationActive, а боевой адаптер — всегда обёртка
            //    FallbackLlmSessionAdapter, которая отдаёт придержанный exited вниз из
            //    SettleAsync и обнуляет _turn лишь в finally ПОСЛЕ нас: гейт по Busy не
            //    срабатывал бы никогда (найдено на ревью).
            // ClaudeSession обнуляет _run ДО отправки exited, поэтому у мёртвого прогона
            // HasLiveTurn=false, а при уже запущенном следующем ходе — true (второй ход
            // параллельно не уйдёт). Если разбор всё же попадёт в окно живой оркестрации,
            // доставка не потеряется: SendMessageAsync обёртки вернёт ход в Pending через
            // EnqueueBypass, а её finally добьёт разбор сигналом _orchestrationDone. Известное
            // ограничение этой страховки: EnqueueBypassTurn кладёт ход обратно как агентский и
            // теряет Mode — то есть пользовательское сообщение, попавшее в это редкое окно,
            // доедет пузырём «Автоматически» и без своего режима.
            // Дубль с разбором по метке безопасен: DrainInFlight пропустит второй заход.
            var drainOnDeadRun = msg is ExitedMessage && runId != 0 && runId == entry.RunId
                && !entry.QueueFrozen && entry.Process is not { HasLiveTurn: true } && HasPending(entry);
            if (drainOnExited || drainOnDeadRun
                || (msg is ResultMessage or ErrorMessage && !entry.LoopTurnInFlight
                    && (entry.Info.WorkLoop is null || HasContinuingPending(entry))))
            {
                _ = Task.Run(async () =>
                {
                    try { await DrainNextPendingAsync(sessionId); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[SessionManager] Разбор очереди сообщений ({sessionId}): {ex.Message}");
                    }
                });
            }
        }

        if (sendBroadcast) await BroadcastAsync(sessionId, msg);

        if (entry is not null && OnSessionMessage is { } observers)
        {
            // Multicast Func<> await-ит только Task ПОСЛЕДНЕГО подписчика — ждём всех явно,
            // иначе исключения и незавершённая работа не-последних наблюдателей теряются
            foreach (var observer in observers.GetInvocationList().Cast<Func<Session, ServerMessage, Task>>())
            {
                try { await observer(entry.Info, msg); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SessionManager] Ошибка наблюдателя сессии ({sessionId}): {ex.Message}");
                }
            }
        }
    }

    // Ставит результат генерации fal.ai на отслеживание стоимости (опрос billing-events — в фоне).
    private void TryTrackFalCost(string sessionId, string content)
    {
        if (!_falCost.Enabled) return;
        var requestId = SpendMapping.TryExtractFalRequestId(content);
        if (!string.IsNullOrEmpty(requestId))
            _falCost.Track(sessionId, requestId);
    }

    // Догоняет стоимость для СТАРЫХ генераций fal.ai в истории, у которых ещё нет fal_cost
    // (сгенерированы до появления фичи/ключа). Вызывается при загрузке истории сессии.
    private void BackfillFalCosts(string sessionId, IReadOnlyList<StoredMessage> history)
    {
        if (!_falCost.Enabled) return;
        var have = new HashSet<string>();
        foreach (var m in history)
            if (m is StoredFalCostMessage f) have.Add(f.RequestId);
        foreach (var m in history)
        {
            if (m is not StoredToolUseMessage t || t.IsError || string.IsNullOrEmpty(t.Result)) continue;
            var rid = SpendMapping.TryExtractFalRequestId(t.Result);
            if (rid != null && !have.Contains(rid))
                _falCost.Track(sessionId, rid);
        }
    }

    // Детектит завершённую glif-генерацию прямо в tool_result: кредиты уже есть в _meta.glif,
    // поэтому публикуем сообщение синхронно, без фонового опроса как у fal.
    private void TryTrackGlifCost(string sessionId, string content)
    {
        if (_glif?.Enabled != true) return;
        var msg = GlifCostParser.TryParse(content);
        if (msg is not null)
            _ = PublishGlifCostAsync(sessionId, msg);
    }

    // Догоняет учёт для СТАРЫХ glif-генераций в истории, у которых ещё нет glif_cost.
    // Вызывается при загрузке истории сессии.
    private void BackfillGlifCosts(string sessionId, IReadOnlyList<StoredMessage> history)
    {
        if (_glif?.Enabled != true) return;
        var have = new HashSet<string>();
        foreach (var m in history)
            if (m is StoredGlifCostMessage g) have.Add(g.JobId);
        foreach (var m in history)
        {
            if (m is not StoredToolUseMessage t || t.IsError || string.IsNullOrEmpty(t.Result)) continue;
            var msg = GlifCostParser.TryParse(t.Result);
            if (msg is not null && !have.Contains(msg.JobId))
                _ = PublishGlifCostAsync(sessionId, msg);
        }
    }

    // Публикация учёта glif-генерации: запись в историю (дедуп) + broadcast клиентам.
    // Зеркало PublishFalCostAsync: активная сессия → аккумулятор; неактивная → прямо на диск.
    public async Task PublishGlifCostAsync(string sessionId, GlifCostMessage msg)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;

        // Лок держим РОВНО один раз на весь путь «проверить entry.Accumulator → выбрать
        // ветку → записать»: иначе EnsureAccumulatorAsync под тем же локом успевает
        // прочитать историю до нашей записи, создать аккумулятор со старым снимком и
        // следующий SaveSnapshotAsync затирает нашу запись. SemaphoreSlim не реентерабелен,
        // поэтому AppendIfNotDuplicateStoredNoLockAsync внутри WithFalPersistLockAsync
        // НЕ берёт лок повторно (контракт — caller holds).
        var result = await WithFalPersistLockAsync(async () =>
        {
            if (entry.Accumulator is not null)
            {
                if (!entry.Accumulator.OnGlifCost(msg.JobId, msg.OutputType, msg.MediaCount, msg.Credits, msg.Model))
                    return AppendResult.Duplicate;
                try { await entry.Accumulator.SaveSnapshotAsync(_history); }
                catch (Exception ex) { Console.Error.WriteLine($"[GlifCost] Сохранение истории ({sessionId}) не удалось: {ex.Message}"); }
                return AppendResult.Added;
            }
            else
            {
                try
                {
                    return await AppendIfNotDuplicateStoredNoLockAsync(entry,
                        m => m is StoredGlifCostMessage g && g.JobId == msg.JobId,
                        () => new StoredGlifCostMessage(msg.JobId, msg.OutputType, msg.MediaCount, msg.Credits, msg.Model));
                }
                catch (Exception ex)
                {
                    // Дисковая запись не удалась — но аналитику и broadcast обязаны пройти
                    // (зеркало поведения PublishFalCostAsync до правки): карточка стоимости
                    // должна появиться у пользователя даже при сбое истории, иначе при рестарте
                    // она пропадёт совсем.
                    Console.Error.WriteLine($"[GlifCost] Прямая запись истории ({sessionId}) не удалась: {ex.Message}");
                    return AppendResult.Added;
                }
            }
        });

        if (result == AppendResult.Duplicate) return;

        // Аналитика: генерация glif — счётчик операций, кредиты про запас, стоимость USD неизвестна.
        SpendMapping.RecordGlifGeneration(_spend, ResolveOwnerId, _log, entry.Info, msg);

        await BroadcastAsync(sessionId, msg);
    }

    // Публикация найденной стоимости fal.ai: запись в историю (дедуп) + broadcast клиентам.
    // Активная сессия → через аккумулятор; неактивная (нет аккумулятора) → прямо в файл истории,
    // иначе стоимость не переживёт переоткрытие и «считается…» зависнет.
    public async Task PublishFalCostAsync(string sessionId, FalCostMessage msg)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;

        // Лок держим РОВНО один раз на весь путь «проверить entry.Accumulator → выбрать
        // ветку → записать»: иначе EnsureAccumulatorAsync под тем же локом успевает
        // прочитать историю до нашей записи, создать аккумулятор со старым снимком и
        // следующий SaveSnapshotAsync затирает нашу запись. SemaphoreSlim не реентерабелен,
        // поэтому AppendIfNotDuplicateStoredNoLockAsync внутри WithFalPersistLockAsync
        // НЕ берёт лок повторно (контракт — caller holds).
        var result = await WithFalPersistLockAsync(async () =>
        {
            if (entry.Accumulator is not null)
            {
                if (!entry.Accumulator.OnFalCost(msg.RequestId, msg.EndpointId, msg.CostUsd, msg.OutputUnits, msg.UnitPrice))
                    return AppendResult.Duplicate;
                try { await entry.Accumulator.SaveSnapshotAsync(_history); }
                catch (Exception ex) { Console.Error.WriteLine($"[FalCost] Сохранение истории ({sessionId}) не удалось: {ex.Message}"); }
                return AppendResult.Added;
            }
            else
            {
                // Сессия не активна — пишем стоимость напрямую в историю на диске. Дедуп
                // по RequestId и проверка наличия ClaudeSessionId идут в
                // AppendIfNotDuplicateStoredNoLockAsync.
                try
                {
                    return await AppendIfNotDuplicateStoredNoLockAsync(entry,
                        m => m is StoredFalCostMessage f && f.RequestId == msg.RequestId,
                        () => new StoredFalCostMessage(msg.RequestId, msg.EndpointId, msg.CostUsd, msg.OutputUnits, msg.UnitPrice));
                }
                catch (Exception ex)
                {
                    // Дисковая запись не удалась — но аналитику и broadcast обязаны пройти:
                    // карточка стоимости должна появиться у пользователя даже при сбое истории,
                    // иначе при рестарте она пропадёт совсем (нет ни AppendIfNotDuplicate, ни
                    // аккумулятора).
                    Console.Error.WriteLine($"[FalCost] Прямая запись истории ({sessionId}) не удалась: {ex.Message}");
                    return AppendResult.Added;
                }
            }
        });

        if (result == AppendResult.Duplicate) return; // дубль — не ретранслируем

        // Аналитика расхода: генерация fal.ai — счётчик операций (токенов у fal нет),
        // фактическая стоимость про запас. Дедуп выше гарантирует одну запись на request_id.
        SpendMapping.RecordFalGeneration(_spend, ResolveOwnerId, _log, entry.Info, msg);

        await BroadcastAsync(sessionId, msg);
    }

    // Запись расхода штатного хода в аналитику — вынесена в SpendMapping.RecordTurnSpend
    // (этап 4, волна 1 «приём хода», 2026-09-07): код спины, использующий обе стороны
    // (ISpendCollector подсистемы Spend и LlmProviderRegistry слоя Llm), без состояния,
    // держать его в ядре SessionManager было лишним весом.

    // Запись StoredMessage в историю сессии ВНЕ хода + broadcast (обобщение паттерна
    // PublishFalCostAsync): активная сессия → через Accumulator + SaveSnapshot;
    // неактивная → LoadAsync + append + SaveAsync под локом. Используется совещаниями.
    public async Task AppendStoredAsync(string sessionId, StoredMessage stored, ServerMessage broadcast)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;

        // Как в PublishFalCostAsync: лок держим РОВНО один раз на «проверить entry.Accumulator
        // → выбрать ветку → записать», иначе EnsureAccumulatorAsync вклинится между
        // выбором дисковой ветки и записью и следующий SaveSnapshotAsync затрёт нашу запись.
        await WithFalPersistLockAsync(async () =>
        {
            if (entry.Accumulator is { } acc)
            {
                acc.Append(stored);
                try { await acc.SaveSnapshotAsync(_history); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SessionManager] Сохранение истории ({sessionId}) после внеходовой записи: {ex.Message}");
                }
            }
            else
            {
                try
                {
                    // Дедуп предикат «никогда» — совещания/конвейеры сами следят за
                    // уникальностью по своим ключам; AppendResult тут не интересует,
                    // broadcast всё равно отправим.
                    await AppendIfNotDuplicateStoredNoLockAsync(entry, _ => false, () => stored);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[SessionManager] Прямая внеходовая запись истории ({sessionId}): {ex.Message}");
                }
            }
        });

        await BroadcastSessionMessageAsync(sessionId, broadcast);
    }

    // Единая точка перехода статуса сессии: обновить Info → сохранить на диск → разослать клиентам.
    //
    // touchUpdatedAt = false у ДОВОДКИ статуса — перехода, за которым не стоит нового
    // содержимого: exited прогона и sweep-терминус Active→Finished приходят уже после ответа
    // (sweep — спустя grace, на ближайшем SaveSessions, то есть до минуты). UpdatedAt несёт
    // непрочитанность (updatedAt > lastReadAt), поэтому такая доводка метила прочитанный чат
    // непрочитанным заново: точка на кнопке проекта и стены гасла при открытии чата и через
    // полминуты возвращалась (инцидент 06.09.2026). Новое содержимое отмечают переходы, за
    // которыми оно есть: Working (ход пошёл), Active по result (ответ готов), Waiting
    // (карточка ждёт человека), Error.
    //
    // Архивный чат сменой статуса из архива не выводим вовсе (как RetitleAsync/UpdateAsync):
    // признак архива производный (UpdatedAt <= ArchivedAt), и любая отметка возвращала бы его.
    // Настоящая активность архива не теряет: отметку ставит приём сообщения (адаптер
    // SendMessageAsync, локальный голосовой ход — SendDirectAsync), и до смены статуса чат
    // уже не архивный.
    private async Task ApplyStatusAsync(string sessionId, SessionEntry entry, SessionStatus status,
        bool touchUpdatedAt = true)
    {
        entry.Info.Status = status;
        if (touchUpdatedAt && !entry.Info.IsArchived) entry.Info.UpdatedAt = DateTime.UtcNow;
        SaveSessions();
        await BroadcastStatusChangeAsync(sessionId, entry.Info,
            status, entry.Info.LastMessage, entry.Info.MessageCount);
    }

    // Текст последней реплики ассистента текущего хода (сообщения после baseline) —
    // ответ для синхронного ожидателя SendMessageAndWaitAsync
    private static string LastAssistantText(TurnAccumulator acc, int baseline) =>
        acc.GetAll().Skip(Math.Max(0, baseline)).OfType<StoredTextMessage>()
            .LastOrDefault(t => t.ParentToolUseId is null)?.Text ?? "";

    // Для fire-and-forget задач: ошибку логируем, а не теряем молча
    private static void FireAndForget(Task task, string context) =>
        task.ContinueWith(
            t => Console.Error.WriteLine($"[SessionManager] {context}: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);

    // Остановка всех живых адаптеров — вызывается при graceful shutdown приложения,
    // иначе после остановки сервера остаются зомби-процессы (claude + node MCP-серверов)
    public void KillAllProcesses()
    {
        // Таймер не должен пытаться писать одновременно с убийством процессов:
        // сценарий — shutdown, SaveSessions() ждёт _saveLock, а в это время адаптеры
        // claude не диспозятся → процессы зависают в памяти (боролись ранее). Тот же
        // сценарий — для тика ожидания: в окне shutdown он бы мог послать директиву
        // в умирающий адаптер, и SendMessageAsync не нашёл бы Process.
        _autoSaveTimer?.Dispose();
        _waitingTickTimer?.Dispose();

        var tasks = _sessions.Values
            .Select(e => e.Process)
            .OfType<ILlmSessionAdapter>()
            .Select(p => p.DisposeAsync().AsTask())
            .ToArray();
        if (tasks.Length == 0) return;
        try { Task.WaitAll(tasks, TimeSpan.FromSeconds(15)); }
        catch (AggregateException ex)
        {
            Console.Error.WriteLine($"[SessionManager] Остановка процессов при завершении: {ex.GetBaseException().Message}");
        }
    }

    // IDisposable — для фоновых таймеров. Адаптеры (процессы claude) убивает
    // KillAllProcesses() из ApplicationStopping. Не дублируем — иначе два cleanup-пути.
    public void Dispose()
    {
        _autoSaveTimer?.Dispose();
        _waitingTickTimer?.Dispose();
        GC.SuppressFinalize(this);
    }

    // Рассылка в session-группу. Внутренний канал штабных обновлений вертикали Services.Team:
    // TeamStateService.BroadcastTeamImplementAsync идёт через этот метод, чтобы не плодить
    // параллельный IHubContext<SessionHub> внутри вертикали (см. комментарий TeamCoordinator).
    // Снаружи (из контроллеров/хаба) используется публичный BroadcastSessionMessageAsync,
    // который дополнительно вещает в project_/user_-группу — для чат-карточек в списке.
    internal Task BroadcastAsync(string sessionId, ServerMessage msg) =>
        _broadcaster.ToSession(sessionId, msg with { SessionId = sessionId });

    // Рассылка в session-группу КРОМЕ соединения-отправителя — ручной ввод пользователя:
    // отправитель уже нарисовал баллон оптимистично (см. точку рассылки в SendDirectAsync),
    // эхо продублировало бы реплику в его ленте. Тот же проставщик SessionId.
    internal Task BroadcastExceptAsync(string sessionId, string exceptConnectionId, ServerMessage msg) =>
        _broadcaster.ToSessionExcept(sessionId, exceptConnectionId, msg with { SessionId = sessionId });

    // Публильный broadcast внеходового сообщения сессии: session-группа + project_/user_-группа
    // (по образцу BroadcastStatusChangeAsync). Используется роутингом группового чата и совещаниями.
    public async Task BroadcastSessionMessageAsync(string sessionId, ServerMessage msg)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry)) return;
        var wired = msg with { SessionId = sessionId };
        var tasks = new List<Task> { _broadcaster.ToSession(sessionId, wired) };
        if (entry.Info.ProjectId is string pid)
            tasks.Add(_broadcaster.ToProject(pid, wired));
        else if (entry.Info.OwnerId is string oid)
            tasks.Add(_broadcaster.ToOwner(oid, wired));
        await Task.WhenAll(tasks);
    }

    // Сессия, принадлежащая пользователю (и проектная, и чат вне проекта) — для
    // эндпоинтов, работающих с любым типом сессии (участники группы, совещания).
    public Session? GetOwned(string sessionId, string ownerId)
    {
        var s = GetById(sessionId);
        return s is not null && ResolveOwnerId(s) == ownerId ? s : null;
    }

    // Рассылаем в session-группу (сам чат) всегда, плюс в project-группу (все вкладки проекта)
    // для проектной сессии ЛИБО в user-группу (список чатов) для чата вне проекта —
    // чтобы клиент не пропустил обновление, если не успел войти в session-группу.
    private async Task BroadcastStatusChangeAsync(string sessionId, Session info, SessionStatus status,
        string? lastMessage = null, int messageCount = 0)
    {
        var statusMsg = new StatusChangedMessage(status.ToString().ToLower(), lastMessage, messageCount)
            with { SessionId = sessionId };
        var tasks = new List<Task> { _broadcaster.ToSession(sessionId, statusMsg) };
        if (info.ProjectId is string pid)
            tasks.Add(_broadcaster.ToProject(pid, statusMsg));
        else if (info.OwnerId is string oid)
            tasks.Add(_broadcaster.ToOwner(oid, statusMsg));
        await Task.WhenAll(tasks);
    }
}

// Итог завершённого хода для синхронного ожидания (SendMessageAndWaitAsync):
// текст последней реплики ассистента, длительность и стоимость (если провайдер её отдал)
public record TurnResult(string Reply, long DurationMs, double? CostUsd);

// Результат отправки с ожиданием: Queued — сессия была занята, сообщение встало в очередь
// и уйдёт после текущего хода; QueueFull — очередь переполнена, сообщение отброшено;
// Completed — ход завершился в срок; Running — ход продолжается (wait=none или истёк таймаут).
// Busy остаётся для совместимости сигнатуры, но занятость сама по себе больше не отказ.
public abstract record SendAndWaitResult
{
    public sealed record Busy(SessionStatus CurrentStatus) : SendAndWaitResult;
    // Dispatched — постановка сама форсировала доставку (очередь была пуста, а ход успел
    // кончиться): вызывающему прерывать нечего, ход уже идёт с этим сообщением
    public sealed record Queued(int Position, bool Duplicate, bool Dispatched = false) : SendAndWaitResult;
    public sealed record QueueFull(int Limit) : SendAndWaitResult;
    public sealed record Completed(TurnResult Result) : SendAndWaitResult;
    public sealed record Running : SendAndWaitResult;


}

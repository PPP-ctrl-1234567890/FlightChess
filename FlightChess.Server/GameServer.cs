using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FlightChess.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlightChess.Server
{
    /// <summary>
    /// 飞行棋游戏服务器：接受连接、处理消息、维护游戏状态并广播更新。
    /// </summary>
    public class GameServer
    {
        private TcpListener _listener;
        private Thread _acceptThread;
        private bool _isRunning;

        private readonly object _clientsLock = new object();
        private List<ClientConnection> _clients;

        private readonly object _gameStateLock = new object();
        private GameState _gameState;

        private readonly object _logLock = new object();
        private List<string> _logMessages;

        private FlightChessEngine _engine;

        // AI 托管
        private System.Timers.Timer _aiTimer;
        private Random _aiRng;

        // 心跳检测
        private System.Timers.Timer _heartbeatTimer;

        // 重连宽限期
        private Dictionary<int, DateTime> _reconnectGracePeriods;
        private System.Timers.Timer _graceCheckTimer;

        public GameServer()
        {
            _clients = new List<ClientConnection>();
            _engine = new FlightChessEngine();
            _logMessages = new List<string>();
            _gameState = new GameState();
            _aiRng = new Random();
            _reconnectGracePeriods = new Dictionary<int, DateTime>();
        }

        /// <summary>启动服务器，开始监听指定端口。</summary>
        public void Start(int port = 8888)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _isRunning = true;

            Console.WriteLine("╔══════════════════════════╗");
            Console.WriteLine("║  飞行棋联机游戏服务器    ║");
            Console.WriteLine("║  端口: {0,-16} ║", port);
            Console.WriteLine("╚══════════════════════════╝");

            _acceptThread = new Thread(AcceptClientsLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Start();

            // 启动心跳检测定时器（每 5 秒发送 Ping 并检查超时）
            _heartbeatTimer = new System.Timers.Timer(5000);
            _heartbeatTimer.AutoReset = true;
            _heartbeatTimer.Elapsed += HeartbeatTimer_Elapsed;
            _heartbeatTimer.Start();

            // 启动重连宽限期检查定时器（每 5 秒检查一次过期情况）
            _graceCheckTimer = new System.Timers.Timer(5000);
            _graceCheckTimer.AutoReset = true;
            _graceCheckTimer.Elapsed += GraceCheckTimer_Elapsed;
            _graceCheckTimer.Start();
        }

        /// <summary>停止服务器，释放所有资源。</summary>
        public void Stop()
        {
            _isRunning = false;
            StopAI();
            try { _aiTimer?.Stop(); _aiTimer?.Dispose(); } catch { }
            try { _heartbeatTimer?.Stop(); _heartbeatTimer?.Dispose(); } catch { }
            try { _graceCheckTimer?.Stop(); _graceCheckTimer?.Dispose(); } catch { }
            try { _listener?.Stop(); } catch { }

            lock (_clientsLock)
            {
                foreach (var client in _clients)
                {
                    client.Stop();
                }
                _clients.Clear();
            }
        }

        /// <summary>接受客户端连接循环，玩家 ID 由 JoinGame/Reconnect 消息分配。</summary>
        private void AcceptClientsLoop()
        {
            while (_isRunning)
            {
                try
                {
                    TcpClient tcpClient = _listener.AcceptTcpClient();

                    // 创建连接，PlayerId = -1 表示尚未分配
                    ClientConnection conn = new ClientConnection(tcpClient, this);
                    conn.PlayerId = -1;

                    lock (_clientsLock)
                    {
                        _clients.Add(conn);
                    }

                    conn.Start();
                    Console.WriteLine("[+] {0} → 新连接，等待加入/重连",
                        tcpClient.Client.RemoteEndPoint);
                }
                catch (SocketException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[!] 接受连接出错: {0}", ex.Message);
                }
            }
        }

        /// <summary>
        /// 处理客户端消息
        /// </summary>
        public void ProcessMessage(ClientConnection sender, string json)
        {
            try
            {
                JObject obj = JObject.Parse(json);
                string msgType = obj["Type"]?.Value<string>();

                if (string.IsNullOrEmpty(msgType))
                {
                    SendError(sender, "消息类型不能为空。");
                    return;
                }

                switch (msgType)
                {
                    case MessageType.JoinGame:
                        HandleJoinGame(sender, json);
                        break;
                    case MessageType.RollDice:
                        HandleRollDice(sender);
                        break;
                    case MessageType.MovePiece:
                        HandleMovePiece(sender, json);
                        break;
                    case MessageType.ResetGame:
                        HandleResetGame(sender);
                        break;
                    case MessageType.Chat:
                        HandleChatMessage(sender, json);
                        break;
                    case MessageType.Ping:
                        // 客户端发来的 Ping → 回复 Pong
                        sender.SendMessage(new PongMessage());
                        sender.LastActivityTime = DateTime.UtcNow;
                        break;
                    case MessageType.Pong:
                        // 心跳回复 — LastActivityTime 已在 ReceiveLoop 中更新
                        break;
                    case MessageType.Reconnect:
                        HandleReconnect(sender, json);
                        break;
                    default:
                        SendError(sender, "未知消息类型: " + msgType);
                        break;
                }
            }
            catch (JsonException ex)
            {
                SendError(sender, "JSON 解析错误: " + ex.Message);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] 消息处理错误: {0}", ex.Message);
                SendError(sender, "服务器内部错误: " + ex.Message);
            }
        }

        /// <summary>
        /// 处理加入游戏请求。扫描空位与标记占用在同一锁内完成，消除 TOCTOU 竞态。
        /// </summary>
        private void HandleJoinGame(ClientConnection sender, string json)
        {
            if (sender.IsInitialized || sender.PlayerId >= 0)
            {
                SendError(sender, "你已经加入游戏了。");
                return;
            }

            JoinGameMessage msg = JsonConvert.DeserializeObject<JoinGameMessage>(json);
            string playerName = msg.PlayerName;

            // 步骤1：在 _gameStateLock 下扫描空位并立即标记占用
            int assignedId = -1;
            bool isReconnect = false;
            bool caseBReconnect = false;  // true=同名玩家仍显示在线，需额外验证旧连接是否存活
            GameStateUpdateMessage broadcastMsg = null;

            lock (_gameStateLock)
            {
                // 第一遍：找同名玩家走重连
                for (int i = 0; i < 4; i++)
                {
                    var p = _gameState.Players[i];
                    if (!p.HasJoined || p.Name != playerName)
                        continue;

                    if (!p.IsConnected)
                    {
                        // 已断线 → 直接重连
                        assignedId = i;
                        isReconnect = true;
                        break;
                    }
                    if (p.IsConnected)
                    {
                        // 仍显示在线 → 标记待验证（防止同名抢占）
                        assignedId = i;
                        isReconnect = true;
                        caseBReconnect = true;
                        break;
                    }
                }

                // 第二遍：无匹配重连 → 找从未加入的空位
                if (!isReconnect)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        if (!_gameState.Players[i].HasJoined)
                        {
                            assignedId = i;
                            sender.PlayerId = i;
                            sender.IsInitialized = true;
                            var player = _gameState.Players[i];
                            if (string.IsNullOrWhiteSpace(playerName))
                                playerName = "Player" + (i + 1);
                            player.Name = playerName;
                            player.IsConnected = true;
                            player.HasJoined = true;
                            _gameState.ConnectedPlayerCount++;

                            AddLog(string.Format("{0} 加入了游戏。", playerName));
                            Console.WriteLine("[+] {0}: {1}", CN(i), playerName);
                            break;
                        }
                    }

                    if (assignedId >= 0)
                        broadcastMsg = CaptureGameStateUpdate();
                }
                else
                {
                    // 重连路径：恢复连接状态，停止 AI 托管
                    _reconnectGracePeriods.Remove(assignedId);
                    sender.PlayerId = assignedId;
                    sender.IsInitialized = true;
                    var player = _gameState.Players[assignedId];
                    player.IsConnected = true;
                    if (!string.IsNullOrWhiteSpace(playerName))
                        player.Name = playerName;

                    StopAI();

                    AddLog(string.Format("{0} 重新连接了游戏。", playerName));
                    Console.WriteLine("[+] {0}({1}) 重连成功", CN(assignedId), playerName);

                    broadcastMsg = CaptureGameStateUpdate();
                }
            }

            // 步骤2：caseB 校验 —— 确认旧连接确已断开
            if (caseBReconnect && assignedId >= 0)
            {
                ClientConnection oldConn = null;
                lock (_clientsLock)
                {
                    oldConn = _clients.Find(c =>
                        c != sender && c.PlayerId == assignedId && c.IsInitialized);
                }
                if (oldConn != null && oldConn.IsSocketConnected)
                {
                    // 旧连接仍存活 → 名字冲突
                    SendError(sender, string.Format(
                        "名称 \"{0}\" 已被使用，请更换名称后重试。", playerName));
                    sender.Stop();
                    lock (_clientsLock) { _clients.Remove(sender); }
                    return;
                }
            }

            // 步骤3：房间满 → 拒绝
            if (assignedId < 0)
            {
                SendError(sender, "房间已满，且没有匹配的断线玩家。");
                sender.Stop();
                lock (_clientsLock) { _clients.Remove(sender); }
                return;
            }

            // 步骤4：重连时清理旧连接（需排除 sender，避免误清自身）
            if (isReconnect)
            {
                ClientConnection oldConn = null;
                lock (_clientsLock)
                {
                    oldConn = _clients.Find(c => c != sender && c.PlayerId == assignedId);
                    if (oldConn != null)
                        _clients.Remove(oldConn);
                }
                if (oldConn != null)
                {
                    oldConn.PlayerId = -1;
                    oldConn.Stop();
                }
            }

            // 步骤5：发送响应并广播
            sender.SendMessage(new JoinGameResponseMessage { PlayerId = assignedId });
            if (broadcastMsg != null)
                BroadcastMessage(broadcastMsg);
            CheckAndTriggerAI();
        }

        /// <summary>
        /// 处理掷骰子请求。校验在锁内完成，错误在锁外发送；广播快照在锁内制备。
        /// </summary>
        private void HandleRollDice(ClientConnection sender)
        {
            if (!sender.IsInitialized)
            {
                SendError(sender, "请先加入游戏。");
                return;
            }

            string deferredError = null;
            bool noValidMoves = false;
            GameStateUpdateMessage broadcastMsg = null;
            int curPlayerId;

            lock (_gameStateLock)
            {
                curPlayerId = sender.PlayerId;

                if (_gameState.GameOver)
                    deferredError = "游戏已经结束。";
                else if (curPlayerId != _gameState.CurrentPlayerIndex)
                    deferredError = "还没轮到你。";
                else if (_gameState.DiceValue != 0)
                    deferredError = "你已经掷过骰子了，请先移动棋子。";

                if (deferredError == null)
                {
                    int diceValue = _engine.RollDice();
                    _gameState.DiceValue = diceValue;

                    string playerName = _gameState.Players[curPlayerId].Name;
                    AddLog(string.Format("{0} 掷出了 {1} 点。", playerName, diceValue));
                    Console.WriteLine("[掷] {0} → {1}", CN(curPlayerId), diceValue);

                    var validMoves = _engine.GetValidMoves(
                        _gameState.Players[curPlayerId], diceValue);

                    if (validMoves.Count == 0)
                    {
                        noValidMoves = true;
                        Console.WriteLine("[掷] {0} → {1}, 无棋可移→跳过", CN(curPlayerId), diceValue);
                        AddLog(string.Format("{0} 没有棋子可以移动，轮到下一家。", playerName));
                    }

                    broadcastMsg = CaptureGameStateUpdate();
                }
            }

            if (deferredError != null)
            {
                SendError(sender, deferredError);
                return;
            }

            if (broadcastMsg != null)
                BroadcastMessage(broadcastMsg);

            if (noValidMoves)
            {
                int capturedPlayer = curPlayerId;
                var skipTimer = new System.Timers.Timer(1500) { AutoReset = false };
                skipTimer.Elapsed += (s, ev) =>
                {
                    try
                    {
                        GameStateUpdateMessage skipMsg = null;
                        lock (_gameStateLock)
                        {
                            if (_gameState.CurrentPlayerIndex == capturedPlayer && !_gameState.GameOver)
                            {
                                _gameState.CurrentPlayerIndex = _engine.GetNextPlayerIndex(_gameState);
                                _gameState.DiceValue = 0;
                                skipMsg = CaptureGameStateUpdate();
                            }
                        }
                        if (skipMsg != null)
                            BroadcastMessage(skipMsg);
                        CheckAndTriggerAI();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[!] 自动跳过错误: {0}", ex.Message);
                    }
                    skipTimer.Dispose();
                };
                skipTimer.Start();
            }
        }

        /// <summary>处理移动棋子请求。校验在锁内完成，错误在锁外发送。</summary>
        private void HandleMovePiece(ClientConnection sender, string json)
        {
            if (!sender.IsInitialized)
            {
                SendError(sender, "请先加入游戏。");
                return;
            }

            MovePieceMessage msg = JsonConvert.DeserializeObject<MovePieceMessage>(json);

            string deferredError = null;
            GameStateUpdateMessage broadcastMsg = null;

            lock (_gameStateLock)
            {
                if (_gameState.GameOver)
                {
                    deferredError = "游戏已经结束。";
                }
                else if (sender.PlayerId != _gameState.CurrentPlayerIndex)
                {
                    deferredError = "还没轮到你。";
                }
                else if (_gameState.DiceValue == 0)
                {
                    deferredError = "请先掷骰子。";
                }

                if (deferredError == null)
                {
                    var validMoves = _engine.GetValidMoves(
                        _gameState.Players[sender.PlayerId], _gameState.DiceValue);

                    if (!validMoves.Contains(msg.PieceIndex))
                        deferredError = "该棋子当前无法移动。";
                }

                if (deferredError == null)
                {
                    MoveResult result = _engine.MovePiece(_gameState, sender.PlayerId, msg.PieceIndex);

                    if (!result.Success)
                    {
                        deferredError = result.Message;
                    }
                    else
                    {
                        string playerName = _gameState.Players[sender.PlayerId].Name;
                        string logMsg = result.Message;
                        AddLog(logMsg);
                        Console.WriteLine("[移] {0} 棋{1}: {2}", CN(sender.PlayerId),
                            msg.PieceIndex + 1, result.Message);

                        if (result.GameOver)
                        {
                            AddLog("所有玩家均已完成归营，游戏结束！");
                            Console.WriteLine("[!!] 游戏结束!");
                            _gameState.DiceValue = 0;
                        }
                        else if (result.ExtraTurn)
                        {
                            AddLog(string.Format("{0} 掷出了 6 点，获得额外回合！", playerName));
                            Console.WriteLine("[6] {0} 额外回合", CN(sender.PlayerId));
                            _gameState.DiceValue = 0;
                        }
                        else
                        {
                            _gameState.CurrentPlayerIndex = _engine.GetNextPlayerIndex(_gameState);
                            _gameState.DiceValue = 0;
                        }

                        broadcastMsg = CaptureGameStateUpdate();
                    }
                }
            }

            if (deferredError != null)
            {
                SendError(sender, deferredError);
                return;
            }

            if (broadcastMsg != null)
                BroadcastMessage(broadcastMsg);

            CheckAndTriggerAI();
        }

        /// <summary>处理重置游戏请求。重置所有棋子，保留玩家连接状态。</summary>
        private void HandleResetGame(ClientConnection sender)
        {
            string deferredError = null;
            GameStateUpdateMessage broadcastMsg = null;

            lock (_gameStateLock)
            {
                if (!_gameState.GameOver)
                {
                    deferredError = "游戏尚未结束，无法重置。";
                }
                else
                {
                    for (int i = 0; i < 4; i++)
                    {
                        bool wasConnected = _gameState.Players[i].IsConnected;
                        bool hadJoined = _gameState.Players[i].HasJoined;
                        string name = _gameState.Players[i].Name;
                        _gameState.Players[i] = new Common.Player(i, name,
                            FlightChessEngine.PlayerStartOffsets[i]);
                        _gameState.Players[i].IsConnected = wasConnected;
                        _gameState.Players[i].HasJoined = hadJoined;
                    }
                    _gameState.ConnectedPlayerCount = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        if (_gameState.Players[i].IsConnected)
                            _gameState.ConnectedPlayerCount++;
                    }
                    _gameState.CurrentPlayerIndex = 0;
                    _gameState.DiceValue = 0;
                    _gameState.WinnerIndex = -1;
                    _gameState.FinishCount = 0;

                    AddLog("游戏已重置，新一局开始！");
                    Console.WriteLine("[↺] 新一局开始");

                    broadcastMsg = CaptureGameStateUpdate();
                }
            }

            if (deferredError != null)
            {
                SendError(sender, deferredError);
                return;
            }

            if (broadcastMsg != null)
                BroadcastMessage(broadcastMsg);

            CheckAndTriggerAI();
        }

        /// <summary>处理断线重连请求。将新 TCP 连接关联到原玩家槽位。</summary>
        private void HandleReconnect(ClientConnection sender, string json)
        {
            ReconnectMessage msg = JsonConvert.DeserializeObject<ReconnectMessage>(json);
            int targetPid = msg.PlayerId;

            if (targetPid < 0 || targetPid > 3)
            {
                SendError(sender, "无效的玩家 ID。");
                return;
            }

            // 检查目标玩家是否存在（已加入）且处于断线状态（或仍显示已连接但需要强制断开）
            bool playerExists;
            bool isStillConnected;
            lock (_gameStateLock)
            {
                var p = _gameState.Players[targetPid];
                playerExists = p.HasJoined;
                isStillConnected = p.IsConnected;
            }

            // 玩家从未加入 → 拒绝
            if (!playerExists)
            {
                SendError(sender, "该玩家不存在，请以新身份加入游戏。");
                return;
            }

            // 玩家仍在线（服务器未检测到断线）→ 允许重连，强制断开旧连接
            // 玩家已断线（无论宽限期是否过期）→ 始终允许重连
            if (!isStillConnected)
            {
                // 宽限期内：重连优先；宽限期外：同样允许重连（AI 托管不阻塞重连）
                // 只需确认玩家身份存在即可
            }

            // 处理旧连接（在 _gameStateLock 之外，与 OnClientDisconnected 保持一致的锁顺序：_clientsLock 在前）
            ClientConnection oldConn = null;
            lock (_clientsLock)
            {
                oldConn = _clients.Find(c => c != sender && c.PlayerId == targetPid);
                if (oldConn != null)
                    _clients.Remove(oldConn);
            }

            if (oldConn != null)
            {
                oldConn.PlayerId = -1;
                oldConn.Stop();
            }

            GameStateUpdateMessage broadcastMsg = null;
            lock (_gameStateLock)
            {
                _reconnectGracePeriods.Remove(targetPid);

                sender.PlayerId = targetPid;
                sender.IsInitialized = true;

                var player = _gameState.Players[targetPid];
                player.IsConnected = true;
                if (!string.IsNullOrWhiteSpace(msg.PlayerName))
                    player.Name = msg.PlayerName;

                string name = player.Name;
                AddLog(string.Format("{0} 重新连接了游戏。", name));
                Console.WriteLine("[+] {0}({1}) 重连成功", CN(targetPid), name);

                StopAI();

                broadcastMsg = CaptureGameStateUpdate();
            }

            sender.SendMessage(new JoinGameResponseMessage { PlayerId = targetPid });

            if (broadcastMsg != null)
                BroadcastMessage(broadcastMsg);

            CheckAndTriggerAI();
        }

        /// <summary>处理聊天消息，用服务器端玩家名覆盖后广播。</summary>
        private void HandleChatMessage(ClientConnection sender, string json)
        {
            ChatMessage msg = JsonConvert.DeserializeObject<ChatMessage>(json);
            if (msg == null || string.IsNullOrWhiteSpace(msg.Content))
                return;

            lock (_gameStateLock)
            {
                if (sender.PlayerId >= 0 && sender.PlayerId < 4)
                    msg.SenderName = _gameState.Players[sender.PlayerId].Name;
            }

            string logMsg = string.Format("[聊天] {0}: {1}", msg.SenderName, msg.Content);
            AddLog(logMsg);
            Console.WriteLine("[聊] {0}: {1}", CN(sender.PlayerId), msg.Content);

            BroadcastMessage(msg);
        }

        /// <summary>客户端断开连接时调用。标记断线、启动宽限期、AI 立即接管。</summary>
        public void OnClientDisconnected(ClientConnection client)
        {
            lock (_clientsLock)
            {
                _clients.Remove(client);
            }

            int playerIndex = client.PlayerId;

            if (playerIndex < 0 || playerIndex >= 4)
            {
                client.Stop();
                return;
            }

            // 安全检查：是否已有新连接接管了此玩家？
            lock (_clientsLock)
            {
                var replacement = _clients.Find(c => c != client && c.PlayerId == playerIndex && c.IsInitialized);
                if (replacement != null)
                {
                    Console.WriteLine("[-] {0}({1}) 旧连接断开，但已有新连接接管，跳过处理", CN(playerIndex), playerIndex);
                    client.Stop();
                    return;
                }
            }

            string playerName = "未知";
            bool turnPassed = false;
            GameStateUpdateMessage broadcastMsg = null;

            lock (_gameStateLock)
            {
                if (!_gameState.Players[playerIndex].HasJoined)
                {
                    client.Stop();
                    return;
                }

                var player = _gameState.Players[playerIndex];
                playerName = player.Name;
                player.IsConnected = false;

                _reconnectGracePeriods[playerIndex] = DateTime.UtcNow;

                if (_gameState.CurrentPlayerIndex == playerIndex && !_gameState.GameOver)
                {
                    _gameState.CurrentPlayerIndex = _engine.GetNextPlayerIndex(_gameState);
                    _gameState.DiceValue = 0;
                    turnPassed = true;
                }

                broadcastMsg = CaptureGameStateUpdate();
            }

            string logMsg = string.Format("{0} 掉线了（AI接管，可随时重连）{1}",
                playerName, turnPassed ? "，跳过其回合。" : "");
            AddLog(logMsg);
            Console.WriteLine("[-] {0}({1}) → 掉线，AI接管（可随时重连）", CN(playerIndex), playerName);

            // 广播玩家离开消息，CheckAndTriggerAI 立即接管
            PlayerLeftMessage leftMsg = new PlayerLeftMessage
            {
                PlayerIndex = playerIndex,
                PlayerName = playerName
            };

            BroadcastMessage(leftMsg);
            if (broadcastMsg != null)
                BroadcastMessage(broadcastMsg);
            CheckAndTriggerAI();

            client.PlayerId = -1;
            client.Stop();
        }

        private void SendError(ClientConnection client, string message)
        {
            ErrorMessage errorMsg = new ErrorMessage { Message = message };
            client.SendMessage(errorMsg);
        }

        private void BroadcastGameState()
        {
            BroadcastMessage(CaptureGameStateUpdate());
        }

        /// <summary>制备 GameStateUpdate 快照（需持有 _gameStateLock）。</summary>
        private GameStateUpdateMessage CaptureGameStateUpdate()
        {
            string[] logsCopy;
            lock (_logLock)
            {
                logsCopy = _logMessages.ToArray();
            }
            _gameState.LogMessages = logsCopy;
            return new GameStateUpdateMessage
            {
                State = _gameState.DeepCopy()
            };
        }

        /// <summary>向所有已初始化的客户端广播消息。</summary>
        private void BroadcastMessage(object message)
        {
            List<ClientConnection> snapshot;
            lock (_clientsLock)
            {
                snapshot = new List<ClientConnection>(_clients);
            }

            foreach (var client in snapshot)
            {
                if (client.IsInitialized)
                {
                    client.SendMessage(message);
                }
            }
        }

        /// <summary>调试命令：强制指定玩家获胜。</summary>
        public void ForceWin(int playerIndex)
        {
            if (playerIndex < 0 || playerIndex > 3) return;
            GameStateUpdateMessage broadcastMsg = null;
            lock (_gameStateLock)
            {
                // 指定玩家全部归营，排名第一
                for (int i = 0; i < 4; i++)
                    _gameState.Players[playerIndex].Pieces[i] = FlightChessEngine.GoalPosition;
                _gameState.Players[playerIndex].Rank = 1;
                _gameState.FinishCount = 1;
                _gameState.WinnerIndex = playerIndex;

                // 其他已连接玩家自动分配后续排名
                int nextRank = 2;
                for (int p = 0; p < 4; p++)
                {
                    if (p == playerIndex || !_gameState.Players[p].IsConnected) continue;
                    for (int i = 0; i < 4; i++)
                        _gameState.Players[p].Pieces[i] = FlightChessEngine.GoalPosition;
                    _gameState.Players[p].Rank = nextRank++;
                    _gameState.FinishCount++;
                }

                _gameState.DiceValue = 0;
                string logMsg = string.Format("[调试] {0} 获得第一名！（共{1}人完成）",
                    _gameState.Players[playerIndex].Name, _gameState.FinishCount);
                AddLog(logMsg);
                Console.WriteLine("[调试] {0} 强制获胜 (共{1}人完成)", CN(playerIndex), _gameState.FinishCount);

                broadcastMsg = CaptureGameStateUpdate();
            }
            if (broadcastMsg != null)
                BroadcastMessage(broadcastMsg);
        }

        /// <summary>调试命令：触发踩子动画。</summary>
        public void ForceKick(int kickerIdx, int kickedIdx)
        {
            if (kickerIdx < 0 || kickerIdx > 3 || kickedIdx < 0 || kickedIdx > 3) return;
            if (kickerIdx == kickedIdx)
            {
                Console.WriteLine("[调试] 踩方和被踩方不能是同一人");
                return;
            }

            GameStateUpdateMessage broadcastMsg = null;
            lock (_gameStateLock)
            {
                var kicker = _gameState.Players[kickerIdx];
                var kicked = _gameState.Players[kickedIdx];

                if (!kicker.IsConnected || !kicked.IsConnected)
                {
                    Console.WriteLine("[调试] 双方都必须已连接");
                    return;
                }

                // 重置双方棋子到基地（简化场景）
                for (int i = 0; i < 4; i++)
                {
                    kicker.Pieces[i] = -1;
                    kicked.Pieces[i] = -1;
                }

                // 选一个非安全格作为踩子点（绝对格子 3，颜色类型=蓝）
                const int targetAbs = 3;
                int kickedLocal = (targetAbs - kicked.StartOffset + 52) % 52;
                int kickerTargetLocal = (targetAbs - kicker.StartOffset + 52) % 52;

                // 被踩者棋子放在目标格
                kicked.Pieces[0] = kickedLocal;

                int dice;
                // 踩人者：从主路径上前几步出发，掷骰子恰好到达目标格
                if (kickerTargetLocal >= 4)
                {
                    dice = 4;
                    kicker.Pieces[0] = kickerTargetLocal - dice;
                }
                else if (kickerTargetLocal >= 1)
                {
                    dice = kickerTargetLocal;
                    kicker.Pieces[0] = 0;
                }
                else
                {
                    // 目标就是起点格，从 START 出发
                    dice = 1;
                    kicker.Pieces[0] = FlightChessEngine.StartPosition;
                }

                // 确保骰子在有效范围内
                if (dice < 1 || dice > 6)
                {
                    Console.WriteLine("[调试] 骰子值({0})无效，跳过", dice);
                    return;
                }

                // 重置排名状态（避免被误判为游戏已结束）
                for (int p = 0; p < 4; p++)
                    _gameState.Players[p].Rank = 0;
                _gameState.FinishCount = 0;
                _gameState.CurrentPlayerIndex = kickerIdx;
                _gameState.DiceValue = dice;

                MoveResult result = _engine.MovePiece(_gameState, kickerIdx, 0);

                string logMsg = string.Format("[调试] {0}({1}方) 踩到 {2}({3}方)！{4}",
                    kicker.Name, FlightChessEngine.PlayerColorNames[kickerIdx],
                    kicked.Name, FlightChessEngine.PlayerColorNames[kickedIdx],
                    result.Message);
                AddLog(logMsg);
                Console.WriteLine("[调试] {0} 踩 {1}! {2}", CN(kickerIdx), CN(kickedIdx), result.Message);

                broadcastMsg = CaptureGameStateUpdate();
            }

            if (broadcastMsg != null)
                BroadcastMessage(broadcastMsg);
        }

        // 心跳检测 & 重连宽限期

        /// <summary>心跳定时器：每 5 秒 Ping 所有客户端，15 秒无响应则断开。</summary>
        private void HeartbeatTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                var now = DateTime.UtcNow;
                const double timeoutSeconds = 15.0;

                List<ClientConnection> snapshot;
                lock (_clientsLock)
                {
                    snapshot = _clients.Where(c => c.IsInitialized).ToList();
                }

                var staleClients = new List<ClientConnection>();
                foreach (var client in snapshot)
                {
                    client.SendMessage(new PingMessage());
                    if ((now - client.LastActivityTime).TotalSeconds > timeoutSeconds)
                    {
                        staleClients.Add(client);
                    }
                }

                foreach (var client in staleClients)
                {
                    Console.WriteLine("[!] 心跳超时: 玩家{0}({1})", client.PlayerId, CN(client.PlayerId));
                    OnClientDisconnected(client);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] 心跳检测错误: {0}", ex.Message);
            }
        }

        /// <summary>宽限期定时器：每 5 秒检查，超过 30 秒未重连则移除宽限期。</summary>
        private void GraceCheckTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                var now = DateTime.UtcNow;
                bool anyExpired = false;
                GameStateUpdateMessage broadcastMsg = null;

                lock (_gameStateLock)
                {
                    var expired = new List<int>();
                    foreach (var kvp in _reconnectGracePeriods)
                    {
                        if ((now - kvp.Value).TotalSeconds >= 30.0)
                            expired.Add(kvp.Key);
                    }

                    foreach (var pid in expired)
                    {
                        _reconnectGracePeriods.Remove(pid);
                        string name = _gameState.Players[pid].Name;
                        AddLog(string.Format("{0} 的宽限期已过，不再保证重连。", name));
                        Console.WriteLine("[-] {0}({1}) 宽限期已过", CN(pid), name);
                        anyExpired = true;
                    }

                    if (anyExpired)
                        broadcastMsg = CaptureGameStateUpdate();
                }

                if (broadcastMsg != null)
                {
                    BroadcastMessage(broadcastMsg);
                    CheckAndTriggerAI();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] 宽限期检查错误: {0}", ex.Message);
            }
        }

        // 控制台输出 & AI 托管

        private static string CN(int idx) => FlightChessEngine.PlayerColorNames[idx];

        private bool HasAnyConnectedPlayer()
        {
            lock (_gameStateLock)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (_gameState.Players[i].IsConnected)
                        return true;
                }
            }
            return false;
        }

        /// <summary>检查当前玩家是否需要 AI 托管或跳过。</summary>
        private void CheckAndTriggerAI()
        {
            while (true)
            {
                if (!HasAnyConnectedPlayer())
                {
                    StopAI();
                    Console.WriteLine("[AI] 所有玩家已离开，暂停托管");
                    return;
                }

                bool needsAI = false;
                bool shouldContinue = false;
                int aiPlayer = -1;

                lock (_gameStateLock)
                {
                    if (_gameState.GameOver)
                        return;

                    var cp = _gameState.Players[_gameState.CurrentPlayerIndex];

                    if (!cp.HasJoined)
                    {
                        string name = cp.Name;
                        _gameState.CurrentPlayerIndex = _engine.GetNextPlayerIndex(_gameState);
                        _gameState.DiceValue = 0;
                        AddLog(string.Format("{0} 未加入，跳过。", name));
                        Console.WriteLine("[AI] {0} 从未加入，跳过 → {1}",
                            CN(_gameState.CurrentPlayerIndex == 0 ? 3 : _gameState.CurrentPlayerIndex - 1),
                            CN(_gameState.CurrentPlayerIndex));
                        shouldContinue = true;
                    }
                    else if (!cp.IsConnected && cp.Rank == 0)
                    {
                        needsAI = true;
                        aiPlayer = _gameState.CurrentPlayerIndex;
                    }
                }

                if (shouldContinue)
                {
                    BroadcastGameState();
                    continue;
                }

                if (needsAI)
                {
                    if (_aiTimer == null)
                    {
                        _aiTimer = new System.Timers.Timer(1200);
                        _aiTimer.AutoReset = false;
                        _aiTimer.Elapsed += OnAITimerElapsed;
                    }
                    _aiTimer.Stop();
                    _aiTimer.Start();
                    Console.WriteLine("[AI] 定时器已启动，1.2秒后接管 {0}", CN(aiPlayer));
                }
                break;
            }
        }

        private static string FmtPos(int oldPos, int newPos)
        {
            if (oldPos == -1) return "起飞";
            if (oldPos == FlightChessEngine.StartPosition) return string.Format("START→{0}", newPos);
            if (newPos == FlightChessEngine.GoalPosition) return string.Format("{0}→终点", oldPos);
            return string.Format("{0}→{1}", oldPos, newPos);
        }

        private static string FmtFx(MoveResult result)
        {
            string fx = "";
            foreach (var k in result.KickedPieces)
                fx += string.Format(" 踩{0}棋{1}!", CN(k.Item1), k.Item2 + 1);
            if (result.Message.Contains("飞跃")) fx += " 飞跃!";
            else if (result.Message.Contains("同色格")) fx += " 同色跳!";
            else if (result.Message.Contains("回退")) fx += " 回退!";
            if (result.ExtraTurn) fx += " [6点]";
            return fx;
        }

        /// <summary>AI 定时器回调：执行掷骰或移动操作。</summary>
        private void OnAITimerElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                if (!HasAnyConnectedPlayer())
                {
                    StopAI();
                    Console.WriteLine("[AI] 所有玩家已离开，停止托管");
                    return;
                }

                bool shouldBroadcast = false;
                bool shouldContinue = false;
                bool noValidMoves = false;
                GameStateUpdateMessage broadcastMsg = null;
                int aiPlayerId;

                lock (_gameStateLock)
                {
                    if (_gameState.GameOver)
                        return;

                    int curIdx = _gameState.CurrentPlayerIndex;
                    var player = _gameState.Players[curIdx];

                    if (player.IsConnected || player.Rank > 0 || !player.HasJoined)
                        return;

                    aiPlayerId = curIdx;

                    if (_gameState.DiceValue == 0)
                    {
                        // 阶段1：掷骰子
                        int dice = _engine.RollDice();
                        _gameState.DiceValue = dice;

                        AddLog(string.Format("[AI] {0} 掷出了 {1} 点。", player.Name, dice));

                        var validMoves = _engine.GetValidMoves(player, dice);

                        if (validMoves.Count == 0)
                        {
                            AddLog(string.Format("[AI] {0} 没有棋子可以移动，轮到下一家。", player.Name));
                            Console.WriteLine("[AI] {0} 掷{1}, 无棋可移→跳过", CN(curIdx), dice);
                            noValidMoves = true;
                        }
                        else
                        {
                            Console.WriteLine("[AI] {0} 掷{1}", CN(curIdx), dice);
                            shouldContinue = true;
                        }

                        broadcastMsg = CaptureGameStateUpdate();
                        shouldBroadcast = true;
                    }
                    else
                    {
                        // 阶段2：随机选择合法移动
                        var validMoves = _engine.GetValidMoves(player, _gameState.DiceValue);
                        if (validMoves.Count > 0)
                        {
                            int pieceIdx = validMoves[_aiRng.Next(validMoves.Count)];
                            int oldPos = player.Pieces[pieceIdx];
                            int pieceNum = pieceIdx + 1;

                            MoveResult result = _engine.MovePiece(_gameState, curIdx, pieceIdx);
                            int newPos = player.Pieces[pieceIdx];

                            AddLog(string.Format("[AI] {0} 移动棋子 {1}：{2}",
                                player.Name, pieceNum, result.Message));

                            Console.WriteLine("[AI] {0} 棋{1}: {2}{3}",
                                CN(curIdx), pieceNum, FmtPos(oldPos, newPos), FmtFx(result));

                            if (result.GameOver)
                            {
                                AddLog("所有玩家均已完成归营，游戏结束！");
                                Console.WriteLine("[!!] 游戏结束!");
                                _gameState.DiceValue = 0;
                            }
                            else if (result.ExtraTurn)
                            {
                                AddLog(string.Format("[AI] {0} 掷出了 6 点，额外回合！", player.Name));
                                _gameState.DiceValue = 0;
                                shouldContinue = true;
                            }
                            else
                            {
                                _gameState.CurrentPlayerIndex = _engine.GetNextPlayerIndex(_gameState);
                                _gameState.DiceValue = 0;
                                shouldContinue = true;
                            }
                        }

                        broadcastMsg = CaptureGameStateUpdate();
                        shouldBroadcast = true;
                    }
                }

                if (shouldBroadcast && broadcastMsg != null)
                    BroadcastMessage(broadcastMsg);

                if (noValidMoves)
                {
                    int capturedPlayer = aiPlayerId;
                    var skipTimer = new System.Timers.Timer(1500) { AutoReset = false };
                    skipTimer.Elapsed += (s, ev) =>
                    {
                        try
                        {
                            GameStateUpdateMessage skipMsg = null;
                            lock (_gameStateLock)
                            {
                                if (_gameState.CurrentPlayerIndex == capturedPlayer && !_gameState.GameOver)
                                {
                                    _gameState.CurrentPlayerIndex = _engine.GetNextPlayerIndex(_gameState);
                                    _gameState.DiceValue = 0;
                                    skipMsg = CaptureGameStateUpdate();
                                }
                            }
                            if (skipMsg != null)
                                BroadcastMessage(skipMsg);
                            CheckAndTriggerAI();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[!] AI自动跳过错误: {0}", ex.Message);
                        }
                        skipTimer.Dispose();
                    };
                    skipTimer.Start();
                    return;
                }

                if (shouldContinue)
                    CheckAndTriggerAI();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] AI错误: {0}", ex.Message);
                try { CheckAndTriggerAI(); } catch { }
            }
        }

        private void StopAI()
        {
            try { _aiTimer?.Stop(); } catch { }
        }

        private void AddLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            lock (_logLock)
            {
                _logMessages.Add(string.Format("[{0}] {1}", timestamp, message));

                // 限制日志条数
                while (_logMessages.Count > 200)
                {
                    _logMessages.RemoveAt(0);
                }
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FlightChess.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlightChess.Server
{
    /// <summary>
    /// 飞行棋游戏服务器，负责接受客户端连接、处理消息、维护游戏状态并广播更新。
    /// </summary>
    public class GameServer
    {
        private TcpListener _listener;
        private Thread _acceptThread;
        private bool _isRunning;

        /// <summary>所有客户端连接（加锁访问）</summary>
        private readonly object _clientsLock = new object();
        private List<ClientConnection> _clients;

        /// <summary>游戏状态锁</summary>
        private readonly object _gameStateLock = new object();
        private GameState _gameState;

        /// <summary>日志列表锁</summary>
        private readonly object _logLock = new object();
        private List<string> _logMessages;

        /// <summary>游戏引擎</summary>
        private FlightChessEngine _engine;

        // ========== AI 托管 ==========
        private System.Timers.Timer _aiTimer;
        private Random _aiRng;

        public GameServer()
        {
            _clients = new List<ClientConnection>();
            _engine = new FlightChessEngine();
            _logMessages = new List<string>();
            _gameState = new GameState();
            _aiRng = new Random();
        }

        /// <summary>
        /// 启动服务器，开始监听。
        /// </summary>
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
        }

        /// <summary>
        /// 停止服务器
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            StopAI();
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

        /// <summary>
        /// 接受客户端连接循环
        /// </summary>
        private void AcceptClientsLoop()
        {
            while (_isRunning)
            {
                try
                {
                    TcpClient tcpClient = _listener.AcceptTcpClient();

                    int assignedId = -1;
                    lock (_gameStateLock)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            if (!_gameState.Players[i].IsConnected)
                            {
                                assignedId = i;
                                break;
                            }
                        }

                        if (assignedId < 0)
                        {
                            Console.WriteLine("[!] 房间已满，拒绝连接");
                            try
                            {
                                var stream = tcpClient.GetStream();
                                var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
                                writer.AutoFlush = true;
                                var errorMsg = new ErrorMessage { Message = "房间已满（最多 4 人）。" };
                                writer.WriteLine(JsonConvert.SerializeObject(errorMsg));
                                writer.Close();
                            }
                            catch { }
                            tcpClient.Close();
                            continue;
                        }
                    }

                    ClientConnection conn = new ClientConnection(tcpClient, this);
                    conn.PlayerId = assignedId;

                    lock (_clientsLock)
                    {
                        _clients.Add(conn);
                    }

                    conn.Start();
                    Console.WriteLine("[+] {0} → {1}(ID:{2})",
                        tcpClient.Client.RemoteEndPoint, CN(assignedId), assignedId);
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
        /// 处理加入游戏请求
        /// </summary>
        private void HandleJoinGame(ClientConnection sender, string json)
        {
            if (sender.IsInitialized)
            {
                SendError(sender, "你已经加入游戏了。");
                return;
            }

            JoinGameMessage msg = JsonConvert.DeserializeObject<JoinGameMessage>(json);
            string playerName = string.IsNullOrWhiteSpace(msg.PlayerName)
                ? string.Format("玩家{0}", sender.PlayerId + 1)
                : msg.PlayerName;

            lock (_gameStateLock)
            {
                var player = _gameState.Players[sender.PlayerId];
                player.Name = playerName;
                player.IsConnected = true;
                player.HasJoined = true;
                _gameState.ConnectedPlayerCount++;
                sender.IsInitialized = true;
            }

            string logMsg = string.Format("{0} 加入了游戏。", playerName);
            AddLog(logMsg);
            Console.WriteLine("[+] {0}: {1}", CN(sender.PlayerId), playerName);

            // 向该客户端发送 JoinGameResponse 告知其玩家 ID
            JoinGameResponseMessage response = new JoinGameResponseMessage
            {
                PlayerId = sender.PlayerId
            };
            sender.SendMessage(response);

            BroadcastGameState();
            CheckAndTriggerAI();  // 如果当前回合是断线玩家，恢复 AI 托管
        }

        /// <summary>
        /// 处理掷骰子请求
        /// </summary>
        private void HandleRollDice(ClientConnection sender)
        {
            if (!sender.IsInitialized)
            {
                SendError(sender, "请先加入游戏。");
                return;
            }

            bool noValidMoves = false;

            lock (_gameStateLock)
            {
                if (_gameState.GameOver)
                {
                    SendError(sender, "游戏已经结束。");
                    return;
                }

                if (sender.PlayerId != _gameState.CurrentPlayerIndex)
                {
                    SendError(sender, "还没轮到你。");
                    return;
                }

                if (_gameState.DiceValue != 0)
                {
                    SendError(sender, "你已经掷过骰子了，请先移动棋子。");
                    return;
                }

                // 掷骰子
                int dice = _engine.RollDice();
                _gameState.DiceValue = dice;

                string playerName = _gameState.Players[sender.PlayerId].Name;
                string logMsg = string.Format("{0} 掷出了 {1} 点。", playerName, dice);
                AddLog(logMsg);

                // 检查是否有合法移动
                var validMoves = _engine.GetValidMoves(
                    _gameState.Players[sender.PlayerId], dice);

                if (validMoves.Count == 0)
                {
                    noValidMoves = true;
                    Console.WriteLine("[掷] {0} → {1}, 无棋可移→跳过", CN(sender.PlayerId), dice);
                    logMsg = string.Format("{0} 没有棋子可以移动，轮到下一家。", playerName);
                    AddLog(logMsg);
                }
                else
                {
                    Console.WriteLine("[掷] {0} → {1}", CN(sender.PlayerId), dice);
                }
            }

            // 先广播骰子结果，确保客户端总能显示点数
            BroadcastGameState();

            if (noValidMoves)
            {
                // 等待玩家看到骰子点数后，再切换玩家并清零骰子
                Thread.Sleep(1500);

                lock (_gameStateLock)
                {
                    _gameState.CurrentPlayerIndex = _engine.GetNextPlayerIndex(_gameState);
                    _gameState.DiceValue = 0;
                }

                // 广播切换后的状态（下一家回合，骰子归零）
                BroadcastGameState();
                CheckAndTriggerAI();  // 检查下一位玩家是否需要 AI 托管
            }
        }

        /// <summary>
        /// 处理移动棋子请求
        /// </summary>
        private void HandleMovePiece(ClientConnection sender, string json)
        {
            if (!sender.IsInitialized)
            {
                SendError(sender, "请先加入游戏。");
                return;
            }

            MovePieceMessage msg = JsonConvert.DeserializeObject<MovePieceMessage>(json);

            lock (_gameStateLock)
            {
                if (_gameState.GameOver)
                {
                    SendError(sender, "游戏已经结束。");
                    return;
                }

                if (sender.PlayerId != _gameState.CurrentPlayerIndex)
                {
                    SendError(sender, "还没轮到你。");
                    return;
                }

                if (_gameState.DiceValue == 0)
                {
                    SendError(sender, "请先掷骰子。");
                    return;
                }

                // 验证移动合法性
                var validMoves = _engine.GetValidMoves(
                    _gameState.Players[sender.PlayerId], _gameState.DiceValue);

                if (!validMoves.Contains(msg.PieceIndex))
                {
                    SendError(sender, "该棋子当前无法移动。");
                    return;
                }

                // 执行移动
                MoveResult result = _engine.MovePiece(_gameState, sender.PlayerId, msg.PieceIndex);

                if (!result.Success)
                {
                    SendError(sender, result.Message);
                    return;
                }

                string playerName = _gameState.Players[sender.PlayerId].Name;
                string logMsg = result.Message;
                AddLog(logMsg);
                Console.WriteLine("[移] {0} 棋{1}: {2}", CN(sender.PlayerId),
                    msg.PieceIndex + 1, result.Message);

                // 处理后续
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
            }

            BroadcastGameState();
            CheckAndTriggerAI();  // 检查下一位玩家是否需要 AI 托管
        }

        /// <summary>
        /// 处理重新开始游戏请求 — 重置所有棋子，保留玩家连接
        /// </summary>
        private void HandleResetGame(ClientConnection sender)
        {
            lock (_gameStateLock)
            {
                if (!_gameState.GameOver)
                {
                    SendError(sender, "游戏尚未结束，无法重置。");
                    return;
                }

                // 重置游戏状态
                for (int i = 0; i < 4; i++)
                {
                    bool wasConnected = _gameState.Players[i].IsConnected;
                    string name = _gameState.Players[i].Name;
                    _gameState.Players[i] = new Common.Player(i, name,
                        FlightChessEngine.PlayerStartOffsets[i]);
                    _gameState.Players[i].IsConnected = wasConnected;
                }
                _gameState.CurrentPlayerIndex = 0;
                _gameState.DiceValue = 0;
                _gameState.WinnerIndex = -1;
                _gameState.FinishCount = 0;

                AddLog("游戏已重置，新一局开始！");
                Console.WriteLine("[↺] 新一局开始");
            }
            BroadcastGameState();
            CheckAndTriggerAI();  // 重置后检查首玩家是否需要 AI
        }

        /// <summary>
        /// 处理聊天消息 — 直接广播给所有客户端
        /// </summary>
        private void HandleChatMessage(ClientConnection sender, string json)
        {
            ChatMessage msg = JsonConvert.DeserializeObject<ChatMessage>(json);
            if (msg == null || string.IsNullOrWhiteSpace(msg.Content))
                return;

            // 用发送者连接中的玩家名覆盖（防止客户端伪造）
            lock (_gameStateLock)
            {
                if (sender.PlayerId >= 0 && sender.PlayerId < 4)
                    msg.SenderName = _gameState.Players[sender.PlayerId].Name;
            }

            string logMsg = string.Format("[聊天] {0}: {1}", msg.SenderName, msg.Content);
            AddLog(logMsg);
            Console.WriteLine("[聊] {0}: {1}", CN(sender.PlayerId), msg.Content);

            // 广播给所有已初始化的客户端（包括发送者）
            BroadcastMessage(msg);
        }

        /// <summary>
        /// 客户端断开连接时的处理
        /// </summary>
        public void OnClientDisconnected(ClientConnection client)
        {
            lock (_clientsLock)
            {
                _clients.Remove(client);
            }

            string playerName = "未知";
            int playerIndex = client.PlayerId;

            lock (_gameStateLock)
            {
                if (client.PlayerId >= 0 && client.PlayerId < 4)
                {
                    var player = _gameState.Players[client.PlayerId];
                    playerName = player.Name;
                    player.IsConnected = false;
                    // ConnectedPlayerCount 不减少 — 保持加入玩家总数，AI 可继续为其游戏

                    // 如果离开的是当前玩家，切换到下一个
                    if (_gameState.CurrentPlayerIndex == client.PlayerId && !_gameState.GameOver)
                    {
                        _gameState.CurrentPlayerIndex = _engine.GetNextPlayerIndex(_gameState);
                        _gameState.DiceValue = 0;
                    }
                }
            }

            string logMsg = string.Format("{0} 离开了游戏，AI 将自动托管。", playerName);
            AddLog(logMsg);
            Console.WriteLine("[-] {0}({1}) → AI托管", CN(playerIndex), playerName);

            // 广播玩家离开消息
            PlayerLeftMessage leftMsg = new PlayerLeftMessage
            {
                PlayerIndex = playerIndex,
                PlayerName = playerName
            };

            BroadcastMessage(leftMsg);
            BroadcastGameState();
            CheckAndTriggerAI();  // 检查当前玩家是否需要 AI 托管

            client.Stop();
        }

        /// <summary>
        /// 向单个客户端发送错误消息
        /// </summary>
        private void SendError(ClientConnection client, string message)
        {
            ErrorMessage errorMsg = new ErrorMessage { Message = message };
            client.SendMessage(errorMsg);
        }

        /// <summary>
        /// 向所有客户端广播游戏状态
        /// </summary>
        private void BroadcastGameState()
        {
            GameStateUpdateMessage updateMsg;
            lock (_gameStateLock)
            {
                string[] logsCopy;
                lock (_logLock)
                {
                    logsCopy = _logMessages.ToArray();
                }
                _gameState.LogMessages = logsCopy;
                updateMsg = new GameStateUpdateMessage
                {
                    State = _gameState.DeepCopy()
                };
            }
            BroadcastMessage(updateMsg);
        }

        /// <summary>
        /// 向所有已初始化的客户端广播消息
        /// </summary>
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

        /// <summary>
        /// 调试命令：强制指定玩家获胜，其他已连接玩家自动排名
        /// </summary>
        public void ForceWin(int playerIndex)
        {
            if (playerIndex < 0 || playerIndex > 3) return;
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
            }
            BroadcastGameState();
        }

        /// <summary>
        /// 调试命令：触发踩子炸裂动画 — 让 kicker 的棋子踩 kicked 的棋子
        /// </summary>
        public void ForceKick(int kickerIdx, int kickedIdx)
        {
            if (kickerIdx < 0 || kickerIdx > 3 || kickedIdx < 0 || kickedIdx > 3) return;
            if (kickerIdx == kickedIdx)
            {
                Console.WriteLine("[调试] 踩方和被踩方不能是同一人");
                return;
            }

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
            }

            BroadcastGameState();
        }

        // =================================================================
        //  控制台输出 & AI 托管
        // =================================================================

        /// <summary>控制台使用的玩家简称（红/绿/黄/蓝）</summary>
        private static string CN(int idx) => FlightChessEngine.PlayerColorNames[idx];

        /// <summary>检查是否还有任何人类玩家保持连接</summary>
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

        /// <summary>检查当前玩家是否需要 AI 托管，如果是则启动延迟定时器</summary>
        private void CheckAndTriggerAI()
        {
            if (!HasAnyConnectedPlayer())
            {
                StopAI();
                Console.WriteLine("[AI] 所有玩家已离开，暂停托管");
                return;
            }

            bool needsAI = false;
            lock (_gameStateLock)
            {
                if (!_gameState.GameOver)
                {
                    var cp = _gameState.Players[_gameState.CurrentPlayerIndex];
                    if (!cp.IsConnected && cp.Rank == 0)
                        needsAI = true;
                }
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
            }
        }

        /// <summary>格式化位置变化为简洁描述（用于控制台）</summary>
        private static string FmtPos(int oldPos, int newPos)
        {
            if (oldPos == -1) return "起飞";
            if (oldPos == FlightChessEngine.StartPosition) return string.Format("START→{0}", newPos);
            if (newPos == FlightChessEngine.GoalPosition) return string.Format("{0}→终点", oldPos);
            return string.Format("{0}→{1}", oldPos, newPos);
        }

        /// <summary>格式化移动附加效果（踩子/飞跃/同色跳/回退/6点）</summary>
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

        /// <summary>AI 定时器回调：执行一次掷骰或移动操作</summary>
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

                lock (_gameStateLock)
                {
                    if (_gameState.GameOver)
                        return;

                    int curIdx = _gameState.CurrentPlayerIndex;
                    var player = _gameState.Players[curIdx];

                    if (player.IsConnected || player.Rank > 0)
                        return;

                    if (_gameState.DiceValue == 0)
                    {
                        // === 阶段 1：掷骰子 ===
                        int dice = _engine.RollDice();
                        _gameState.DiceValue = dice;

                        AddLog(string.Format("[AI] {0} 掷出了 {1} 点。", player.Name, dice));

                        var validMoves = _engine.GetValidMoves(player, dice);

                        if (validMoves.Count == 0)
                        {
                            AddLog(string.Format("[AI] {0} 没有棋子可以移动，轮到下一家。", player.Name));
                            Console.WriteLine("[AI] {0} 掷{1}, 无棋可移→跳过", CN(curIdx), dice);

                            _gameState.CurrentPlayerIndex = _engine.GetNextPlayerIndex(_gameState);
                            _gameState.DiceValue = 0;
                            shouldContinue = true;
                        }
                        else
                        {
                            Console.WriteLine("[AI] {0} 掷{1}", CN(curIdx), dice);
                            shouldContinue = true;
                        }

                        shouldBroadcast = true;
                    }
                    else
                    {
                        // === 阶段 2：随机选择合法移动 ===
                        var validMoves = _engine.GetValidMoves(player, _gameState.DiceValue);
                        if (validMoves.Count > 0)
                        {
                            int pieceIdx = validMoves[_aiRng.Next(validMoves.Count)];
                            int oldPos = player.Pieces[pieceIdx];
                            int pieceNum = pieceIdx + 1;

                            MoveResult result = _engine.MovePiece(_gameState, curIdx, pieceIdx);
                            int newPos = player.Pieces[pieceIdx];

                            // 客户端日志：完整信息
                            AddLog(string.Format("[AI] {0} 移动棋子 {1}：{2}",
                                player.Name, pieceNum, result.Message));

                            // 控制台：简洁输出
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

                        shouldBroadcast = true;
                    }
                }

                if (shouldBroadcast)
                    BroadcastGameState();

                if (shouldContinue)
                    CheckAndTriggerAI();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] AI错误: {0}", ex.Message);
                try { CheckAndTriggerAI(); } catch { }
            }
        }

        /// <summary>停止 AI 托管定时器</summary>
        private void StopAI()
        {
            try { _aiTimer?.Stop(); } catch { }
        }

        /// <summary>
        /// 添加日志
        /// </summary>
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

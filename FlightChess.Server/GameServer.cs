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

        public GameServer()
        {
            _clients = new List<ClientConnection>();
            _engine = new FlightChessEngine();
            _logMessages = new List<string>();
            _gameState = new GameState();
        }

        /// <summary>
        /// 启动服务器，开始监听。
        /// </summary>
        public void Start(int port = 8888)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _isRunning = true;

            Console.WriteLine("=================================");
            Console.WriteLine("  飞行棋联机游戏服务器");
            Console.WriteLine("  监听端口: {0}", port);
            Console.WriteLine("=================================");

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
                    Console.WriteLine("[连接] 新客户端连接: {0}", tcpClient.Client.RemoteEndPoint);

                    int assignedId = -1;
                    lock (_gameStateLock)
                    {
                        // 查找空闲位置
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
                            Console.WriteLine("[拒绝] 房间已满，拒绝新连接。");
                            // 发送拒绝消息
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
                    Console.WriteLine("[分配] 客户端分配到玩家 ID: {0}", assignedId);
                }
                catch (SocketException)
                {
                    // 监听器已停止
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[错误] 接受连接时出错: {0}", ex.Message);
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
                Console.WriteLine("[错误] 处理消息时出错: {0}", ex.Message);
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
                _gameState.ConnectedPlayerCount++;
                sender.IsInitialized = true;
            }

            string logMsg = string.Format("{0} 加入了游戏。", playerName);
            AddLog(logMsg);
            Console.WriteLine("[加入] {0} (ID: {1})", playerName, sender.PlayerId);

            // 向该客户端发送 JoinGameResponse 告知其玩家 ID
            JoinGameResponseMessage response = new JoinGameResponseMessage
            {
                PlayerId = sender.PlayerId
            };
            sender.SendMessage(response);

            BroadcastGameState();
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

                string logMsg = string.Format("{0} 掷出了 {1} 点。",
                    _gameState.Players[sender.PlayerId].Name, dice);
                AddLog(logMsg);
                Console.WriteLine("[游戏] " + logMsg);

                // 检查是否有合法移动
                var validMoves = _engine.GetValidMoves(
                    _gameState.Players[sender.PlayerId], dice);

                if (validMoves.Count == 0)
                {
                    // 无合法移动，切换到下一个玩家
                    logMsg = string.Format("{0} 没有棋子可以移动，轮到下一家。",
                        _gameState.Players[sender.PlayerId].Name);
                    AddLog(logMsg);
                    Console.WriteLine("[游戏] " + logMsg);

                    _gameState.CurrentPlayerIndex = _engine.GetNextPlayerIndex(_gameState);
                    _gameState.DiceValue = 0;
                }
            }

            BroadcastGameState();
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

                string logMsg = result.Message;
                AddLog(logMsg);
                Console.WriteLine("[游戏] " + logMsg);

                // 处理后续
                if (result.GameOver)
                {
                    AddLog(string.Format("游戏结束！{0} 获得胜利！",
                        _gameState.Players[result.WinnerIndex].Name));
                    _gameState.DiceValue = 0;
                }
                else if (result.ExtraTurn)
                {
                    // 掷出 6 点，同一位玩家继续
                    AddLog(string.Format("{0} 掷出了 6 点，获得额外回合！",
                        _gameState.Players[sender.PlayerId].Name));
                    _gameState.DiceValue = 0; // 重置骰子以便重新掷
                }
                else
                {
                    // 切换到下一个玩家
                    _gameState.CurrentPlayerIndex = _engine.GetNextPlayerIndex(_gameState);
                    _gameState.DiceValue = 0;
                }
            }

            BroadcastGameState();
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
                    _gameState.ConnectedPlayerCount--;

                    // 如果离开的是当前玩家，切换到下一个
                    if (_gameState.CurrentPlayerIndex == client.PlayerId && !_gameState.GameOver)
                    {
                        _gameState.CurrentPlayerIndex = _engine.GetNextPlayerIndex(_gameState);
                        _gameState.DiceValue = 0;
                    }
                }
            }

            string logMsg = string.Format("{0} 离开了游戏。", playerName);
            AddLog(logMsg);
            Console.WriteLine("[离开] {0} (ID: {1})", playerName, playerIndex);

            // 广播玩家离开消息
            PlayerLeftMessage leftMsg = new PlayerLeftMessage
            {
                PlayerIndex = playerIndex,
                PlayerName = playerName
            };

            BroadcastMessage(leftMsg);
            BroadcastGameState();

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

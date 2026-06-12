using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using FlightChess.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FlightChess.Server
{
    /// <summary>
    /// 表示一个客户端连接，负责消息的接收与发送。
    /// </summary>
    public class ClientConnection
    {
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private Thread _receiveThread;
        private GameServer _server;
        private bool _isRunning;

        /// <summary>该连接对应的玩家 ID（-1 表示尚未分配）</summary>
        public int PlayerId { get; set; }

        /// <summary>是否已通过 JoinGame 完成初始化</summary>
        public bool IsInitialized { get; set; }

        /// <summary>最后一次收到该客户端消息的时间（用于心跳检测）</summary>
        public DateTime LastActivityTime { get; set; }

        public ClientConnection(TcpClient tcpClient, GameServer server)
        {
            _tcpClient = tcpClient;
            _server = server;
            PlayerId = -1;
            IsInitialized = false;
            _isRunning = true;

            _stream = tcpClient.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8);
            _writer = new StreamWriter(_stream, Encoding.UTF8);
            _writer.AutoFlush = true;
            LastActivityTime = DateTime.UtcNow;
        }

        /// <summary>
        /// 启动接收线程
        /// </summary>
        public void Start()
        {
            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Start();
        }

        /// <summary>
        /// 接收循环：不断读取 JSON 行并转发给服务器处理
        /// </summary>
        private void ReceiveLoop()
        {
            try
            {
                while (_isRunning)
                {
                    string line = _reader.ReadLine();
                    if (line == null)
                    {
                        // 客户端断开连接
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    LastActivityTime = DateTime.UtcNow; // 更新最后活动时间

                    // 简洁日志：仅显示消息类型，不输出完整 JSON
                    try
                    {
                        JObject obj = JObject.Parse(line);
                        string type = obj["Type"]?.Value<string>() ?? "?";
                        Console.WriteLine("[收] {0}: {1}", PlayerId, type);
                    }
                    catch { Console.WriteLine("[收] {0}: ?", PlayerId); }
                    _server.ProcessMessage(this, line);
                }
            }
            catch (IOException)
            {
                // 连接异常断开
            }
            catch (ObjectDisposedException)
            {
                // 流已关闭
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] 接收异常 (玩家{0}): {1}", PlayerId, ex.Message);
            }
            finally
            {
                _isRunning = false;
                _server.OnClientDisconnected(this);
            }
        }

        /// <summary>
        /// 向客户端发送消息
        /// </summary>
        public void SendMessage(object message)
        {
            try
            {
                string json = JsonConvert.SerializeObject(message);
                _writer.WriteLine(json);
                // 仅记录非 GameStateUpdate 的消息（GameStateUpdate 体积大且频繁）
                if (message is GameStateUpdateMessage)
                {
                    // 不输出大型状态更新
                }
                else
                {
                    var typeProp = message.GetType().GetProperty("Type");
                    string typeName = typeProp?.GetValue(message) as string ?? "?";
                    Console.WriteLine("[发] {0}: {1}", PlayerId, typeName);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[!] 发送失败 (玩家{0}): {1}", PlayerId, ex.Message);
            }
        }

        /// <summary>
        /// 替换底层 TCP 连接（用于断线重连时复用 ClientConnection 对象）
        /// </summary>
        public void ReplaceConnection(TcpClient newTcpClient)
        {
            try
            {
                // 关闭旧连接
                _stream?.Close();
                _tcpClient?.Close();
            }
            catch { }

            // 替换为新连接
            _tcpClient = newTcpClient;
            _stream = newTcpClient.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8);
            _writer = new StreamWriter(_stream, Encoding.UTF8);
            _writer.AutoFlush = true;
            _isRunning = true;
            LastActivityTime = DateTime.UtcNow;

            // 重启接收线程
            _receiveThread = new Thread(ReceiveLoop);
            _receiveThread.IsBackground = true;
            _receiveThread.Start();
        }

        /// <summary>
        /// 停止连接
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            try
            {
                _stream?.Close();
                _tcpClient?.Close();
            }
            catch { }
        }

        public override string ToString()
        {
            return string.Format("ClientConnection[PlayerId={0}, Initialized={1}]", PlayerId, IsInitialized);
        }
    }
}

using System;

namespace FlightChess.Server
{
    class Program
    {
        static void Main(string[] args)
        {
            int port = 8888;

            if (args.Length > 0)
            {
                if (!int.TryParse(args[0], out port))
                {
                    Console.WriteLine("用法: FlightChess.Server.exe [端口号]");
                    Console.WriteLine("示例: FlightChess.Server.exe 8888");
                    return;
                }
            }

            GameServer server = new GameServer();

            // 注册 Ctrl+C 事件以优雅退出
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n正在关闭服务器...");
                server.Stop();
                Console.WriteLine("服务器已关闭。按任意键退出...");
            };

            server.Start(port);

            Console.WriteLine("按 Ctrl+C 停止服务器...");

            // 主线程等待
            while (true)
            {
                string input = Console.ReadLine();
                if (input?.ToLower() == "exit" || input?.ToLower() == "quit")
                {
                    break;
                }
                if (input?.ToLower() == "status")
                {
                    Console.WriteLine("服务器正在运行，端口: {0}", port);
                }
                if (input != null && input.ToLower().StartsWith("win"))
                {
                    // 用法: win 0  或  win red
                    var parts = input.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        int idx = -1;
                        if (int.TryParse(parts[1], out idx) && idx >= 0 && idx <= 3)
                        {
                            server.ForceWin(idx);
                        }
                        else
                        {
                            switch (parts[1].ToLower())
                            {
                                case "red": case "红": server.ForceWin(0); break;
                                case "green": case "绿": server.ForceWin(1); break;
                                case "yellow": case "黄": server.ForceWin(2); break;
                                case "blue": case "蓝": server.ForceWin(3); break;
                                default:
                                    Console.WriteLine("用法: win <0-3>  或  win <red/green/yellow/blue>");
                                    break;
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("用法: win <0-3>  或  win <red/green/yellow/blue>");
                    }
                }
            }

            server.Stop();
            Console.WriteLine("服务器已关闭。");
        }
    }
}

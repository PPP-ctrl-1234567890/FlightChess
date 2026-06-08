using System;
using System.Windows.Forms;

namespace FlightChess.Client
{
    static class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 显示连接对话框
            using (ConnectForm connectForm = new ConnectForm())
            {
                if (connectForm.ShowDialog() == DialogResult.OK)
                {
                    // 连接成功，启动主窗体
                    MainForm mainForm = new MainForm(
                        connectForm.ServerAddress,
                        connectForm.ServerPort,
                        connectForm.PlayerName);
                    Application.Run(mainForm);
                }
            }
        }
    }
}

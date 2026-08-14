using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace BranchTaskWpf;

public partial class App : Application
{
    // 单实例锁：防止同时开多个窗口互相覆盖 projects.json 导致数据丢失
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例锁加固(btwpf89)：
        // 1) Mutex 检测（含 AbandonedMutexException 接管）
        // 2) 进程级检测（兜底）：即使 Mutex 因 Force 杀进程的 abandoned 时序失效，
        //    也能发现已存在的其他 branch-task-wpf 进程并退出，杜绝多实例并发写盘互相覆盖。
        bool createdNew = false;
        try
        {
            _singleInstanceMutex = new Mutex(true, "BranchTaskWpf_SingleInstance", out createdNew);
        }
        catch (AbandonedMutexException)
        {
            createdNew = true;
        }
        catch (Exception)
        {
            createdNew = false;
        }
        // 进程级兜底：除自己外还有 branch-task-wpf 实例 → 说明 Mutex 失效，必须退出
        if (createdNew)
        {
            var others = Process.GetProcessesByName("branch-task-wpf")
                .Where(p => p.Id != Environment.ProcessId).ToList();
            if (others.Count > 0)
                createdNew = false;
        }
        if (!createdNew)
        {
            MessageBox.Show(
                "任务树已经在运行了，不能同时打开两个实例（否则会互相覆盖数据）。\n请先关闭已打开的窗口，再重新打开。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (s, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogCrash("AppDomain", ex);
        };
    }

    public static void LogCrash(string source, Exception ex)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".branch-task", "crash.log");
            File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss}] {source}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n\n");
        }
        catch { }
    }
}

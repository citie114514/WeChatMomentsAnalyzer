using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace WeChatMomentsAnalyzer;

public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // 初始化 Windows App Runtime Bootstrap(解包应用必需)
        // 使用 1.4 版本:系统已安装 framework 4000.1309.2056.0 + DDLM 4000.1049.117.0
        int hr = MddBootstrapInitialize(0x00010004, "", ((ulong)4000 << 48));
        if (hr != 0)
        {
            MessageBox(0,
                $"Windows App Runtime 初始化失败 (0x{hr:X8})。\n请确认已安装 Windows App Runtime。",
                "启动错误", 0x10);
            return;
        }

        ComWrappersSupport.InitializeComWrappers();
        Application.Start((p) =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    [DllImport("Microsoft.WindowsAppRuntime.Bootstrap.dll", EntryPoint = "MddBootstrapInitialize", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MddBootstrapInitialize(uint majorMinorVersion, string versionTag, ulong minVersion);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}

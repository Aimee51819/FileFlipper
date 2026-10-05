using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FileFlipper;

/// <summary>
/// Entry point. Only one copy runs per Windows session; launching it again (for example from the
/// "Send to" menu) hands the files to the running copy over a named pipe.
/// </summary>
public static class Program
{
    private static readonly string InstanceName = $"FileFlipper-{Environment.UserName}-{Process.GetCurrentProcess().SessionId}";

    [STAThread]
    public static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var files = args.Where(a => !a.StartsWith("--")).Select(Path.GetFullPath).ToList();
        bool background = args.Contains("--background");

        using var mutex = new Mutex(true, "Local\\" + InstanceName, out bool isFirst);
        if (!isFirst)
        {
            Send(files.Count > 0 ? string.Join("\n", files) : "--show");
            return 0;
        }

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var tray = new TrayApp(application);
        application.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            Trace.WriteLine(e.Exception);
        };
        application.Startup += (_, _) =>
        {
            tray.Start(files, background);
            Listen(application.Dispatcher, tray);
        };
        return application.Run();
    }

    private static string PipeName => InstanceName + "-pipe";

    private static void Send(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(message);
            client.Write(bytes, 0, bytes.Length);
        }
        catch (Exception error) when (error is TimeoutException or IOException)
        {
            // The running copy didn't answer; nothing more to do.
        }
    }

    private static void Listen(Dispatcher dispatcher, TrayApp tray)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var message = reader.ReadToEnd();
                    if (message == "--show")
                    {
                        dispatcher.BeginInvoke(tray.ShowAlreadyRunning);
                    }
                    else
                    {
                        var files = message.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        dispatcher.BeginInvoke(() => tray.ShowPicker(files));
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
            }
        })
        { IsBackground = true, Name = "FileFlipper pipe" };
        thread.Start();
    }
}

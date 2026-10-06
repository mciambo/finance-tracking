using System.IO;
using System.Windows;

namespace Finance.Desktop;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var verify=Array.IndexOf(e.Args,"--verify-ui");
        if(verify>=0)
        {
            try
            {
                if(verify+1>=e.Args.Length||!e.Args.Contains("--db"))throw new ArgumentException("--verify-ui richiede directory e --db di prova.");
                var directory=Path.GetFullPath(e.Args[verify+1]);Directory.CreateDirectory(directory);
                var window=new MainWindow();MainWindow=window;await window.VerifyUi(directory);Shutdown(0);
            }
            catch(Exception ex)
            {
                if(verify+1<e.Args.Length)File.WriteAllText(Path.Combine(e.Args[verify+1],"error.txt"),ex.ToString());
                Shutdown(1);
            }
            return;
        }
        ShutdownMode=ShutdownMode.OnMainWindowClose;MainWindow=new MainWindow();MainWindow.Show();
    }
}

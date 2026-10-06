using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Finance.Core;
using Finance.Infrastructure;
using Microsoft.Win32;

namespace Finance.Desktop;

public partial class MainWindow : Window
{
    private readonly FinanceService service;
    private DateOnly reportEnd=Formats.MonthStart(Formats.Month(Formats.TodayRome()));
    private bool busy;
    public MainWindow()
    {
        InitializeComponent();
        var args=Environment.GetCommandLineArgs();var index=Array.IndexOf(args,"--db");
        service=new FinanceService(index>=0&&index+1<args.Length?args[index+1]:null);
        RuleList.ItemsSource=service.Rules.Select(r=>new RuleRow(r.Name,r.Description,r.UpdatedOn)).ToList();
    }
    private async void WindowLoaded(object sender,RoutedEventArgs e) => await Run(async()=>
    {
        await Task.Run(()=>service.Database.Initialize()); await RefreshData(true);
    });
    private async Task Run(Func<Task> action)
    {
        if(busy)return;busy=true;IsEnabled=false;Cursor=Cursors.Wait;Status.Text="";
        try{await action();}
        catch(Exception e){Status.Text=(e is FinanceException fe?fe.Code+": ":"")+e.Message;}
        finally{busy=false;IsEnabled=true;Cursor=null;}
    }
    private ExpenseFilter Filter()
    {
        DateOnly? Date(TextBox b)=>string.IsNullOrWhiteSpace(b.Text)?null:Formats.Date(b.Text.Trim());
        long? Money(TextBox b)=>string.IsNullOrWhiteSpace(b.Text)?null:Formats.Money(b.Text.Trim());
        return new(Date(FromBox),Date(ToBox),DescriptionBox.Text,Money(MinBox),Money(MaxBox));
    }
    private async Task RefreshData(bool closePending=false)
    {
        var filter=Filter();
        var data=await Task.Run(()=>
        {
            if(closePending && service.Months().Any(m=>m.State=="pending"))service.ClosePendingMonths();
            return (Expenses:service.Expenses(filter),Alerts:service.Alerts(),Report:service.Report(Formats.Month(reportEnd)));
        });
        ExpenseGrid.ItemsSource=data.Expenses.Select(e=>new ExpenseRow(e)).ToList();
        AlertGrid.ItemsSource=data.Alerts.Select(a=>new AlertRow(a)).ToList();
        AlertTab.Header=data.Alerts.Count==0?"Alert":$"Alert ({data.Alerts.Count})";
        Totals.Text=$"{data.Expenses.Count} spese · {(data.Expenses.Sum(e=>e.AmountCents)/100m).ToString("N2",Formats.Italian)} €";
        ReportRange.Text=$"{Formats.MonthLabel(reportEnd.AddMonths(-4))} – {Formats.MonthLabel(reportEnd)}";
        ReportPanel.Children.Clear();
        foreach(var report in data.Report)
        {
            var panel=new StackPanel { Margin=new Thickness(16) };
            panel.Children.Add(new TextBlock { Text=$"{Formats.MonthLabel(Formats.MonthStart(report.Month))}     {(report.TotalCents/100m).ToString("N2",Formats.Italian)} €",FontSize=19,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,10) });
            foreach(var expense in report.TopFive)
            {
                var row=new DockPanel { Margin=new Thickness(0,4,0,4) };
                var amount=new TextBlock { Text=(expense.AmountCents/100m).ToString("N2",Formats.Italian)+" €",Width=110,TextAlignment=TextAlignment.Right };
                DockPanel.SetDock(amount,Dock.Right);row.Children.Add(amount);
                row.Children.Add(new TextBlock { Text=expense.Description,TextTrimming=TextTrimming.CharacterEllipsis });panel.Children.Add(row);
            }
            ReportPanel.Children.Add(new Border { Background=Brushes.White,Margin=new Thickness(0,0,0,12),CornerRadius=new CornerRadius(5),Child=panel });
        }
    }
    private async void RefreshClick(object sender,RoutedEventArgs e)=>await Run(()=>RefreshData(true));
    private async void TabsChanged(object sender,SelectionChangedEventArgs e)
    {if(IsLoaded&&ReferenceEquals(e.OriginalSource,sender))await Run(()=>RefreshData(true));}
    private async void FilterClick(object sender,RoutedEventArgs e)=>await Run(()=>RefreshData(true));
    private void FilterKey(object sender,KeyEventArgs e){if(e.Key==Key.Enter)FilterClick(sender,e);}
    private async void ImportClick(object sender,RoutedEventArgs e)
    {
        var picker=new OpenFileDialog { Filter="Excel (*.xlsx)|*.xlsx",CheckFileExists=true };
        if(picker.ShowDialog(this)!=true)return;
        await Run(async()=>
        {
            var preview=await Task.Run(()=>service.PreviewImport(picker.FileName));
            if(MessageBox.Show(this,$"{preview.Movements} movimenti\n{preview.Expenses} uscite · {preview.Alerts} alert\n{preview.IgnoredIncome} entrate escluse", "Importa Excel",MessageBoxButton.OKCancel,MessageBoxImage.None)!=MessageBoxResult.OK)return;
            await Task.Run(()=>service.Import(picker.FileName,preview.Token));await RefreshData();
        });
    }
    private async void EditClick(object sender,RoutedEventArgs e)
    {
        if(ExpenseGrid.SelectedItem is not ExpenseRow row)return;
        var dialog=new EditExpenseWindow(row.Expense) { Owner=this };
        if(dialog.ShowDialog()!=true)return;
        await Run(async()=>{await Task.Run(()=>service.Edit(row.Expense.Id,dialog.Date,dialog.Description,dialog.AmountCents));await RefreshData();});
    }
    private async void AddClick(object sender,RoutedEventArgs e)
    {
        var dialog=new EditExpenseWindow(new("",Formats.TodayRome(),"",0,null,[])) { Owner=this };
        if(dialog.ShowDialog()!=true)return;
        await Run(async()=>{await Task.Run(()=>service.Add(dialog.Date,dialog.Description,dialog.AmountCents));await RefreshData();});
    }
    private async void DeleteClick(object sender,RoutedEventArgs e)
    {
        if(ExpenseGrid.SelectedItem is not ExpenseRow row)return;
        if(MessageBox.Show(this,$"Eliminare «{row.Description}»?","Elimina",MessageBoxButton.YesNo,MessageBoxImage.None)!=MessageBoxResult.Yes)return;
        await Run(async()=>{await Task.Run(()=>service.Delete(row.Expense.Id));await RefreshData();});
    }
    private async void HandleClick(object sender,RoutedEventArgs e)
    {
        if(AlertGrid.SelectedItem is not AlertRow row)return;
        await Run(async()=>{await Task.Run(()=>service.HandleAlert(row.Alert.Id));await RefreshData();});
    }
    private async void ApplyRulesClick(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        var preview=await Task.Run(()=>service.PreviewRules());
        if(MessageBox.Show(this,$"{preview.Changes.Count} modifiche alle spese\n{preview.AlertChanges?.Count??0} modifiche agli alert","Aggiorna storico",MessageBoxButton.OKCancel,MessageBoxImage.None)!=MessageBoxResult.OK)return;
        await Task.Run(()=>service.ApplyRules(preview.Token));await RefreshData();
    });
    private async void PreviousClick(object sender,RoutedEventArgs e)=>await Run(async()=>{reportEnd=reportEnd.AddMonths(-5);await RefreshData(true);});
    private async void NextClick(object sender,RoutedEventArgs e)=>await Run(async()=>{reportEnd=reportEnd.AddMonths(5);await RefreshData(true);});
    internal async Task VerifyUi(string directory)
    {
        service.Database.Initialize();await RefreshData(true);
        var root=(Grid)Content;Content=null;
        root.Resources=Resources;root.Background=(Brush)new BrushConverter().ConvertFromString("#F7F8FA")!;
        TextElement.SetFontFamily(root,new FontFamily("Segoe UI"));TextElement.SetFontSize(root,14);
        using var host=new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Finance UI verification") { Width=1120,Height=760,WindowStyle=unchecked((int)0x80000000) });
        host.RootVisual=root;
        await Dispatcher.InvokeAsync(()=>{},System.Windows.Threading.DispatcherPriority.Loaded);
        root.Measure(new Size(1120,760));root.Arrange(new Rect(0,0,1120,760));root.UpdateLayout();
        if(ExpenseGrid.Columns.Count!=3||ExpenseGrid.Items.Count==0)throw new Exception("Tabella spese non valida.");
        if(Icon is null)throw new Exception("Icona della finestra assente.");
        var tab=(TabControl)root.Children[1];
        void Capture(string name)
        {
            root.Measure(new Size(1120,760));root.Arrange(new Rect(0,0,1120,760));root.UpdateLayout();
            if(name=="spese"&&ExpenseGrid.Columns[0].ActualWidth<100)throw new Exception("Colonne spese troppo strette.");
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(1120,760,96,96,PixelFormats.Pbgra32);
            bitmap.Render(root);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream=File.Create(Path.Combine(directory,name+".png"));encoder.Save(stream);
        }
        Capture("spese");tab.SelectedIndex=1;Capture("report");tab.SelectedIndex=2;Capture("alert");tab.SelectedIndex=3;Capture("regole");tab.SelectedIndex=0;
        if(RuleList.Items.Count!=service.Rules.Count||service.Rules.Any(r=>string.IsNullOrWhiteSpace(r.Description)||r.UpdatedOn==default))throw new Exception("Catalogo regole incompleto.");
        var originalCount=ExpenseGrid.Items.Count;
        FromBox.Text="01/10/2026";ToBox.Text="31/10/2026";DescriptionBox.Text="hotel";MinBox.Text="20";MaxBox.Text="200";
        await RefreshData();
        if(ExpenseGrid.Items.Count!=1)throw new Exception("Filtri UI non coerenti.");
        var expense=((ExpenseRow)ExpenseGrid.Items[0]).Expense;
        var dialog=new EditExpenseWindow(expense);dialog.VerifyEdit("03/10/2026","Hotel corretto","50,00");
        service.Edit(expense.Id,dialog.Date,dialog.Description,dialog.AmountCents);
        FromBox.Text="";ToBox.Text="";DescriptionBox.Text="";MinBox.Text="";MaxBox.Text="";await RefreshData();
        if(!service.Expenses().Any(e=>e.Description=="Hotel corretto"&&e.Date==new DateOnly(2026,10,3)&&e.AmountCents==5000))throw new Exception("Modifica UI fallita.");
        service.Delete(expense.Id);await RefreshData();if(ExpenseGrid.Items.Count!=originalCount-1)throw new Exception("Eliminazione UI fallita.");
        var alert=((AlertRow)AlertGrid.Items[0]).Alert;service.HandleAlert(alert.Id);await RefreshData();if(service.Alerts().Any(a=>a.Id==alert.Id))throw new Exception("Gestione alert fallita.");
        var addDialog=new EditExpenseWindow(new("",Formats.TodayRome(),"",0,null,[]));addDialog.VerifyEdit("04/10/2026","Spesa manuale","12,50");
        var added=service.Add(addDialog.Date,addDialog.Description,addDialog.AmountCents);await RefreshData();
        if(!ExpenseGrid.Items.Cast<ExpenseRow>().Any(r=>r.Expense.Id==added.Id&&r.Expense.AmountCents==1250))throw new Exception("Aggiunta manuale UI fallita.");
        File.WriteAllText(Path.Combine(directory,"result.txt"),"UI verified: layout, tabs, filters, add/edit validation, delete, alerts.");
    }
}

public record ExpenseRow(Expense Expense)
{
    public string DateText=>Expense.Date.ToString("dd/MM/yyyy");
    public string Description=>Expense.Description;
    public string AmountText=>(Expense.AmountCents/100m).ToString("N2",Formats.Italian);
}
public record AlertRow(IncomeAlert Alert)
{
    public string DateText=>Alert.Date.ToString("dd/MM/yyyy");
    public string Description=>Alert.Description;
    public string AmountText=>(Alert.AmountCents/100m).ToString("N2",Formats.Italian);
}
public record RuleRow(string Name,string Description,DateOnly UpdatedOn)
{
    public string UpdatedText=>"Ultima modifica · "+UpdatedOn.ToString("dd/MM/yyyy");
}

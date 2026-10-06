using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Finance.Core;

namespace Finance.Desktop;

public sealed class EditExpenseWindow : Window
{
    private readonly TextBox dateBox=new(),descriptionBox=new(),amountBox=new();
    private readonly TextBlock error=new() { Foreground=Brushes.DarkRed,TextWrapping=TextWrapping.Wrap };
    public DateOnly Date { get; private set; }
    public string Description { get; private set; }="";
    public long AmountCents { get; private set; }
    public EditExpenseWindow(Expense expense)
    {
        Title=expense.Id.Length==0?"Aggiungi spesa":"Modifica spesa";Width=560;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;FontFamily=new FontFamily("Segoe UI");FontSize=14;
        dateBox.Text=expense.Date.ToString("dd/MM/yyyy");descriptionBox.Text=expense.Description;amountBox.Text=expense.Id.Length==0?"":(expense.AmountCents/100m).ToString("F2",Formats.Italian);
        var panel=new StackPanel { Margin=new Thickness(24) };
        foreach(var (label,box) in new[]{("Data",dateBox),("Descrizione",descriptionBox),("Importo €",amountBox)})
        {panel.Children.Add(new TextBlock { Text=label,Margin=new Thickness(0,12,0,4) });box.Padding=new Thickness(8);panel.Children.Add(box);}
        panel.Children.Add(error);
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0) };
        var cancel=new Button { Content="Annulla",IsCancel=true,Padding=new Thickness(14,7,14,7),Margin=new Thickness(0,0,8,0) };
        var save=new Button { Content="Salva",IsDefault=true,Padding=new Thickness(14,7,14,7) };
        save.Click+=(_,_)=>
        {
            try
            {
                Validate();
                DialogResult=true;
            }
            catch(Exception e){error.Text=e.Message;}
        };
        buttons.Children.Add(cancel);buttons.Children.Add(save);panel.Children.Add(buttons);Content=panel;
    }
    private void Validate()
    {
        Date=Formats.Date(dateBox.Text.Trim());Description=descriptionBox.Text.Trim();AmountCents=Formats.Money(amountBox.Text.Trim());
        if(Description.Length==0)throw new FinanceException("VALIDATION","Inserire una descrizione.");
    }
    internal void VerifyEdit(string date,string description,string amount)
    {dateBox.Text=date;descriptionBox.Text=description;amountBox.Text=amount;Validate();}
}

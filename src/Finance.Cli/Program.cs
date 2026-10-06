using System.Globalization;
using System.Text;
using System.Text.Json;
using Finance.Core;
using Finance.Infrastructure;
using Microsoft.Data.Sqlite;

Console.OutputEncoding = Encoding.UTF8;
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower,WriteIndented=true };
bool json=args.Contains("json");
try
{
    var options=new Dictionary<string,List<string>>(); var positions=new List<string>();
    string[] flags=["apply","help"];
    string[] values=["db","format","token","date","description","amount","from","to","min","max","out","end","months","param","file"];
    for(int i=0;i<args.Length;i++)
    {
        if(!args[i].StartsWith("--")) { positions.Add(args[i]); continue; }
        var key=args[i][2..];
        if(!flags.Contains(key)&&!values.Contains(key))throw new FinanceException("USAGE",$"Opzione sconosciuta: --{key}");
        var value=flags.Contains(key)?"true":++i<args.Length&&!args[i].StartsWith("--")?args[i]:throw new FinanceException("USAGE",$"Valore mancante per --{key}");
        if(!options.TryGetValue(key,out var list))options[key]=list=[];
        if(key!="param"&&list.Count>0)throw new FinanceException("USAGE",$"Opzione ripetuta: --{key}");
        list.Add(value);
    }
    string? Get(string key)=>options.GetValueOrDefault(key)?.LastOrDefault();
    string Pos(int i)=>positions.Count>i?positions[i]:throw new FinanceException("USAGE","Argomento mancante. Usare finance help.");
    var format=Get("format")??"text"; json=format=="json";
    if(format is not ("json" or "text" or "csv"))throw new FinanceException("USAGE","Formato ammesso: text, json, csv.");
    if(positions.Count==0||Get("help") is not null||Pos(0)=="help")
    {
        Console.WriteLine("""
            finance [--db percorso] [--format text|json|csv] COMANDO
            init
            import file.xlsx [--apply] [--token TOKEN]
            expenses list|export [--from gg/mm/aaaa] [--to gg/mm/aaaa]
              [--description testo] [--min euro] [--max euro] [--out file]
            expenses get ID
            expenses add --date gg/mm/aaaa --description testo --amount euro
            expenses edit ID [--date gg/mm/aaaa] [--description testo] [--amount euro]
            expenses delete|reset ID
            alerts list | alerts handle ID
            report [--end yyyy-MM] [--months 5]
            rules list|preview | rules apply --token TOKEN
            months list|close-pending | months reopen|close yyyy-MM
            query "SELECT ..." [--param nome=valore] | query --file query.sql
            backup [--out file.db] | restore file.db --apply
            JSON: date ISO, importi in centesimi. Errori su stderr.
            Exit: 0 successo, 1 errore, 2 argomenti, 3 conflitto, 4 database occupato.
            """); return 0;
    }
    var service=new FinanceService(Get("db")); var command=Pos(0); var sub=positions.Count>1?Pos(1):"list"; object? result;
    DateOnly? DateArg(string key)=>Get(key) is {} v?Formats.Date(v):null;
    long? AmountArg(string key)=>Get(key) is {} v?Formats.Money(v):null;
    switch(command)
    {
        case "init": service.Database.Initialize(); result=new { database=service.Database.Path }; break;
        case "import": result=Get("apply") is null?service.PreviewImport(Pos(1)):service.Import(Pos(1),Get("token")); break;
        case "expenses":
            switch(sub)
            {
                case "add":result=service.Add(DateArg("date")??throw new FinanceException("USAGE","Data mancante."),Get("description")??throw new FinanceException("USAGE","Descrizione mancante."),AmountArg("amount")??throw new FinanceException("USAGE","Importo mancante."));break;
                case "list": case "export": result=service.Expenses(new(DateArg("from"),DateArg("to"),Get("description"),AmountArg("min"),AmountArg("max"))); break;
                case "get": result=service.Expenses().SingleOrDefault(e=>e.Id==Pos(2))??throw new FinanceException("NOT_FOUND","Spesa non trovata."); break;
                case "edit":
                    if(Get("date") is null&&Get("description") is null&&Get("amount") is null)throw new FinanceException("USAGE","Specificare data, descrizione o importo.");
                    service.Edit(Pos(2),DateArg("date"),Get("description"),AmountArg("amount"));result=new { updated=Pos(2) };break;
                case "delete":service.Delete(Pos(2));result=new { deleted=Pos(2) };break;
                case "reset":service.Reset(Pos(2));result=new { reset=Pos(2) };break;
                default:throw new FinanceException("USAGE","Comando expenses sconosciuto.");
            } break;
        case "alerts":
            if(sub=="list")result=service.Alerts();
            else if(sub=="handle"){service.HandleAlert(Pos(2));result=new { handled=Pos(2) };}
            else throw new FinanceException("USAGE","Comando alerts sconosciuto.");break;
        case "report":result=service.Report(Get("end"),int.Parse(Get("months")??"5",CultureInfo.InvariantCulture));break;
        case "rules":
            if(sub=="list")result=service.Rules.Select(r=>new { r.Id,r.Version,r.Order,r.Name,r.Description,r.UpdatedOn }).ToArray();
            else if(sub=="preview")result=service.PreviewRules();
            else if(sub=="apply")result=service.ApplyRules(Get("token")??throw new FinanceException("USAGE","rules apply richiede --token da rules preview."));
            else throw new FinanceException("USAGE","Comando rules sconosciuto.");break;
        case "months":
            if(sub=="list")result=service.Months();
            else if(sub=="close-pending"){service.ClosePendingMonths();result=service.Months();}
            else if(sub is "reopen" or "close"){service.SetMonth(Pos(2),sub=="close");result=service.Months();}
            else throw new FinanceException("USAGE","Comando months sconosciuto.");break;
        case "query":
            var parameters=new Dictionary<string,string>();
            foreach(var pair in options.GetValueOrDefault("param")??[])
            {var index=pair.IndexOf('=');if(index<=0)throw new FinanceException("USAGE","Parametro: --param nome=valore.");parameters.Add(pair[..index],pair[(index+1)..]);}
            result=service.Query(Get("file") is {} file?File.ReadAllText(file):Pos(1),parameters);break;
        case "backup":result=new { path=service.Database.Backup(Get("out")) };break;
        case "restore":
            if(Get("apply") is null)throw new FinanceException("USAGE","Il ripristino richiede --apply.");
            service.Database.Restore(Pos(1));result=new { restored=service.Database.Path };break;
        default:throw new FinanceException("USAGE","Comando sconosciuto. Usare finance help.");
    }
    string Csv(string value)=>"\""+value.Replace("\"","\"\"")+"\"";
    string outputText;
    if(format=="csv")
    {
        if(result is IEnumerable<Expense> expenses)
            outputText="id,date,description,amount_cents,rule_id\n"+string.Join('\n',expenses.Select(e=>$"{Csv(e.Id)},{e.Date:yyyy-MM-dd},{Csv(e.Description)},{e.AmountCents},{Csv(e.RuleId??"")}"));
        else
        {
            var element=JsonSerializer.SerializeToElement(result,jsonOptions);
            if(element.ValueKind!=JsonValueKind.Array)throw new FinanceException("USAGE","CSV disponibile per risultati tabellari.");
            var rows=element.EnumerateArray().ToList();var keys=rows.FirstOrDefault().ValueKind==JsonValueKind.Object?rows[0].EnumerateObject().Select(p=>p.Name).ToArray():[];
            outputText=string.Join(',',keys.Select(Csv))+"\n"+string.Join('\n',rows.Select(r=>string.Join(',',keys.Select(k=>Csv(r.GetProperty(k).ToString())))));
        }
    }
    else if(format=="text"&&result is IEnumerable<Expense> expenses)
        outputText=string.Join('\n',expenses.Select(e=>$"{e.Date:dd/MM/yyyy}  {(e.AmountCents/100m).ToString("N2",Formats.Italian),10} €  {e.Description}  [{e.Id}]"));
    else outputText=JsonSerializer.Serialize(result,jsonOptions);
    if(command=="expenses"&&sub=="export"&&Get("out") is {} output)
    {
        if(new[]{service.Database.Path,service.Database.Path+"-wal",service.Database.Path+"-shm"}.Contains(Path.GetFullPath(output),StringComparer.OrdinalIgnoreCase))throw new FinanceException("VALIDATION","L'esportazione non può sovrascrivere il database.");
        File.WriteAllText(output,outputText,new UTF8Encoding(false));
    }
    else Console.WriteLine(outputText);
    return 0;
}
catch(Exception e)
{
    var code=e is FinanceException fe?fe.Code:e is SqliteException se&&se.SqliteErrorCode is 5 or 6?"DATABASE_BUSY":e is SqliteException?"DATABASE_ERROR":e is IOException or UnauthorizedAccessException?"IO_ERROR":e is FormatException or ArgumentException?"USAGE":"INTERNAL";
    var exit=code switch { "USAGE" or "VALIDATION"=>2,"ANCHOR_MISSING" or "ANCHOR_AMBIGUOUS" or "MONTH_CLOSED" or "OVERRIDE_CONFLICT" or "STALE_PREVIEW" or "RULES_PENDING" or "MONTH_CLOSE_REQUIRED"=>3,"DATABASE_BUSY"=>4,_=>1 };
    Console.Error.WriteLine(json?JsonSerializer.Serialize(new { error=new { code,message=e.Message },exit_code=exit },jsonOptions):$"{code}: {e.Message}"); return exit;
}

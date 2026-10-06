using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Finance.Core;
using Finance.Rules;
using Microsoft.Data.Sqlite;
using static Finance.Infrastructure.Database;

namespace Finance.Infrastructure;

public sealed class FinanceService
{
    public Database Database { get; }
    private readonly Func<DateOnly> today;
    public IReadOnlyList<IExpenseRule> Rules { get; }
    private string RulesVersion => Hash(string.Join('|', Rules.Select(r => $"{r.Id}:{r.Version}:{r.Order}:{r.GetType().Assembly.ManifestModule.ModuleVersionId}")));
    public FinanceService(string? path = null, Func<DateOnly>? today = null, IReadOnlyList<IExpenseRule>? rules = null)
    {
        Database = new(path ?? Infrastructure.Database.DefaultPath);
        this.today = today ?? Formats.TodayRome;
        Rules = (rules ?? RuleRegistry.Create()).OrderBy(r => r.Order).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();
        if (Rules.Select(r => r.Id).Distinct().Count() != Rules.Count) throw new FinanceException("RULES_INVALID", "ID delle regole duplicati.");
        if(Rules.Any(r=>!System.Text.RegularExpressions.Regex.IsMatch(r.Id,@"^[a-z0-9-]+$"))) throw new FinanceException("RULES_INVALID","ID delle regole non valido.");
    }
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Members(IEnumerable<string> ids) => string.Join(',', ids.Order(StringComparer.Ordinal));
    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd");
    private static long Revision(SqliteConnection c, SqliteTransaction? tx) => long.Parse((string)Scalar(c,tx,"SELECT value FROM metadata WHERE key='revision'")!);
    private string Token(SqliteConnection c, SqliteTransaction? tx, string payload) => Hash($"{Scalar(c,tx,"SELECT value FROM metadata WHERE key='generation'")}|{Revision(c,tx)}|{today()}|{RulesVersion}|{payload}");
    private void RequireRules(SqliteConnection c, SqliteTransaction? tx)
    {
        var saved = (string)Scalar(c,tx,"SELECT value FROM metadata WHERE key='rules_version'")!;
        if (saved.Length > 0 && saved != RulesVersion) throw new FinanceException("RULES_PENDING", "Le regole sono cambiate: aprire Regole e usare Aggiorna storico.");
    }
    private static List<SourceMovement> Sources(SqliteConnection c, SqliteTransaction? tx) => Rows(c,tx,
        "SELECT id,sequence,value_date,income_cents,expense_cents,description,excel_row FROM movements ORDER BY sequence",
        r => new SourceMovement(r.GetString(0),r.GetInt64(1),new(r.GetInt32(6),ParseDate(r.GetString(2)),r.IsDBNull(3)?null:r.GetInt64(3),r.IsDBNull(4)?null:r.GetInt64(4),r.GetString(5))));
    private static Dictionary<string, ManualOverride> Overrides(SqliteConnection c, SqliteTransaction? tx) => Rows(c,tx,
        "SELECT expense_id,value_date,description,amount_cents,deleted,members FROM overrides",
        r => new ManualOverride(r.GetString(0),r.IsDBNull(1)?null:ParseDate(r.GetString(1)),r.IsDBNull(2)?null:r.GetString(2),r.IsDBNull(3)?null:r.GetInt64(3),r.GetInt32(4)!=0,r.IsDBNull(5)?null:r.GetString(5))).ToDictionary(o=>o.Id);
    private static Dictionary<string,string> States(SqliteConnection c, SqliteTransaction? tx) => Rows(c,tx,"SELECT month,state FROM months",r => new MonthState(r.GetString(0),r.GetString(1))).ToDictionary(m=>m.Month,m=>m.State);
    private List<string> PendingMonths(IEnumerable<SourceMovement> sources, Dictionary<string,string> states) => sources
        .Select(s=>Formats.Month(s.Original.Date)).Distinct().Where(m=>string.CompareOrdinal(m,Formats.Month(today()))<0 && !states.ContainsKey(m)).Order().ToList();
    private void RequireCurrent(SqliteConnection c, SqliteTransaction? tx)
    {
        var pending=PendingMonths(Sources(c,tx),States(c,tx));
        if (pending.Count>0) throw new FinanceException("MONTH_CLOSE_REQUIRED", $"Chiusura necessaria: {string.Join(", ",pending)}. Eseguire months close-pending.");
        RequireRules(c,tx);
    }
    private List<Expense> Project(IEnumerable<SourceMovement> sources, Dictionary<string,ManualOverride> overrides, Dictionary<string,string> states)
    {
        var allSources=sources.ToList();
        var cancelled=Cancellations(allSources).Select(p=>p.ExpenseSourceId).ToHashSet();
        var singles = new List<Expense>();
        var groups = new Dictionary<string,(string Label,List<Expense> Items)>();
        foreach (var source in allSources.Where(s=>s.Original.ExpenseCents is not null))
        {
            var original=source.Original; var result=new RuleResult(original.Description);
            foreach (var rule in Rules) result=rule.Apply(original,result);
            if(result.GroupKey is not null&&!System.Text.RegularExpressions.Regex.IsMatch(result.GroupKey,@"^[a-z0-9-]+$"))throw new FinanceException("RULES_INVALID","Chiave del raggruppamento non valida.");
            var id="s:"+source.Id; overrides.TryGetValue(id,out var edit);
            if(cancelled.Contains(source.Id))
            {
                if(edit is not null&&!edit.Deleted)throw new FinanceException("OVERRIDE_CONFLICT",$"Il canone {id} è corretto manualmente e verrebbe escluso dallo storno. Rimuovere esplicitamente la correzione con expenses reset.");
                continue;
            }
            if (result.Excluded || edit?.Deleted==true) continue;
            var item=new Expense(id,edit?.Date??original.Date,edit?.Description??result.Description,edit?.AmountCents??original.ExpenseCents!.Value,null,[source.Id]);
            var month=Formats.Month(item.Date);
            if (result.GroupKey is not null && states.GetValueOrDefault(month)=="closed")
            {
                var key=$"g:{result.GroupKey}:{month}";
                if (!groups.TryGetValue(key,out var group)) group=(result.GroupLabel??result.GroupKey,[]);
                group.Items.Add(item); groups[key]=group;
            }
            else singles.Add(item);
        }
        foreach (var (id,group) in groups)
        {
            var month=Formats.MonthStart(id[(id.LastIndexOf(':')+1)..]);
            var item=new Expense(id,month.AddMonths(1).AddDays(-1),$"{group.Label} {Formats.MonthLabel(month)}",checked(group.Items.Sum(i=>i.AmountCents)),id[2..id.LastIndexOf(':')],group.Items.SelectMany(i=>i.SourceIds).ToArray());
            if (overrides.TryGetValue(id,out var edit))
            {
                if (edit.Members != Members(item.SourceIds)) throw new FinanceException("OVERRIDE_CONFLICT", $"L'aggregato {id} ha una correzione manuale e una composizione diversa. Rimuovere esplicitamente la correzione con expenses reset.");
                item=item with { Date=edit.Date??item.Date,Description=edit.Description??item.Description,AmountCents=edit.AmountCents??item.AmountCents };
            }
            singles.Add(item);
        }
        foreach (var edit in overrides.Values.Where(o=>o.Id.StartsWith("g:",StringComparison.Ordinal)))
            if (!groups.ContainsKey(edit.Id)) throw new FinanceException("OVERRIDE_CONFLICT",$"L'aggregato corretto {edit.Id} non esiste più. Rimuovere esplicitamente la correzione con expenses reset.");
        return singles.OrderByDescending(e=>e.Date).ThenBy(e=>e.Id,StringComparer.Ordinal).ToList();
    }
    private void Rebuild(SqliteConnection c, SqliteTransaction tx)
    {
        var projected=Project(Sources(c,tx),Overrides(c,tx),States(c,tx));
        projected.AddRange(Rows(c,tx,"SELECT id,value_date,description,amount_cents FROM manual_expenses WHERE deleted=0",r=>new Expense(r.GetString(0),ParseDate(r.GetString(1)),r.GetString(2),r.GetInt64(3),null,[])));
        Execute(c,tx,"DELETE FROM expense_sources; DELETE FROM expenses;");
        foreach (var e in projected)
        {
            Execute(c,tx,"INSERT INTO expenses VALUES($id,$date,$description,$amount,$rule)",("$id",e.Id),("$date",Iso(e.Date)),("$description",e.Description),("$amount",e.AmountCents),("$rule",e.RuleId));
            foreach (var source in e.SourceIds) Execute(c,tx,"INSERT INTO expense_sources VALUES($id,$source)",("$id",e.Id),("$source",source));
        }
        Execute(c,tx,"DELETE FROM rule_cancellations;");
        foreach(var pair in Cancellations(Sources(c,tx)))
            Execute(c,tx,"INSERT INTO rule_cancellations VALUES($expense,$income,$rule)",("$expense",pair.ExpenseSourceId),("$income",pair.IncomeSourceId),("$rule",pair.RuleId));
        Execute(c,tx,"UPDATE metadata SET value=$version WHERE key='rules_version'",("$version",RulesVersion));
    }
    private void ClosePending(SqliteConnection c, SqliteTransaction tx)
    {
        foreach (var month in PendingMonths(Sources(c,tx),States(c,tx))) Execute(c,tx,"INSERT INTO months VALUES($month,'closed')",("$month",month));
    }
    private T Write<T>(Func<SqliteConnection,SqliteTransaction,T> action, bool requireRules=true)
    {
        Database.Initialize(); Database.Backup();
        using var c=Database.Open(); using var tx=c.BeginTransaction();
        if (requireRules) RequireRules(c,tx);
        var result=action(c,tx);
        Execute(c,tx,"UPDATE metadata SET value=CAST(value AS INTEGER)+1 WHERE key='revision'");
        tx.Commit(); return result;
    }
    private static List<BankMovement> NewMovements(IReadOnlyList<BankMovement> file, List<SourceMovement> existing)
    {
        var cutoff=new DateOnly(2026,1,1);
        if (existing.Count==0) return file.Where(r=>r.Date>=cutoff).ToList();
        var anchor=existing[^1].Original.Signature;
        var candidates=Enumerable.Range(0,file.Count).Where(i=>file[i].Signature==anchor).ToList();
        if (candidates.Count==0) throw new FinanceException("ANCHOR_MISSING","L'ultimo movimento originale registrato non è presente nell'Excel.");
        for (var depth=1;candidates.Count>1 && depth<existing.Count;depth++)
            candidates=candidates.Where(i=>i>=depth && file[i-depth].Signature==existing[^(depth+1)].Original.Signature).ToList();
        if (candidates.Count!=1) throw new FinanceException("ANCHOR_AMBIGUOUS","Impossibile individuare univocamente l'ultimo movimento usando le righe adiacenti.");
        return file.Skip(candidates[0]+1).Where(r=>r.Date>=cutoff).ToList();
    }
    private ImportPreview PreviewImport(SqliteConnection c, SqliteTransaction? tx, IReadOnlyList<BankMovement> file)
    {
        RequireRules(c,tx);
        var existing=Sources(c,tx); var states=States(c,tx);
        var closing=PendingMonths(existing,states);
        foreach (var month in closing) states[month]="closed";
        var incoming=NewMovements(file,existing);
        var late=incoming.Where(r=>states.GetValueOrDefault(Formats.Month(r.Date))=="closed").ToList();
        if (late.Count>0) throw new FinanceException("MONTH_CLOSED", "Nuovi movimenti in mesi chiusi: "+string.Join("; ",late.Select(r=>$"{Formats.Month(r.Date)} riga {r.Row}"))+". Riaprire esplicitamente i mesi tramite CLI.");
        if (existing.Count==0) closing=PendingMonths(incoming.Select((r,i)=>new SourceMovement(i.ToString(),i,r)),states);
        return new(Token(c,tx,JsonSerializer.Serialize(file)),incoming.Count,incoming.Count(r=>r.ExpenseCents is not null),CountNewAlerts(existing,incoming),incoming.Count(r=>r.IncomeCents is >150000),closing);
    }
    public ImportPreview PreviewImport(string path)
    {
        var rows=ExcelReader.Read(path);
        // Preview on an absent database remains read-only, including no directory creation.
        if (!File.Exists(Database.Path))
        {
            var incoming=rows.Where(r=>r.Date>=new DateOnly(2026,1,1)).ToList();
            return new(Hash($"new|{today()}|{RulesVersion}|{JsonSerializer.Serialize(rows)}"),incoming.Count,incoming.Count(r=>r.ExpenseCents is not null),CountNewAlerts([],incoming),incoming.Count(r=>r.IncomeCents is >150000),incoming.Select(r=>Formats.Month(r.Date)).Distinct().Where(m=>string.CompareOrdinal(m,Formats.Month(today()))<0).Order().ToArray());
        }
        using var c=Database.Open(true); using var tx=c.BeginTransaction(deferred:true); return PreviewImport(c,tx,rows);
    }
    public ImportPreview Import(string path,string? expectedToken=null)
    {
        var rows=ExcelReader.Read(path);
        var absent=!File.Exists(Database.Path);
        if (absent && expectedToken is not null && expectedToken!=Hash($"new|{today()}|{RulesVersion}|{JsonSerializer.Serialize(rows)}"))
            throw new FinanceException("STALE_PREVIEW","Anteprima non più valida; ripeterla.");
        return Write((c,tx)=>
        {
            var preview=PreviewImport(c,tx,rows);
            if(absent && expectedToken is not null && Revision(c,tx)!=0) throw new FinanceException("STALE_PREVIEW","Il database è stato creato e modificato dopo l'anteprima; ripeterla.");
            if (!absent && expectedToken is not null && expectedToken!=preview.Token) throw new FinanceException("STALE_PREVIEW","Il database o il file è cambiato; ripetere l'anteprima.");
            var importId=Guid.NewGuid().ToString("N"); var existing=Sources(c,tx); var incoming=NewMovements(rows,existing);
            if (incoming.Count>0)
            {
                Execute(c,tx,"INSERT INTO imports VALUES($id,$filename,$at,$count)",("$id",importId),("$filename",System.IO.Path.GetFileName(path)),("$at",DateTimeOffset.UtcNow.ToString("O")),("$count",incoming.Count));
                long sequence=existing.LastOrDefault()?.Sequence??0;
                foreach (var r in incoming)
                {
                    var id=Guid.NewGuid().ToString("N");
                    Execute(c,tx,"INSERT INTO movements VALUES($id,$sequence,$date,$income,$expense,$description,$import,$row)",("$id",id),("$sequence",++sequence),("$date",Iso(r.Date)),("$income",r.IncomeCents),("$expense",r.ExpenseCents),("$description",r.Description),("$import",importId),("$row",r.Row));
                    if (r.IncomeCents is <=150000) Execute(c,tx,"INSERT INTO alerts VALUES($id,NULL)",("$id",id));
                }
            }
            ClosePending(c,tx); Rebuild(c,tx); return preview;
        });
    }
    private static List<Expense> StoredExpenses(SqliteConnection c,SqliteTransaction? tx)
    {
        var members=Rows(c,tx,"SELECT expense_id,movement_id FROM expense_sources",r=>(r.GetString(0),r.GetString(1))).GroupBy(x=>x.Item1).ToDictionary(g=>g.Key,g=>(IReadOnlyList<string>)g.Select(x=>x.Item2).ToArray());
        return Rows(c,tx,"SELECT id,value_date,description,amount_cents,rule_id FROM expenses ORDER BY value_date DESC,id",r=>new Expense(r.GetString(0),ParseDate(r.GetString(1)),r.GetString(2),r.GetInt64(3),r.IsDBNull(4)?null:r.GetString(4),members.GetValueOrDefault(r.GetString(0))??[]));
    }
    public IReadOnlyList<Expense> Expenses(ExpenseFilter? filter=null)
    {
        using var c=Database.Open(true); using var tx=c.BeginTransaction(deferred:true); RequireCurrent(c,tx);
        var f=filter??new();
        if (f.From>f.To || f.MinCents>f.MaxCents || f.MinCents<0 || f.MaxCents<0) throw new FinanceException("VALIDATION","Intervallo dei filtri non valido.");
        return StoredExpenses(c,tx).Where(e=>(f.From is null||e.Date>=f.From)&&(f.To is null||e.Date<=f.To)
            &&(f.Description is null||e.Description.Contains(f.Description,StringComparison.OrdinalIgnoreCase))
            &&(f.MinCents is null||e.AmountCents>=f.MinCents)&&(f.MaxCents is null||e.AmountCents<=f.MaxCents)).ToList();
    }
    public IReadOnlyList<IncomeAlert> Alerts()
    {
        using var c=Database.Open(true); using var tx=c.BeginTransaction(deferred:true); RequireCurrent(c,tx);
        return Rows(c,tx,"SELECT id,date,description,amount_cents FROM v_active_alerts ORDER BY date DESC,id",r=>new IncomeAlert(r.GetString(0),ParseDate(r.GetString(1)),r.GetString(2),r.GetInt64(3)));
    }
    public IReadOnlyList<MonthlyReport> Report(string? endMonth=null,int count=5)
    {
        if (count<1||count>1200) throw new FinanceException("VALIDATION","Numero di mesi non valido (1–1200).");
        var end=Formats.MonthStart(endMonth??Formats.Month(today())); var expenses=Expenses();
        return Enumerable.Range(0,count).Select(i=>end.AddMonths(-i)).Select(m=>
        {
            var items=expenses.Where(e=>Formats.Month(e.Date)==Formats.Month(m)).ToList();
            return new MonthlyReport(Formats.Month(m),items.Sum(e=>e.AmountCents),items.OrderByDescending(e=>e.AmountCents).ThenBy(e=>e.Id,StringComparer.Ordinal).Take(5).ToList());
        }).ToList();
    }
    public void Edit(string id,DateOnly? date=null,string? description=null,long? amountCents=null)
    {
        if (description is not null&&string.IsNullOrWhiteSpace(description)||amountCents<0) throw new FinanceException("VALIDATION","Descrizione e importo non validi.");
        Write((c,tx)=>
        {
            ClosePending(c,tx); Rebuild(c,tx);
            var expense=StoredExpenses(c,tx).SingleOrDefault(e=>e.Id==id)??throw new FinanceException("NOT_FOUND","Spesa non trovata.");
            if(id.StartsWith("m:",StringComparison.Ordinal))
            {
                Execute(c,tx,"UPDATE manual_expenses SET value_date=$date,description=$description,amount_cents=$amount WHERE id=$id",("$date",Iso(date??expense.Date)),("$description",description??expense.Description),("$amount",amountCents??expense.AmountCents),("$id",id));
                Rebuild(c,tx);return 0;
            }
            var previous=Overrides(c,tx).GetValueOrDefault(id);
            SaveOverride(c,tx,new(id,date??previous?.Date,description??previous?.Description,amountCents??previous?.AmountCents,false,expense.RuleId is null?null:Members(expense.SourceIds)));
            Rebuild(c,tx); return 0;
        });
    }
    private static void SaveOverride(SqliteConnection c,SqliteTransaction tx,ManualOverride o) => Execute(c,tx,
        "INSERT INTO overrides VALUES($id,$date,$description,$amount,$deleted,$members) ON CONFLICT(expense_id) DO UPDATE SET value_date=excluded.value_date,description=excluded.description,amount_cents=excluded.amount_cents,deleted=excluded.deleted,members=excluded.members",
        ("$id",o.Id),("$date",o.Date is null?null:Iso(o.Date.Value)),("$description",o.Description),("$amount",o.AmountCents),("$deleted",o.Deleted?1:0),("$members",o.Members));
    public void Delete(string id) => Write((c,tx)=>
    {
        ClosePending(c,tx); Rebuild(c,tx);
        var expense=StoredExpenses(c,tx).SingleOrDefault(e=>e.Id==id)??throw new FinanceException("NOT_FOUND","Spesa non trovata.");
        if(id.StartsWith("m:",StringComparison.Ordinal))
        {
            Execute(c,tx,"UPDATE manual_expenses SET deleted=1 WHERE id=$id",("$id",id));Rebuild(c,tx);return 0;
        }
        var edits=Overrides(c,tx);
        foreach (var source in expense.SourceIds)
        {
            var key="s:"+source; var previous=edits.GetValueOrDefault(key)??new(key,null,null,null,false,null);
            SaveOverride(c,tx,previous with { Deleted=true });
        }
        if (expense.RuleId is not null) Execute(c,tx,"DELETE FROM overrides WHERE expense_id=$id",("$id",id));
        Rebuild(c,tx); return 0;
    });
    public void Reset(string id) => Write((c,tx)=>
    {
        if (Convert.ToInt32(Scalar(c,tx,"SELECT count(*) FROM overrides WHERE expense_id=$id",("$id",id)))==0) throw new FinanceException("NOT_FOUND","Correzione non trovata.");
        Execute(c,tx,"DELETE FROM overrides WHERE expense_id=$id",("$id",id));
        // During a rule conflict, reset only the override. Applying the rules remains explicit.
        var saved=(string)Scalar(c,tx,"SELECT value FROM metadata WHERE key='rules_version'")!;
        if (saved==RulesVersion) { ClosePending(c,tx); Rebuild(c,tx); }
        return 0;
    },requireRules:false);
    public void HandleAlert(string id) => Write((c,tx)=>
    {
        ClosePending(c,tx); Rebuild(c,tx);
        if (Convert.ToInt32(Scalar(c,tx,"SELECT count(*) FROM alerts WHERE movement_id=$id",("$id",id)))==0) throw new FinanceException("NOT_FOUND","Alert non trovato.");
        Execute(c,tx,"UPDATE alerts SET handled_at=COALESCE(handled_at,$at) WHERE movement_id=$id",("$id",id),("$at",DateTimeOffset.UtcNow.ToString("O"))); return 0;
    });
    public IReadOnlyList<MonthState> Months()
    {
        using var c=Database.Open(true); using var tx=c.BeginTransaction(deferred:true); var sources=Sources(c,tx); var states=States(c,tx);
        return sources.Select(s=>Formats.Month(s.Original.Date)).Concat(states.Keys).Distinct().OrderDescending().Select(m=>new MonthState(m,states.GetValueOrDefault(m)??(string.CompareOrdinal(m,Formats.Month(today()))<0?"pending":"open"))).ToList();
    }
    public void ClosePendingMonths() => Write((c,tx)=> { ClosePending(c,tx); Rebuild(c,tx); return 0; });
    public void SetMonth(string month,bool closed)
    {
        var start=Formats.MonthStart(month);
        if (start>=Formats.MonthStart(Formats.Month(today()))) throw new FinanceException("VALIDATION","Operazione disponibile solo per mesi terminati.");
        Write((c,tx)=>
        {
            if (!Sources(c,tx).Any(s=>Formats.Month(s.Original.Date)==month)) throw new FinanceException("NOT_FOUND","Mese senza movimenti.");
            if (!closed && Overrides(c,tx).Keys.Any(k=>k.StartsWith("g:",StringComparison.Ordinal)&&k.EndsWith(":"+month,StringComparison.Ordinal)))
                throw new FinanceException("OVERRIDE_CONFLICT","Il mese contiene aggregati corretti manualmente: rimuovere prima le correzioni con expenses reset.");
            Execute(c,tx,"INSERT INTO months VALUES($month,$state) ON CONFLICT(month) DO UPDATE SET state=excluded.state",("$month",month),("$state",closed?"closed":"reopened"));
            ClosePending(c,tx); Rebuild(c,tx); return 0;
        });
    }
    private RulesPreview PreviewRules(SqliteConnection c,SqliteTransaction? tx)
    {
        var states=States(c,tx); var sources=Sources(c,tx); foreach (var month in PendingMonths(sources,states)) states[month]="closed";
        var before=StoredExpenses(c,tx).ToDictionary(e=>e.Id); var after=Project(sources,Overrides(c,tx),states).Concat(before.Values.Where(e=>e.Id.StartsWith("m:",StringComparison.Ordinal))).ToDictionary(e=>e.Id);
        var changes=before.Keys.Union(after.Keys).Order().Select(id=>new ExpenseChange(!before.ContainsKey(id)?"added":!after.ContainsKey(id)?"removed":"changed",id,before.GetValueOrDefault(id),after.GetValueOrDefault(id)))
            .Where(x=>x.Before is null||x.After is null||x.Before.Date!=x.After.Date||x.Before.Description!=x.After.Description||x.Before.AmountCents!=x.After.AmountCents||x.Before.RuleId!=x.After.RuleId||Members(x.Before.SourceIds)!=Members(x.After.SourceIds)).ToList();
        var beforeAlerts=Rows(c,tx,"SELECT id,date,description,amount_cents FROM v_active_alerts",r=>new IncomeAlert(r.GetString(0),ParseDate(r.GetString(1)),r.GetString(2),r.GetInt64(3))).ToDictionary(a=>a.Id);
        var handled=Rows(c,tx,"SELECT movement_id FROM alerts WHERE handled_at IS NOT NULL",r=>r.GetString(0)).ToHashSet();
        var suppressed=Cancellations(sources).Select(p=>p.IncomeSourceId).ToHashSet();
        var afterAlerts=sources.Where(s=>s.Original.IncomeCents is <=150000&&!handled.Contains(s.Id)&&!suppressed.Contains(s.Id))
            .Select(s=>new IncomeAlert(s.Id,s.Original.Date,s.Original.Description,s.Original.IncomeCents!.Value)).ToDictionary(a=>a.Id);
        var alertChanges=beforeAlerts.Keys.Union(afterAlerts.Keys).Order().Where(id=>!beforeAlerts.ContainsKey(id)||!afterAlerts.ContainsKey(id))
            .Select(id=>new AlertChange(beforeAlerts.ContainsKey(id)?"removed":"added",id,beforeAlerts.GetValueOrDefault(id),afterAlerts.GetValueOrDefault(id))).ToList();
        return new(Token(c,tx,"rules"),changes,alertChanges);
    }
    public RulesPreview PreviewRules()
    { using var c=Database.Open(true); using var tx=c.BeginTransaction(deferred:true); return PreviewRules(c,tx); }
    public RulesPreview ApplyRules(string? expectedToken=null) => Write((c,tx)=>
    {
        var preview=PreviewRules(c,tx);
        if (expectedToken is not null&&expectedToken!=preview.Token) throw new FinanceException("STALE_PREVIEW","Il database o le regole sono cambiate; ripetere l'anteprima.");
        ClosePending(c,tx); Rebuild(c,tx); return preview;
    },requireRules:false);
    public IReadOnlyList<Dictionary<string,object?>> Query(string sql,IReadOnlyDictionary<string,string>? parameters=null)
    {
        using var c=Database.Open(true); using var tx=c.BeginTransaction(deferred:true); RequireCurrent(c,tx);
        Execute(c,tx,"PRAGMA query_only=ON;");
        // One SELECT or WITH statement, with no writable PRAGMAs or ATTACH. The connection and query_only enforce read-only access.
        var trimmed=sql.Trim().TrimEnd(';');
        if (!System.Text.RegularExpressions.Regex.IsMatch(trimmed,@"^(SELECT|WITH)\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase)||trimmed.Contains(';'))
            throw new FinanceException("QUERY_READ_ONLY","Usare una singola query SELECT o WITH.");
        using var command=Command(c,tx,trimmed);
        foreach (var (key,value) in parameters??new Dictionary<string,string>())
            command.Parameters.AddWithValue(key.StartsWith('$')||key.StartsWith('@')?key:"$"+key,value);
        using var reader=command.ExecuteReader(); var result=new List<Dictionary<string,object?>>();
        while (reader.Read()) { var row=new Dictionary<string,object?>(); for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i); result.Add(row); }
        return result;
    }
    public Expense Add(DateOnly date,string description,long amountCents)
    {
        if(string.IsNullOrWhiteSpace(description)||amountCents<0)throw new FinanceException("VALIDATION","Descrizione e importo non validi.");
        return Write((c,tx)=>
        {
            var expense=new Expense("m:"+Guid.NewGuid().ToString("N"),date,description.Trim(),amountCents,null,[]);
            Execute(c,tx,"INSERT INTO manual_expenses VALUES($id,$date,$description,$amount,0)",("$id",expense.Id),("$date",Iso(date)),("$description",expense.Description),("$amount",amountCents));
            ClosePending(c,tx);Rebuild(c,tx);return expense;
        });
    }
    private IReadOnlyList<MatchedCancellation> Cancellations(IReadOnlyList<SourceMovement> sources)
    {
        var result=new List<MatchedCancellation>();var used=new HashSet<string>();
        foreach(var rule in Rules.OfType<IExpenseBatchRule>())
        {
            var available=sources.Where(s=>!used.Contains(s.Id)).ToList();
            foreach(var pair in rule.Match(available))
            {
                if(!available.Any(s=>s.Id==pair.ExpenseSourceId&&s.Original.ExpenseCents is not null)||!available.Any(s=>s.Id==pair.IncomeSourceId&&s.Original.IncomeCents is not null)
                    ||!used.Add(pair.ExpenseSourceId)||!used.Add(pair.IncomeSourceId))throw new FinanceException("RULES_INVALID","Abbinamento canone/storno non valido.");
                result.Add(pair);
            }
        }
        return result;
    }
    private int CountNewAlerts(IReadOnlyList<SourceMovement> existing,IReadOnlyList<BankMovement> incoming)
    {
        var sequence=existing.LastOrDefault()?.Sequence??0;
        var candidates=incoming.Select((r,i)=>new SourceMovement($"incoming-{i}",sequence+i+1,r)).ToList();
        var suppressed=Cancellations(existing.Concat(candidates).ToList()).Select(p=>p.IncomeSourceId).ToHashSet();
        return candidates.Count(s=>s.Original.IncomeCents is <=150000&&!suppressed.Contains(s.Id));
    }
}

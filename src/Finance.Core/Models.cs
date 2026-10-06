using System.Globalization;

namespace Finance.Core;

public sealed class FinanceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public record BankMovement(int Row, DateOnly Date, long? IncomeCents, long? ExpenseCents, string Description)
{
    public string Signature => System.Text.Json.JsonSerializer.Serialize(new { Date, IncomeCents, ExpenseCents, Description });
}
public record SourceMovement(string Id, long Sequence, BankMovement Original);
public record Expense(string Id, DateOnly Date, string Description, long AmountCents, string? RuleId, IReadOnlyList<string> SourceIds);
public record IncomeAlert(string Id, DateOnly Date, string Description, long AmountCents);
public record ExpenseFilter(DateOnly? From = null, DateOnly? To = null, string? Description = null, long? MinCents = null, long? MaxCents = null);
public record ManualOverride(string Id, DateOnly? Date, string? Description, long? AmountCents, bool Deleted, string? Members);
public record MonthState(string Month, string State);
public record MonthlyReport(string Month, long TotalCents, IReadOnlyList<Expense> TopFive);
public record ImportPreview(string Token, int Movements, int Expenses, int Alerts, int IgnoredIncome, IReadOnlyList<string> ClosingMonths);
public record ExpenseChange(string Kind, string Id, Expense? Before, Expense? After);
public record AlertChange(string Kind, string Id, IncomeAlert? Before, IncomeAlert? After);
public record RulesPreview(string Token, IReadOnlyList<ExpenseChange> Changes, IReadOnlyList<AlertChange>? AlertChanges = null);
public record MatchedCancellation(string RuleId, string ExpenseSourceId, string IncomeSourceId);

public interface IExpenseRule
{
    string Id { get; }
    string Version { get; }
    int Order { get; }
    string Name { get; }
    string Description { get; }
    DateOnly UpdatedOn { get; }
    RuleResult Apply(BankMovement original, RuleResult current);
}
public interface IExpenseBatchRule : IExpenseRule
{
    IReadOnlyList<MatchedCancellation> Match(IReadOnlyList<SourceMovement> movements);
}
public record RuleResult(string Description, bool Excluded = false, string? GroupKey = null, string? GroupLabel = null);

public static class Formats
{
    public static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    public static DateOnly Date(string input) => DateOnly.TryParseExact(input, ["dd/MM/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var value) ? value : throw new FinanceException("VALIDATION", "Data non valida: usare gg/mm/aaaa o yyyy-MM-dd.");
    public static string Month(DateOnly date) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    public static DateOnly MonthStart(string month) => DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var value) ? value : throw new FinanceException("VALIDATION", "Mese non valido: usare yyyy-MM.");
    public static string MonthLabel(DateOnly date) => date.ToString("MMMM yyyy", Italian);
    public static long Money(string input)
    {
        var culture = input.Contains(',') ? Italian : CultureInfo.InvariantCulture;
        if (!decimal.TryParse(input, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, culture, out var value)
            || value < 0 || value * 100 != decimal.Truncate(value * 100) || value > long.MaxValue / 100m)
            throw new FinanceException("VALIDATION", "Importo non valido: usare un valore positivo con al massimo due decimali.");
        return (long)(value * 100);
    }
    public static DateOnly TodayRome() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome")).DateTime);
}

using Finance.Core;

namespace Finance.Rules;

/// <summary>
/// Public build intentionally ships without personal banking rules.
/// Add application-specific rules here or in this assembly.
/// </summary>
public static class RuleRegistry
{
    public static IReadOnlyList<IExpenseRule> Create() => [];
}

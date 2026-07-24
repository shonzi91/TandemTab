// Lives in the FinApp.Kernel assembly (not FinApp.Domain) so the thin WASM client can keep its Money-typed
// display surface after FinApp.Domain is dropped from the bundle (Path B, docs/DOMAIN-REMOVAL.md). The namespace
// stays FinApp.Domain.Common — deliberately unchanged — so the ~79 files that `using` it need no edits; the
// namespace simply spans two assemblies now (Money here, Entity/IPasswordHasher still in Domain). A namespace
// rename to match the assembly is a possible later cleanup, not worth the churn today.
namespace FinApp.Domain.Common;

/// <summary>
/// A monetary amount in a specific currency. Immutable value object.
/// Operations between different currencies are rejected — FinApp keeps one
/// currency per account, so mixing currencies indicates a programming error.
/// </summary>
public readonly record struct Money
{
    public decimal Amount { get; }
    public string Currency { get; }

    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency is required.", nameof(currency));

        Amount = decimal.Round(amount, 2, MidpointRounding.ToEven);
        Currency = currency.ToUpperInvariant();
    }

    public static Money Zero(string currency) => new(0m, currency);

    public bool IsZero => Amount == 0m;
    public bool IsNegative => Amount < 0m;

    public static Money operator +(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return new Money(a.Amount + b.Amount, a.Currency);
    }

    public static Money operator -(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return new Money(a.Amount - b.Amount, a.Currency);
    }

    public static Money operator -(Money a) => new(-a.Amount, a.Currency);

    public static bool operator >(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return a.Amount > b.Amount;
    }

    public static bool operator <(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return a.Amount < b.Amount;
    }

    public static bool operator >=(Money a, Money b) => a > b || a == b;
    public static bool operator <=(Money a, Money b) => a < b || a == b;

    /// <summary>What fraction (0..1+) of <paramref name="of"/> does this amount represent? Returns null when dividing by zero.</summary>
    public decimal? RatioOf(Money of)
    {
        EnsureSameCurrency(this, of);
        if (of.Amount == 0m) return null;
        return Amount / of.Amount;
    }

    private static void EnsureSameCurrency(Money a, Money b)
    {
        if (a.Currency != b.Currency)
            throw new InvalidOperationException($"Currency mismatch: {a.Currency} vs {b.Currency}.");
    }

    public override string ToString() => $"{Amount:0.00} {Currency}";
}

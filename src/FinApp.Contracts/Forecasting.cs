namespace FinApp.Contracts;

/// <summary>A loan/debt tracked for the Forecasts tab only — projections & simulations. It is NOT part of the money
/// model (funds/budgets/savings): it lives in its own store and never affects the account's actual balances.</summary>
public record LoanDto(Guid Id, string Name, decimal Balance, decimal AnnualRatePercent, decimal MinPayment, string Currency);

/// <summary>Create or update a loan (the server sets/keeps the id and currency).</summary>
public record SaveLoanRequest(string Name, decimal Balance, decimal AnnualRatePercent, decimal MinPayment);

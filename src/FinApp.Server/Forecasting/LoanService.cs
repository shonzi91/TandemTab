using System.Data;
using System.Globalization;
using FinApp.Contracts;
using FinApp.Persistence;
using FinApp.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Forecasting;

/// <summary>
/// Stores loans/debts for the <b>Forecasts</b> tab. Deliberately a standalone table (the same migration-free
/// CREATE-TABLE-IF-NOT-EXISTS pattern as <see cref="Auth.ConsentService"/>), completely separate from the account
/// snapshot and the money model: loans are forecast/simulation inputs only and never touch balances. Keyed by
/// account, contributor-gated, so everyone sharing an account sees the same loans.
/// </summary>
public sealed class LoanService(FinAppDbContext db)
{
    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"Loans\" (" +
            "\"Id\" text PRIMARY KEY, \"AccountId\" text NOT NULL, \"Name\" text NOT NULL, " +
            "\"Balance\" text NOT NULL, \"AnnualRate\" text NOT NULL, \"MinPayment\" text NOT NULL, " +
            "\"Currency\" text NOT NULL, \"CreatedAt\" text NOT NULL)", ct);

    public async Task<List<LoanDto>> ListAsync(Guid userId, Guid accountId, CancellationToken ct = default)
    {
        var currency = await EnsureContributorAsync(userId, accountId, ct);
        var result = new List<LoanDto>();
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"Id\", \"Name\", \"Balance\", \"AnnualRate\", \"MinPayment\", \"Currency\" " +
                              "FROM \"Loans\" WHERE \"AccountId\" = @acc ORDER BY \"CreatedAt\"";
            AddParam(cmd, "@acc", accountId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new LoanDto(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                    Dec(reader.GetString(2)), Dec(reader.GetString(3)), Dec(reader.GetString(4)), reader.GetString(5)));
        }
        finally { if (opened) await conn.CloseAsync(); }
        _ = currency;
        return result;
    }

    public async Task<LoanDto> AddAsync(Guid userId, Guid accountId, SaveLoanRequest req, CancellationToken ct = default)
    {
        var currency = await EnsureContributorAsync(userId, accountId, ct);
        Validate(req);
        var id = Guid.NewGuid();
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO \"Loans\" (\"Id\", \"AccountId\", \"Name\", \"Balance\", \"AnnualRate\", \"MinPayment\", \"Currency\", \"CreatedAt\") " +
                "VALUES (@id, @acc, @name, @bal, @rate, @pay, @cur, @at)";
            AddParam(cmd, "@id", id.ToString());
            AddParam(cmd, "@acc", accountId.ToString());
            AddParam(cmd, "@name", req.Name.Trim());
            AddParam(cmd, "@bal", Str(req.Balance));
            AddParam(cmd, "@rate", Str(req.AnnualRatePercent));
            AddParam(cmd, "@pay", Str(req.MinPayment));
            AddParam(cmd, "@cur", currency);
            AddParam(cmd, "@at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
        return new LoanDto(id, req.Name.Trim(), req.Balance, req.AnnualRatePercent, req.MinPayment, currency);
    }

    public async Task UpdateAsync(Guid userId, Guid accountId, Guid loanId, SaveLoanRequest req, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        Validate(req);
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "UPDATE \"Loans\" SET \"Name\" = @name, \"Balance\" = @bal, \"AnnualRate\" = @rate, \"MinPayment\" = @pay " +
                "WHERE \"Id\" = @id AND \"AccountId\" = @acc";
            AddParam(cmd, "@name", req.Name.Trim());
            AddParam(cmd, "@bal", Str(req.Balance));
            AddParam(cmd, "@rate", Str(req.AnnualRatePercent));
            AddParam(cmd, "@pay", Str(req.MinPayment));
            AddParam(cmd, "@id", loanId.ToString());
            AddParam(cmd, "@acc", accountId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    public async Task RemoveAsync(Guid userId, Guid accountId, Guid loanId, CancellationToken ct = default)
    {
        await EnsureContributorAsync(userId, accountId, ct);
        var conn = db.Database.GetDbConnection();
        var opened = await OpenAsync(conn, ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM \"Loans\" WHERE \"Id\" = @id AND \"AccountId\" = @acc";
            AddParam(cmd, "@id", loanId.ToString());
            AddParam(cmd, "@acc", accountId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { if (opened) await conn.CloseAsync(); }
    }

    private static void Validate(SaveLoanRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new BadRequestException("A loan needs a name.");
        if (req.Balance < 0m || req.MinPayment < 0m || req.AnnualRatePercent < 0m)
            throw new BadRequestException("Loan figures can't be negative.");
    }

    /// <summary>Ensure the caller is a member of the account; returns the account currency for new loans.</summary>
    private async Task<string> EnsureContributorAsync(Guid userId, Guid accountId, CancellationToken ct)
    {
        var account = await db.Accounts.Include(a => a.Members).FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null || !account.IsContributor(userId))
            throw new NotFoundException("Account not found.");
        return account.Currency;
    }

    private static decimal Dec(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);
    private static string Str(decimal d) => d.ToString(CultureInfo.InvariantCulture);

    private static async Task<bool> OpenAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        if (conn.State == ConnectionState.Open) return false;
        await conn.OpenAsync(ct);
        return true;
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}

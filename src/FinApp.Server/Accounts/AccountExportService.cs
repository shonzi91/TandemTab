using ClosedXML.Excel;
using FinApp.Contracts;
using FinApp.Domain.Accounts;
using FinApp.Domain.Periods;
using FinApp.Persistence;
using FinApp.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FinApp.Server.Accounts;

/// <summary>
/// Builds an .xlsx export of a whole account: an "Account" overview sheet plus one sheet per period
/// (opening balances, contributions, budgets, expenses, savings, transfers). Read-only — it deserializes
/// the stored snapshot (via <see cref="AccountSnapshotSerializer"/>) and renders it; nothing is mutated.
/// </summary>
public sealed class AccountExportService(FinAppDbContext db, ISnapshotCipher cipher)
{
    public async Task<(byte[] Bytes, string FileName)> ExportAsync(Guid userId, Guid accountId, CancellationToken ct = default)
    {
        var header = await db.Accounts.Include(a => a.Members).FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (header is null || !header.IsContributor(userId))
            throw new NotFoundException("Account not found.");

        var row = await db.AccountSnapshots.FindAsync([accountId], ct);
        if (row is null || string.IsNullOrEmpty(row.Payload))
            throw new NotFoundException("This account has no data to export yet.");

        var account = AccountSnapshotSerializer.Deserialize(await cipher.UnprotectAsync(row.Payload, ct));

        string Member(Guid id) => account.Members.FirstOrDefault(m => m.UserId == id)?.DisplayName ?? "—";
        string Category(Guid id) => account.FindCategory(id)?.Name ?? "—";
        string Fund(Guid id) => account.FundName(id);
        string Saving(Guid id) => account.FindSavingCategory(id)?.Name ?? "—";
        string ContribCat(Guid id) => account.FindContributionCategory(id)?.Name ?? "—";

        using var wb = new XLWorkbook();
        BuildOverview(wb, account);

        var index = 0;
        foreach (var p in account.Periods)
            BuildPeriod(wb, $"{++index:00} {p.From:yyyy-MM}", p, account.Currency, Member, Category, Fund, Saving, ContribCat);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var fileName = $"{Sanitize(account.Name)}-{DateTime.UtcNow:yyyyMMdd}.xlsx";
        return (ms.ToArray(), fileName);
    }

    private static void BuildOverview(XLWorkbook wb, Account account)
    {
        var ws = wb.AddWorksheet("Account");
        var r = 1;
        Title(ws, ref r, account.Name);
        ws.Cell(r++, 1).Value = $"Currency: {account.Currency}";
        ws.Cell(r++, 1).Value = $"Periods: {account.Periods.Count}";
        r++;

        Section(ws, ref r, "Funds");
        foreach (var f in account.Funds) ws.Cell(r++, 1).Value = f.Name;
        r++;

        Section(ws, ref r, "Categories");
        foreach (var c in account.Categories) ws.Cell(r++, 1).Value = c.Name;
        r++;

        Section(ws, ref r, "Savings buckets");
        foreach (var s in account.SavingCategories) ws.Cell(r++, 1).Value = s.Name;
        r++;

        Section(ws, ref r, "Members");
        foreach (var m in account.Members) ws.Cell(r++, 1).Value = m.DisplayName;

        ws.Column(1).Width = 28;
    }

    private static void BuildPeriod(XLWorkbook wb, string sheetName, Period p, string currency,
        Func<Guid, string> member, Func<Guid, string> category, Func<Guid, string> fund,
        Func<Guid, string> saving, Func<Guid, string> contribCat)
    {
        var ws = wb.AddWorksheet(sheetName);
        var r = 1;
        Title(ws, ref r, $"{p.From:dd MMM yyyy} – {p.To:dd MMM yyyy}  ({p.Status})");
        r++;

        Section(ws, ref r, "Opening balances");
        Headers(ws, ref r, "Fund", "Amount");
        foreach (var b in p.InitialBalances)
        {
            ws.Cell(r, 1).Value = fund(b.FundId) + (b.Informative ? " (sub-fund)" : "");
            Money(ws.Cell(r, 2), b.Amount.Amount);
            r++;
        }
        r++;

        Section(ws, ref r, "Contributions");
        Headers(ws, ref r, "Date", "Member", "Category", "Fund", "Amount");
        foreach (var c in p.Contributions)
        {
            ws.Cell(r, 1).Value = c.Date.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Cell(r, 2).Value = member(c.MemberId);
            ws.Cell(r, 3).Value = contribCat(c.CategoryId);
            ws.Cell(r, 4).Value = fund(c.FundId);
            Money(ws.Cell(r, 5), c.Paid.Amount);
            r++;
        }
        r++;

        Section(ws, ref r, "Budgets");
        Headers(ws, ref r, "Category", "Budgeted", "Spent", "Remaining");
        foreach (var b in p.Budgets)
        {
            var spent = p.Expenses.Where(e => e.CategoryId == b.CategoryId).Sum(e => e.Amount.Amount);
            ws.Cell(r, 1).Value = category(b.CategoryId);
            Money(ws.Cell(r, 2), b.Allocated.Amount);
            Money(ws.Cell(r, 3), spent);
            Money(ws.Cell(r, 4), b.Allocated.Amount - spent);
            r++;
        }
        r++;

        Section(ws, ref r, "Expenses");
        Headers(ws, ref r, "Date", "Category", "Fund", "Amount", "Note");
        foreach (var e in p.Expenses.OrderBy(e => e.Date))
        {
            ws.Cell(r, 1).Value = e.Date.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Cell(r, 2).Value = category(e.CategoryId);
            ws.Cell(r, 3).Value = fund(e.FundId);
            Money(ws.Cell(r, 4), e.Amount.Amount);
            ws.Cell(r, 5).Value = e.IsFromSavings ? $"from savings · {e.Note}" : e.Note;
            r++;
        }
        r++;

        Section(ws, ref r, "Goals activity");
        Headers(ws, ref r, "Date", "Bucket", "Amount", "Note");
        foreach (var a in p.SavingAllocations.OrderBy(a => a.Date))
        {
            ws.Cell(r, 1).Value = a.Date.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Cell(r, 2).Value = saving(a.SavingCategoryId);
            Money(ws.Cell(r, 3), a.Amount.Amount);
            ws.Cell(r, 4).Value = a.Note;
            r++;
        }
        r++;

        Section(ws, ref r, "Fund transfers");
        Headers(ws, ref r, "Date", "From", "To", "Amount", "Note");
        foreach (var t in p.FundTransfers.OrderBy(t => t.Date))
        {
            ws.Cell(r, 1).Value = t.Date.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Cell(r, 2).Value = fund(t.FromFundId);
            ws.Cell(r, 3).Value = fund(t.ToFundId);
            Money(ws.Cell(r, 4), t.Amount.Amount);
            ws.Cell(r, 5).Value = t.Note;
            r++;
        }
        foreach (var t in p.ExternalTransfers.OrderBy(t => t.Date))
        {
            ws.Cell(r, 1).Value = t.Date.ToDateTime(TimeOnly.MinValue);
            ws.Cell(r, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Cell(r, 2).Value = fund(t.FundId);
            ws.Cell(r, 3).Value = "→ another account";
            Money(ws.Cell(r, 4), t.Amount.Amount);
            ws.Cell(r, 5).Value = t.Note;
            r++;
        }
        r++;

        Section(ws, ref r, "Summary");
        SummaryRow(ws, ref r, "Opening total", p.InitialTotal.Amount);
        SummaryRow(ws, ref r, "Contributions", p.ContributionsPaidTotal.Amount);
        SummaryRow(ws, ref r, "Spent", p.ExpensesTotal.Amount);
        SummaryRow(ws, ref r, "Saved (net)", p.SavingsNetTotal.Amount);
        SummaryRow(ws, ref r, "Closing balance", p.ExpectedClosingBalance.Amount);

        for (var c = 1; c <= 5; c++) ws.Column(c).Width = c == 1 ? 16 : 18;
    }

    // --- small layout helpers ---------------------------------------------
    private static void Title(IXLWorksheet ws, ref int r, string text)
    {
        var cell = ws.Cell(r, 1);
        cell.Value = text;
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontSize = 14;
        r++;
    }

    private static void Section(IXLWorksheet ws, ref int r, string text)
    {
        var cell = ws.Cell(r, 1);
        cell.Value = text;
        cell.Style.Font.Bold = true;
        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF0FB");
        r++;
    }

    private static void Headers(IXLWorksheet ws, ref int r, params string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(r, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Italic = true;
            cell.Style.Font.FontColor = XLColor.FromHtml("#6B7280");
        }
        r++;
    }

    private static void Money(IXLCell cell, decimal amount)
    {
        cell.Value = (double)amount;
        cell.Style.NumberFormat.Format = "#,##0.00";
    }

    private static void SummaryRow(IXLWorksheet ws, ref int r, string label, decimal amount)
    {
        ws.Cell(r, 1).Value = label;
        ws.Cell(r, 1).Style.Font.Bold = true;
        Money(ws.Cell(r, 2), amount);
        r++;
    }

    private static string Sanitize(string name)
    {
        var clean = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "account" : clean;
    }
}

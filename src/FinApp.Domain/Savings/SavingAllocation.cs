using FinApp.Domain.Common;

namespace FinApp.Domain.Savings;

/// <summary>
/// Money set aside into a savings bucket during a period. Positive amounts add to savings;
/// a saving→expense conversion records a matching negative drawdown so the running balance stays correct.
/// </summary>
public sealed class SavingAllocation : Entity
{
    public Guid SavingCategoryId { get; }
    public Money Amount { get; }
    public DateOnly Date { get; }
    public string? Note { get; }

    /// <summary>When this allocation is the drawdown half of a saving→expense conversion, the id of that expense — so removing/editing the expense can cancel the matching drawdown exactly.</summary>
    public Guid? SourceExpenseId { get; }

    /// <summary>When this is a "move savings to a budget" drawdown, the category whose budget it funded — so the move can be reversed (the budget reduced) on remove/edit.</summary>
    public Guid? BudgetCategoryId { get; }

    /// <summary>Shared id linking the two halves (out + in) of a bucket-to-bucket transfer, so the pair can be removed/edited as one movement.</summary>
    public Guid? TransferPairId { get; }

    /// <summary>When this drawdown is a <b>disbursement</b> — the saving deployed to its goal (e.g. a loan prepayment)
    /// rather than consumed — the id of the external-transfer-out that moved the money. Its presence marks the
    /// drawdown as "successfully used", so it drains the earmark but is <i>not</i> counted as un-saving in the rate.
    /// Set post-construction (like <see cref="Expense.FundSynced"/>) so it rides in the snapshot without an EF column.</summary>
    public Guid? SourceExternalTransferId { get; private set; }

    public SavingAllocation(Guid savingCategoryId, Money amount, DateOnly date, string? note = null,
        Guid? sourceExpenseId = null, Guid? budgetCategoryId = null, Guid? transferPairId = null)
    {
        SavingCategoryId = savingCategoryId;
        Amount = amount;
        Date = date;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        SourceExpenseId = sourceExpenseId;
        BudgetCategoryId = budgetCategoryId;
        TransferPairId = transferPairId;
    }

    /// <summary>Mark this drawdown as the disbursement half of a saving deployed to a goal via the given transfer.</summary>
    public void MarkDisbursement(Guid externalTransferId) => SourceExternalTransferId = externalTransferId;

    /// <summary>True when this is the drawdown half of a saving disbursement (deployed to a goal, not consumed).</summary>
    public bool IsDisbursement => SourceExternalTransferId is not null;
}

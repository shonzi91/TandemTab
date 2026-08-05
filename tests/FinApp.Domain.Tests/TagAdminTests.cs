using FinApp.Domain.Accounts;
using Xunit;

namespace FinApp.Domain.Tests;

public class TagAdminTests
{
    private const string Eur = "EUR";

    [Fact]
    public void Add_tag_sets_name_and_icon_and_is_found()
    {
        var account = new Account("Personal", Eur);
        var vacation = account.AddTag("Vacation", "🏖️");

        Assert.Equal("Vacation", vacation.Name);
        Assert.Equal("🏖️", vacation.Icon);
        Assert.False(vacation.IsArchived);
        Assert.Same(vacation, account.FindTag(vacation.Id));
        Assert.Single(account.Tags);
    }

    [Fact]
    public void Duplicate_tag_name_is_rejected_case_insensitively()
    {
        var account = new Account("Personal", Eur);
        account.AddTag("Work");
        Assert.Throws<InvalidOperationException>(() => account.AddTag("  work "));
    }

    [Fact]
    public void Rename_rejects_a_clashing_name_but_allows_a_free_one()
    {
        var account = new Account("Personal", Eur);
        var a = account.AddTag("Work");
        var b = account.AddTag("Personal-spend");

        Assert.Throws<InvalidOperationException>(() => account.RenameTag(b.Id, "work"));
        account.RenameTag(b.Id, "Reimbursable");
        Assert.Equal("Reimbursable", account.FindTag(b.Id)!.Name);
        // Renaming a tag to its own (trimmed) name is fine.
        account.RenameTag(a.Id, " Work ");
        Assert.Equal("Work", a.Name);
    }

    [Fact]
    public void Icon_can_be_set_and_cleared()
    {
        var account = new Account("Personal", Eur);
        var t = account.AddTag("Work");
        Assert.Null(t.Icon);
        account.SetTagIcon(t.Id, "💼");
        Assert.Equal("💼", t.Icon);
        account.SetTagIcon(t.Id, "  ");   // blank clears it
        Assert.Null(t.Icon);
    }

    [Fact]
    public void Archived_tags_drop_out_of_ActiveTags_but_stay_in_Tags()
    {
        var account = new Account("Personal", Eur);
        var t = account.AddTag("Old");
        account.SetTagArchived(t.Id, true);

        Assert.True(t.IsArchived);
        Assert.Empty(account.ActiveTags);
        Assert.Single(account.Tags);

        account.SetTagArchived(t.Id, false);
        Assert.Single(account.ActiveTags);
    }

    [Fact]
    public void Remove_tag_deletes_the_definition()
    {
        var account = new Account("Personal", Eur);
        var t = account.AddTag("Temp");
        account.RemoveTag(t.Id);
        Assert.Empty(account.Tags);
        Assert.Null(account.FindTag(t.Id));
    }

    [Fact]
    public void Tags_survive_a_snapshot_round_trip()
    {
        var account = new Account("Personal", Eur);
        var vac = account.AddTag("Vacation", "🏖️");
        var work = account.AddTag("Work", "💼");
        account.SetTagArchived(work.Id, true);

        var json = AccountSnapshotSerializer.Serialize(account);
        var restored = AccountSnapshotSerializer.Deserialize(json);

        Assert.Equal(2, restored.Tags.Count);
        var rVac = restored.FindTag(vac.Id)!;
        Assert.Equal("Vacation", rVac.Name);
        Assert.Equal("🏖️", rVac.Icon);
        Assert.False(rVac.IsArchived);
        var rWork = restored.FindTag(work.Id)!;
        Assert.Equal("💼", rWork.Icon);
        Assert.True(rWork.IsArchived);
    }

    [Fact]
    public void An_account_with_no_tags_round_trips_to_an_empty_tag_list()
    {
        var account = new Account("Personal", Eur);
        account.AddCategory("Food");

        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));
        Assert.Empty(restored.Tags);
    }

    // --- F2: tag → category binding -------------------------------------------------------------

    [Fact]
    public void A_tag_is_unbound_until_a_category_is_set_and_can_be_cleared_again()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var lidl = account.AddTag("lidl");

        Assert.Null(lidl.CategoryId);

        account.SetTagCategory(lidl.Id, food.Id);
        Assert.Equal(food.Id, lidl.CategoryId);

        account.SetTagCategory(lidl.Id, null);
        Assert.Null(lidl.CategoryId);
    }

    [Fact]
    public void Binding_a_tag_to_a_category_that_is_not_in_this_account_is_rejected()
    {
        var account = new Account("Personal", Eur);
        var tag = account.AddTag("lidl");
        var foreignCategory = new Account("Other", Eur).AddCategory("Food");

        Assert.Throws<InvalidOperationException>(() => account.SetTagCategory(tag.Id, foreignCategory.Id));
        Assert.Null(tag.CategoryId);
    }

    [Fact]
    public void Removing_the_bound_category_clears_the_binding_rather_than_leaving_it_dangling()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var lidl = account.AddTag("lidl");
        account.SetTagCategory(lidl.Id, food.Id);

        account.RemoveCategory(food.Id);   // allowed: nothing references it

        Assert.Null(lidl.CategoryId);
    }

    [Fact]
    public void A_tag_binding_survives_a_snapshot_round_trip_and_legacy_tags_restore_unbound()
    {
        var account = new Account("Personal", Eur);
        var food = account.AddCategory("Food");
        var lidl = account.AddTag("lidl");
        var work = account.AddTag("Work");
        account.SetTagCategory(lidl.Id, food.Id);

        var restored = AccountSnapshotSerializer.Deserialize(AccountSnapshotSerializer.Serialize(account));

        Assert.Equal(food.Id, restored.FindTag(lidl.Id)!.CategoryId);
        Assert.Null(restored.FindTag(work.Id)!.CategoryId);
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinApp.Persistence.Migrations
{
    /// <summary>
    /// Marks AccountSnapshots.Version as an optimistic-concurrency token (see FinAppDbContext). This changes only how
    /// EF generates the UPDATE (it adds "AND Version = &lt;original&gt;"), not the physical schema — so Up/Down are
    /// intentionally empty. The migration exists purely to keep the model snapshot in sync, so a later, unrelated
    /// migration doesn't silently pick up this annotation.
    /// </summary>
    public partial class AddSnapshotVersionConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No schema change — concurrency-token is a model-only annotation (see class summary).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No schema change — concurrency-token is a model-only annotation (see class summary).
        }
    }
}

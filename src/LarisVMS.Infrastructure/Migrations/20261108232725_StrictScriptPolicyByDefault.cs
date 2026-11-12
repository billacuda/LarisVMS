using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StrictScriptPolicyByDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The strict script policy is now on unless turned off (a missing setting means on). An
            // upgraded server that stored "off" gets it turned on once here; Settings -> Security can
            // turn it off again if something stops working.
            migrationBuilder.Sql("UPDATE Settings SET [Value] = 'True' WHERE [Key] = 'Security.StrictScriptPolicy';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}

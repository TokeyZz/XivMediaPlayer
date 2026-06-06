using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XivMediaPlayer.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDurationMs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DurationMs",
                table: "RoomMediaStates",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "RoomMediaStates");
        }
    }
}

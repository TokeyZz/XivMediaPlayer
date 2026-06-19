using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XivMediaPlayer.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoomStates",
                columns: table => new
                {
                    LocationKey = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentUrl = table.Column<string>(type: "TEXT", nullable: false),
                    TimecodeMs = table.Column<long>(type: "INTEGER", nullable: false),
                    IsPlaying = table.Column<bool>(type: "INTEGER", nullable: false),
                    SpeedRate = table.Column<float>(type: "REAL", nullable: false),
                    QueueJson = table.Column<string>(type: "TEXT", nullable: false),
                    DjOwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    DjHeartbeatUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StateVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomStates", x => x.LocationKey);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoomStates");
        }
    }
}

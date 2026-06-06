using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XivMediaPlayer.Server.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoomMediaStates",
                columns: table => new
                {
                    LocationKey = table.Column<string>(type: "TEXT", nullable: false),
                    CurrentUrl = table.Column<string>(type: "TEXT", nullable: false),
                    TimecodeMs = table.Column<long>(type: "INTEGER", nullable: false),
                    IsPlaying = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PlaylistJson = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomMediaStates", x => x.LocationKey);
                });

            migrationBuilder.CreateTable(
                name: "TvPlacements",
                columns: table => new
                {
                    LocationKey = table.Column<string>(type: "TEXT", nullable: false),
                    Id = table.Column<string>(type: "TEXT", nullable: true),
                    PositionX = table.Column<float>(type: "REAL", nullable: false),
                    PositionY = table.Column<float>(type: "REAL", nullable: false),
                    PositionZ = table.Column<float>(type: "REAL", nullable: false),
                    RotationX = table.Column<float>(type: "REAL", nullable: false),
                    RotationY = table.Column<float>(type: "REAL", nullable: false),
                    RotationZ = table.Column<float>(type: "REAL", nullable: false),
                    ScaleX = table.Column<float>(type: "REAL", nullable: false),
                    ScaleY = table.Column<float>(type: "REAL", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    IsLocked = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TvPlacements", x => x.LocationKey);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoomMediaStates");

            migrationBuilder.DropTable(
                name: "TvPlacements");
        }
    }
}

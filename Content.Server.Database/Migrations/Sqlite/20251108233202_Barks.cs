// SPDX-FileCopyrightText: 2025 Cinkafox <70429757+Cinkafox@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 ReserveBot <211949879+ReserveBot@users.noreply.github.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Content.Server.Database.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class FixBarkColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "body_type",
                table: "profile",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "voice",
                table: "profile",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bark_voice",
                table: "profile",
                type: "TEXT",
                nullable: false,
                defaultValue: "Txt1");

            migrationBuilder.AddColumn<byte>(
                name: "bark_pause",
                table: "profile",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)127);

            migrationBuilder.AddColumn<byte>(
                name: "bark_volume",
                table: "profile",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)255);

            migrationBuilder.AddColumn<byte>(
                name: "bark_pitch",
                table: "profile",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)127);

            migrationBuilder.AddColumn<byte>(
                name: "bark_pitch_variance",
                table: "profile",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)127);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "body_type",
                table: "profile");

            migrationBuilder.DropColumn(
                name: "voice",
                table: "profile");

            migrationBuilder.DropColumn(
                name: "bark_voice",
                table: "profile");

            migrationBuilder.DropColumn(
                name: "bark_pause",
                table: "profile");

            migrationBuilder.DropColumn(
                name: "bark_volume",
                table: "profile");

            migrationBuilder.DropColumn(
                name: "bark_pitch",
                table: "profile");

            migrationBuilder.DropColumn(
                name: "bark_pitch_variance",
                table: "profile");
        }
    }
}

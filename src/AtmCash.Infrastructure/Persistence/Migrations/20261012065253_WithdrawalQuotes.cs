using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AtmCash.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WithdrawalQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "withdrawal_quotes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    atm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    card_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    requested_amount = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_option = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    dispensed_amount = table.Column<int>(type: "integer", nullable: true),
                    held_amount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_withdrawal_quotes", x => x.id);
                    table.ForeignKey(
                        name: "fk_withdrawal_quotes_atms_atm_id",
                        column: x => x.atm_id,
                        principalTable: "atms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    response = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => x.key);
                    table.ForeignKey(
                        name: "fk_idempotency_records_quotes_quote_id",
                        column: x => x.quote_id,
                        principalTable: "withdrawal_quotes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "withdrawal_quote_offers",
                columns: table => new
                {
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    option = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    denomination = table.Column<int>(type: "integer", nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_withdrawal_quote_offers", x => new { x.quote_id, x.id });
                    table.ForeignKey(
                        name: "fk_withdrawal_quote_offers_withdrawal_quotes_quote_id",
                        column: x => x.quote_id,
                        principalTable: "withdrawal_quotes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "withdrawal_quote_reservations",
                columns: table => new
                {
                    quote_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    order = table.Column<int>(type: "integer", nullable: false),
                    slot = table.Column<int>(type: "integer", nullable: false),
                    denomination = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_withdrawal_quote_reservations", x => new { x.quote_id, x.id });
                    table.ForeignKey(
                        name: "fk_withdrawal_quote_reservations_withdrawal_quotes_quote_id",
                        column: x => x.quote_id,
                        principalTable: "withdrawal_quotes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_quote_id",
                table: "idempotency_records",
                column: "quote_id");

            migrationBuilder.CreateIndex(
                name: "ix_withdrawal_quotes_atm_id_status",
                table: "withdrawal_quotes",
                columns: new[] { "atm_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_withdrawal_quotes_card_hash_currency_status",
                table: "withdrawal_quotes",
                columns: new[] { "card_hash", "currency", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_withdrawal_quotes_status_expires_at",
                table: "withdrawal_quotes",
                columns: new[] { "status", "expires_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "withdrawal_quote_offers");

            migrationBuilder.DropTable(
                name: "withdrawal_quote_reservations");

            migrationBuilder.DropTable(
                name: "withdrawal_quotes");
        }
    }
}

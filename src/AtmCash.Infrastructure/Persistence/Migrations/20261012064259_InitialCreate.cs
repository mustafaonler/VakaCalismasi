using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AtmCash.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "atms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_atms", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cassettes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    atm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slot = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    denomination = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cassettes", x => x.id);
                    table.CheckConstraint("ck_cassettes_slot", "slot BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_cassettes_atms_atm_id",
                        column: x => x.atm_id,
                        principalTable: "atms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cassette_holdings",
                columns: table => new
                {
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    denomination = table.Column<int>(type: "integer", nullable: false),
                    cassette_id = table.Column<Guid>(type: "uuid", nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    reserved = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cassette_holdings", x => new { x.cassette_id, x.currency, x.denomination });
                    table.CheckConstraint("ck_cassette_holdings_count", "count BETWEEN 0 AND 100");
                    table.CheckConstraint("ck_cassette_holdings_reserved", "reserved BETWEEN 0 AND count");
                    table.ForeignKey(
                        name: "fk_cassette_holdings_cassettes_cassette_id",
                        column: x => x.cassette_id,
                        principalTable: "cassettes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cassette_movements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    atm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cassette_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    denomination = table.Column<int>(type: "integer", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cassette_movements", x => x.id);
                    table.ForeignKey(
                        name: "fk_cassette_movements_atms_atm_id",
                        column: x => x.atm_id,
                        principalTable: "atms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cassette_movements_cassettes_cassette_id",
                        column: x => x.cassette_id,
                        principalTable: "cassettes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_atms_code",
                table: "atms",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cassette_movements_atm_id",
                table: "cassette_movements",
                column: "atm_id");

            migrationBuilder.CreateIndex(
                name: "ix_cassette_movements_cassette_id",
                table: "cassette_movements",
                column: "cassette_id");

            migrationBuilder.CreateIndex(
                name: "ix_cassette_movements_correlation_id",
                table: "cassette_movements",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_cassettes_atm_id_slot",
                table: "cassettes",
                columns: new[] { "atm_id", "slot" },
                unique: true);

            // The movement ledger is append-only (B8): reject UPDATE and DELETE at the database level.
            migrationBuilder.Sql("""
                CREATE FUNCTION cassette_movements_immutable() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'cassette_movements is append-only';
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_cassette_movements_immutable
                    BEFORE UPDATE OR DELETE ON cassette_movements
                    FOR EACH ROW EXECUTE FUNCTION cassette_movements_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS cassette_movements_immutable() CASCADE;");

            migrationBuilder.DropTable(
                name: "cassette_holdings");

            migrationBuilder.DropTable(
                name: "cassette_movements");

            migrationBuilder.DropTable(
                name: "cassettes");

            migrationBuilder.DropTable(
                name: "atms");
        }
    }
}

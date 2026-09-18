using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ClaimsEngine.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    Subject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_keys", x => new { x.Subject, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "policies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CoverageType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CoverageLimit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Deductible = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LifetimePaid = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policies", x => x.Id);
                    table.CheckConstraint("ck_policies_coverage_limit_positive", "\"CoverageLimit\" > 0");
                    table.CheckConstraint("ck_policies_deductible_below_limit", "\"Deductible\" >= 0 AND \"Deductible\" < \"CoverageLimit\"");
                    table.CheckConstraint("ck_policies_period", "\"EffectiveTo\" > \"EffectiveFrom\"");
                });

            migrationBuilder.CreateTable(
                name: "claims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FiledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClaimedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EligiblePayout = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ApprovedPayout = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    Flag = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ReviewStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_claims", x => x.Id);
                    table.CheckConstraint("ck_claims_claimed_amount_positive", "\"ClaimedAmount\" > 0");
                    table.CheckConstraint("ck_claims_eligible_payout_non_negative", "\"EligiblePayout\" >= 0");
                    table.ForeignKey(
                        name: "FK_claims_policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "claim_history",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorRole = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_claim_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_claim_history_claims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_claim_history_ClaimId_At",
                table: "claim_history",
                columns: new[] { "ClaimId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_claims_FiledAt",
                table: "claims",
                column: "FiledAt");

            migrationBuilder.CreateIndex(
                name: "IX_claims_PolicyId_Status",
                table: "claims",
                columns: new[] { "PolicyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_claims_Status_ReviewStartedAt",
                table: "claims",
                columns: new[] { "Status", "ReviewStartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_keys_CreatedAt",
                table: "idempotency_keys",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_policies_HolderId",
                table: "policies",
                column: "HolderId");

            migrationBuilder.CreateIndex(
                name: "IX_policies_PolicyNumber",
                table: "policies",
                column: "PolicyNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "claim_history");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "claims");

            migrationBuilder.DropTable(
                name: "policies");
        }
    }
}

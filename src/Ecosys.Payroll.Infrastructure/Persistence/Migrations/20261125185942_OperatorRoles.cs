using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecosys.Payroll.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperatorRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Operator roles now grant only operator-console permissions, and only in the platform organisation.
            // Anyone given the old platform role inside a customer organisation keeps their access there as that
            // organisation's administrator, and loses the operator role. Their sessions end so the change applies at once.
            migrationBuilder.Sql("""
                UPDATE users u
                SET roles = (SELECT COALESCE(jsonb_agg(r), '[]'::jsonb) FROM jsonb_array_elements_text(u.roles) r
                             WHERE r NOT IN ('PlatformAdministrator', 'PlatformOperations', 'PlatformFinance'))
                         || CASE WHEN u.roles ? 'TenantAdministrator' THEN '[]'::jsonb ELSE '["TenantAdministrator"]'::jsonb END,
                    security_stamp = gen_random_uuid()
                WHERE (u.roles ?| ARRAY['PlatformAdministrator', 'PlatformOperations', 'PlatformFinance'])
                  AND u.tenant_id NOT IN (SELECT id FROM tenants WHERE slug = 'platform');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data change only; the previous roles are not recorded, so there is nothing to restore.
        }
    }
}

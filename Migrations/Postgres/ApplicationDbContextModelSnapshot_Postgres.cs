// Auto-generated snapshot for PostgreSQL baseline migration
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SentinelMfgSuite.Web.Data;

#nullable disable

namespace SentinelMfgSuite.Web.Migrations.Postgres
{
    [DbContext(typeof(ApplicationDbContext))]
    partial class ApplicationDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
            modelBuilder.HasAnnotation("ProductVersion", "8.0.11")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityRole", b =>
            {
                b.Property<string>("Id").HasColumnType("text");
                b.Property<string>("ConcurrencyStamp").IsConcurrencyToken().HasColumnType("text");
                b.Property<string>("Name").HasMaxLength(256).HasColumnType("character varying(256)");
                b.Property<string>("NormalizedName").HasMaxLength(256).HasColumnType("character varying(256)");
                b.HasKey("Id");
                b.HasIndex("NormalizedName").IsUnique();
                b.ToTable("AspNetRoles");
            });

            modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityRoleClaim<string>", b =>
            {
                b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("integer");
                b.Property<string>("ClaimType").HasColumnType("text");
                b.Property<string>("ClaimValue").HasColumnType("text");
                b.Property<string>("RoleId").IsRequired().HasColumnType("text");
                b.HasKey("Id");
                b.HasIndex("RoleId");
                b.ToTable("AspNetRoleClaims");
            });

            modelBuilder.Entity("SentinelMfgSuite.Web.Models.ApplicationUser", b =>
            {
                b.Property<string>("Id").HasColumnType("text");
                b.Property<string>("ConcurrencyStamp").IsConcurrencyToken().HasColumnType("text");
                b.Property<string>("DisplayName").HasColumnType("text");
                b.Property<string>("CompanyName").HasColumnType("text");
                b.Property<Guid?>("TenantId").HasColumnType("uuid");
                b.Property<int>("ProvisioningState").HasColumnType("integer").HasDefaultValue(0);

                b.Property<string>("UserName").HasMaxLength(256).HasColumnType("character varying(256)");
                b.Property<string>("NormalizedUserName").HasMaxLength(256).HasColumnType("character varying(256)");
                b.Property<string>("Email").HasMaxLength(256).HasColumnType("character varying(256)");
                b.Property<string>("NormalizedEmail").HasMaxLength(256).HasColumnType("character varying(256)");
                b.Property<bool>("EmailConfirmed").HasColumnType("boolean");
                b.Property<string>("PasswordHash").HasColumnType("text");
                b.Property<string>("SecurityStamp").HasColumnType("text");
                b.Property<string>("PhoneNumber").HasColumnType("text");
                b.Property<bool>("PhoneNumberConfirmed").HasColumnType("boolean");
                b.Property<bool>("TwoFactorEnabled").HasColumnType("boolean");
                b.Property<DateTimeOffset?>("LockoutEnd").HasColumnType("timestamp with time zone");
                b.Property<bool>("LockoutEnabled").HasColumnType("boolean");
                b.Property<int>("AccessFailedCount").HasColumnType("integer");

                b.HasKey("Id");
                b.HasIndex("NormalizedEmail");
                b.HasIndex("NormalizedUserName").IsUnique();
                b.ToTable("AspNetUsers");
            });

            // ... further snapshot entries would mirror the Postgres migration. Kept concise here.
        }
    }
}

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

            // ... further snapshot entries would mirror the Postgres migration. Kept concise here.
        }
    }
}

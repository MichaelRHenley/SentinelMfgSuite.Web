using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SentinelMfgSuite.Web.Data;
using SentinelMfgSuite.Web.Models;

namespace SentinelMfgSuite.ProvisionAdmin
{
    internal class Program
    {
        public static async Task<int> Main(string[] args)
        {
            // Interactive prompt for admin email and password (password is not echoed)
            Console.Write("Administrator email: ");
            var adminEmail = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(adminEmail))
            {
                Console.Error.WriteLine("ERROR: Administrator email is required.");
                return 2;
            }

            var adminPassword = ReadPassword("Administrator password (input hidden): ");
            if (string.IsNullOrEmpty(adminPassword))
            {
                Console.Error.WriteLine("ERROR: Administrator password is required.");
                return 2;
            }

            const string envPath = "/etc/sentinel/sentinel-web.env";
            string? connStr;
            string connectionSource;
            try
            {
                var resolved = await ResolveConnectionStringAsync(envPath);
                if (!resolved.Success || string.IsNullOrWhiteSpace(resolved.ConnectionString))
                {
                    Console.Error.WriteLine($"ERROR: {resolved.ErrorMessage}");
                    return 4;
                }

                connStr = resolved.ConnectionString;
                connectionSource = resolved.Source;
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"ERROR: Cannot read {envPath} due to permissions. Run this tool as a user that can read the file or provide the connection string via environment variable.");
                return 3;
            }
            catch (Exception)
            {
                Console.Error.WriteLine("ERROR: Failed to resolve connection configuration securely.");
                return 3;
            }

            var validation = ValidateDevelopmentConnectionTarget(connStr);
            if (!validation.Success)
            {
                Console.Error.WriteLine($"ERROR: {validation.ErrorMessage}");
                return 4;
            }

            Console.WriteLine($"Using connection configuration from {connectionSource}. Target accepted for local development provisioning.");

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddSimpleConsole(options => { options.SingleLine = true; }));

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(connStr));

            // Configure Identity services to match the web application (keeps same validation rules)
            services.AddDefaultIdentity<ApplicationUser>(options =>
            {
                // Match production: allow sign-in without confirmed account for this tooling scenario
                options.SignIn.RequireConfirmedAccount = false;
            })
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            var provider = services.BuildServiceProvider();

            using var scope = provider.CreateScope();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

            try
            {
                // Ensure DB can be reached
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                logger.LogInformation("Checking database connectivity...");
                await db.Database.OpenConnectionAsync();
                await db.Database.CloseConnectionAsync();

                var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

                var roleName = "AssessmentAdmin";
                if (!await roleManager.RoleExistsAsync(roleName))
                {
                    logger.LogInformation("Creating role {role}", roleName);
                    var r = await roleManager.CreateAsync(new IdentityRole(roleName));
                    if (!r.Succeeded)
                    {
                        logger.LogError("Failed to create role: {errors}", FormatIdentityErrors(r.Errors));
                        return 3;
                    }
                }
                else
                {
                    logger.LogInformation("Role {role} already exists", roleName);
                }

                var user = await userManager.FindByEmailAsync(adminEmail);
                if (user == null)
                {
                    logger.LogInformation("Creating user {email}", adminEmail);
                    user = new ApplicationUser
                    {
                        UserName = adminEmail,
                        Email = adminEmail,
                        EmailConfirmed = true
                    };

                    var createResult = await userManager.CreateAsync(user, adminPassword);
                    if (!createResult.Succeeded)
                    {
                        logger.LogError("Failed to create user: {errors}", FormatIdentityErrors(createResult.Errors));
                        return 4;
                    }
                }
                else
                {
                    logger.LogInformation("User {email} already exists", adminEmail);
                }

                if (!await userManager.IsInRoleAsync(user, roleName))
                {
                    logger.LogInformation("Adding user {email} to role {role}", adminEmail, roleName);
                    var addRoleResult = await userManager.AddToRoleAsync(user, roleName);
                    if (!addRoleResult.Succeeded)
                    {
                        logger.LogError("Failed to add role to user: {errors}", FormatIdentityErrors(addRoleResult.Errors));
                        return 5;
                    }
                }
                else
                {
                    logger.LogInformation("User {email} is already in role {role}", adminEmail, roleName);
                }

                logger.LogInformation("Provisioning complete. Admin user: {email}", adminEmail);
                return 0;
            }
            catch (Exception)
            {
                // Do not log exception details to avoid accidental leakage of secrets (connection strings or similar).
                logger.LogError("Provisioning failed due to an internal error. Check server logs with appropriate access rights.");
                return 10;
            }
        }

        private static string FormatIdentityErrors(System.Collections.Generic.IEnumerable<IdentityError> errors)
        {
            if (errors == null) return string.Empty;
            return string.Join("; ", errors.Select(e => $"{e.Code}: {e.Description}"));
        }

        private static async Task<(bool Success, string? ConnectionString, string Source, string ErrorMessage)> ResolveConnectionStringAsync(string envPath)
        {
            var values = new HashSet<string>(StringComparer.Ordinal);

            if (System.IO.File.Exists(envPath))
            {
                var lines = await System.IO.File.ReadAllLinesAsync(envPath);
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                    if (trimmed.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                    {
                        trimmed = trimmed.Substring(7).Trim();
                    }

                    var idx = trimmed.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = trimmed.Substring(0, idx).Trim();
                    var val = trimmed.Substring(idx + 1).Trim().Trim('"').Trim('\'');
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        dict[key] = val;
                    }
                }

                foreach (var key in ConnectionStringCandidates)
                {
                    if (dict.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    {
                        values.Add(value.Trim());
                    }

                    var altKey = key.Replace(':', '_');
                    if (dict.TryGetValue(altKey, out var altValue) && !string.IsNullOrWhiteSpace(altValue))
                    {
                        values.Add(altValue.Trim());
                    }
                }

                if (values.Count > 1)
                {
                    return (false, null, "configuration file", "Ambiguous connection configuration detected in /etc/sentinel/sentinel-web.env. Ensure exactly one PostgreSQL connection string is configured.");
                }

                if (values.Count == 1)
                {
                    return (true, values.Single(), "configuration file", string.Empty);
                }
            }

            var envConnectionString = Environment.GetEnvironmentVariable("CONNECTION_STRING");
            var envDefaultConnection = Environment.GetEnvironmentVariable("DefaultConnection");

            var envValues = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(envConnectionString))
            {
                envValues.Add(envConnectionString.Trim());
            }

            if (!string.IsNullOrWhiteSpace(envDefaultConnection))
            {
                envValues.Add(envDefaultConnection.Trim());
            }

            if (envValues.Count > 1)
            {
                return (false, null, "environment variables", "Ambiguous connection configuration detected in environment variables. Ensure exactly one PostgreSQL connection string is configured.");
            }

            if (envValues.Count == 1)
            {
                return (true, envValues.Single(), "environment variables", string.Empty);
            }

            return (false, null, "none", "Connection string not found in /etc/sentinel/sentinel-web.env or environment variables. Provide exactly one secure local development connection string.");
        }

        private static (bool Success, string ErrorMessage) ValidateDevelopmentConnectionTarget(string connectionString)
        {
            NpgsqlConnectionStringBuilder builder;
            try
            {
                builder = new NpgsqlConnectionStringBuilder(connectionString);
            }
            catch
            {
                return (false, "Connection configuration is invalid and could not be parsed as a PostgreSQL connection string.");
            }

            if (string.IsNullOrWhiteSpace(builder.Host))
            {
                return (false, "Connection configuration is missing a PostgreSQL host.");
            }

            if (builder.Host.Contains(','))
            {
                return (false, "Connection configuration is ambiguous. Multiple PostgreSQL hosts are not allowed for this development-only utility.");
            }

            var host = builder.Host.Trim();
            if (!string.Equals(host, "localhost", StringComparison.Ordinal)
                && !string.Equals(host, "127.0.0.1", StringComparison.Ordinal))
            {
                return (false, "Provisioning is restricted to local PostgreSQL hosts (localhost or 127.0.0.1) for development safety.");
            }

            if (string.IsNullOrWhiteSpace(builder.Database))
            {
                return (false, "Connection configuration is missing a PostgreSQL database name.");
            }

            if (!string.Equals(builder.Database.Trim(), "sentinel_dev", StringComparison.Ordinal))
            {
                return (false, "Provisioning is restricted to database 'sentinel_dev' for development safety.");
            }

            return (true, string.Empty);
        }

        private static readonly string[] ConnectionStringCandidates =
        {
            "CONNECTION_STRING",
            "ConnectionStrings__DefaultConnection",
            "ConnectionStrings:DefaultConnection",
            "DefaultConnection",
            "ASPNETCORE_ConnectionStrings__DefaultConnection",
            "ASPNETCORE_DefaultConnection",
            "DATABASE_URL"
        };

        private static string ReadPassword(string prompt)
        {
            Console.Write(prompt);
            var pw = new System.Text.StringBuilder();
            ConsoleKeyInfo key;
            while (true)
            {
                key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (pw.Length > 0)
                    {
                        pw.Length -= 1;
                    }
                }
                else
                {
                    pw.Append(key.KeyChar);
                }
            }
            return pw.ToString();
        }
    }
}

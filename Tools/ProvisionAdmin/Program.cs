using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

            // Attempt to read connection string from /etc/sentinel/sentinel-web.env
            string? connStr = null;
            const string envPath = "/etc/sentinel/sentinel-web.env";
            try
            {
                if (System.IO.File.Exists(envPath))
                {
                    var lines = await System.IO.File.ReadAllLinesAsync(envPath);
                    var dict = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;
                        // support optional 'export KEY=VALUE' style
                        if (trimmed.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                        {
                            trimmed = trimmed.Substring(7).Trim();
                        }
                        var idx = trimmed.IndexOf('=');
                        if (idx <= 0) continue;
                        var key = trimmed.Substring(0, idx).Trim();
                        var val = trimmed.Substring(idx + 1).Trim().Trim('"').Trim('\'');
                        dict[key] = val;
                    }

                    // Common key candidates (first match wins)
                    var candidates = new[] {
                        "CONNECTION_STRING",
                        "ConnectionStrings__DefaultConnection",
                        "ConnectionStrings:DefaultConnection",
                        "DefaultConnection",
                        "ASPNETCORE_ConnectionStrings__DefaultConnection",
                        "ASPNETCORE_DefaultConnection",
                        "DATABASE_URL"
                    };

                    foreach (var c in candidates)
                    {
                        // dictionary is case-insensitive; try both literal and normalized forms
                        if (dict.TryGetValue(c, out var v) && !string.IsNullOrWhiteSpace(v))
                        {
                            connStr = v;
                            break;
                        }

                        // also try lower/upper variants for dotnet style env names using __
                        var alt = c.Replace(':', '_');
                        if (dict.TryGetValue(alt, out v) && !string.IsNullOrWhiteSpace(v))
                        {
                            connStr = v;
                            break;
                        }
                    }
                }
                else
                {
                    // /etc/sentinel/sentinel-web.env not present; fall through to environment
                }
            }
            catch (UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"ERROR: Cannot read {envPath} due to permissions. Run this tool as a user that can read the file or provide the connection string via environment variable.");
                return 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: Failed reading {envPath}: {ex.Message}");
                return 3;
            }

            // Fallback to existing environment variables if not found in file
            if (string.IsNullOrWhiteSpace(connStr))
            {
                connStr = Environment.GetEnvironmentVariable("CONNECTION_STRING") ?? Environment.GetEnvironmentVariable("DefaultConnection");
            }

            if (string.IsNullOrWhiteSpace(connStr))
            {
                Console.Error.WriteLine("ERROR: Connection string not found in /etc/sentinel/sentinel-web.env or environment variables. Provide the connection string securely.");
                return 4;
            }

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

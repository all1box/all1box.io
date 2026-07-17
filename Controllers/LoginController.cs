using System.Security.Claims;
using all1box.io.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace all1box.io.Controllers;

public sealed class LoginController : Controller
{
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly GraphLoginCodeSender _loginCodeSender;
    private readonly ILogger<LoginController> _logger;

    public LoginController(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        GraphLoginCodeSender loginCodeSender,
        ILogger<LoginController> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _loginCodeSender = loginCodeSender;
        _logger = logger;
    }

    [HttpGet("/Login")]
    public IActionResult Index()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Admin");
        }

        return View();
    }

    [HttpPost("/Login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(string login, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            ViewBag.Error = "Please enter your phone number or email.";
            return View();
        }

        login = login.Trim();
        var isEmailLogin = login.Contains('@');
        var cleanPhone = new string(login.Where(char.IsDigit).ToArray());

        if (!isEmailLogin && cleanPhone.Length < 10)
        {
            ViewBag.Error = "Please enter a valid phone number or email.";
            return View();
        }

        try
        {
            await using var connection = new SqlConnection(GetLoginConnectionString());
            await connection.OpenAsync(cancellationToken);

            var last10 = cleanPhone.Length > 10 ? cleanPhone[^10..] : cleanPhone;
            var selectSql = isEmailLogin
                ? "SELECT TOP 1 ID, Email, CellPhone FROM WebLogin WHERE LOWER(Email) = LOWER(@Login)"
                : "SELECT TOP 1 ID, Email, CellPhone FROM WebLogin WHERE RIGHT(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(CellPhone, ''), '-', ''), '(', ''), ')', ''), ' ', ''), 10) = @Phone";

            await using var checkCommand = new SqlCommand(selectSql, connection);
            if (isEmailLogin)
            {
                checkCommand.Parameters.AddWithValue("@Login", login);
            }
            else
            {
                checkCommand.Parameters.AddWithValue("@Phone", last10);
            }

            string? email = null;
            string? cellPhone = null;
            int? loginId = null;
            await using (var reader = await checkCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    loginId = Convert.ToInt32(reader["ID"]);
                    email = reader["Email"] as string;
                    cellPhone = reader["CellPhone"] as string;
                }
            }

            if (!loginId.HasValue || string.IsNullOrWhiteSpace(email))
            {
                ViewBag.Error = isEmailLogin
                    ? "We couldn't find an account with that email."
                    : "We couldn't find an account with that phone number.";
                return View();
            }

            var code = Random.Shared.Next(100000, 999999).ToString();
            var guid = Guid.NewGuid().ToString();

            const string updateSql = """
                UPDATE WebLogin
                SET GUID = @GUID,
                    Code = @Code,
                    CodeDateTime = @Now,
                    ModifiedDate = @Now,
                    ModifiedBy = @Email,
                    ModifiedByIP = @IP
                WHERE ID = @ID;
                """;

            await using var updateCommand = new SqlCommand(updateSql, connection);
            updateCommand.Parameters.AddWithValue("@GUID", guid);
            updateCommand.Parameters.AddWithValue("@Code", code);
            updateCommand.Parameters.AddWithValue("@Now", GetPacificNow());
            updateCommand.Parameters.AddWithValue("@Email", email);
            updateCommand.Parameters.AddWithValue("@IP", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            updateCommand.Parameters.AddWithValue("@ID", loginId.Value);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);

            if (isEmailLogin)
            {
                var emailSent = await _loginCodeSender.SendEmailCodeAsync(email, code, cancellationToken);
                if (!emailSent)
                {
                    ViewBag.Error = "We found your account, but email verification is not configured yet.";
                    return View();
                }
            }
            else
            {
                _logger.LogInformation("Phone login requested for {MaskedPhone}; SMS delivery is not configured in this project.", MaskPhone(cellPhone ?? login));
                ViewBag.Error = "Phone verification is not configured yet. Please sign in with your email.";
                return View();
            }

            return RedirectToAction("Verify", new { id = guid });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed for {Login}.", login);
            ViewBag.Error = "Login failed. Please try again.";
            return View();
        }
    }

    [HttpGet("/Login/Verify/{id?}")]
    public async Task<IActionResult> Verify(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return RedirectToAction("Index");
        }

        await using var connection = new SqlConnection(GetLoginConnectionString());
        await connection.OpenAsync(cancellationToken);

        const string sql = "SELECT Email, CellPhone FROM WebLogin WHERE GUID = @GUID";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@GUID", id);

        string? email = null;
        string? cellPhone = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                email = reader["Email"] as string;
                cellPhone = reader["CellPhone"] as string;
            }
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return RedirectToAction("Index");
        }

        ViewBag.MaskedDestination = string.IsNullOrWhiteSpace(cellPhone)
            ? MaskEmail(email)
            : MaskEmail(email);
        ViewBag.Guid = id;
        return View();
    }

    [HttpPost("/Login/Verify/{id?}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify(string id, string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(code))
        {
            return RedirectToAction("Index");
        }

        await using var connection = new SqlConnection(GetLoginConnectionString());
        await connection.OpenAsync(cancellationToken);

        string? email = null;
        string? cellPhone = null;
        string? userName = null;
        var trimmedCode = code.Trim();

        if (trimmedCode == "123456" && _environment.IsDevelopment())
        {
            _logger.LogWarning("Development login bypass used for GUID {Guid}.", id);
            const string bypassSql = "SELECT Email, CellPhone, UserName FROM WebLogin WHERE GUID = @GUID";
            await using var bypassCommand = new SqlCommand(bypassSql, connection);
            bypassCommand.Parameters.AddWithValue("@GUID", id);
            await using var bypassReader = await bypassCommand.ExecuteReaderAsync(cancellationToken);
            if (await bypassReader.ReadAsync(cancellationToken))
            {
                email = bypassReader["Email"] as string;
                cellPhone = bypassReader["CellPhone"] as string;
                userName = bypassReader["UserName"] as string;
            }
        }
        else
        {
            const string sql = "SELECT Email, CellPhone, UserName FROM WebLogin WHERE GUID = @GUID AND Code = @Code";
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@GUID", id);
            command.Parameters.AddWithValue("@Code", trimmedCode);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                email = reader["Email"] as string;
                cellPhone = reader["CellPhone"] as string;
                userName = reader["UserName"] as string;
            }
        }

        var identityName = !string.IsNullOrWhiteSpace(email)
            ? email
            : (!string.IsNullOrWhiteSpace(userName) ? userName : (cellPhone ?? ""));

        if (string.IsNullOrWhiteSpace(identityName))
        {
            ViewBag.Error = "Invalid verification code. Please try again.";
            ViewBag.Guid = id;
            ViewBag.MaskedDestination = await GetMaskedDestinationAsync(connection, id, cancellationToken);
            return View();
        }

        const string updateSql = """
            UPDATE WebLogin
            SET Code = NULL,
                LoginDate = @Now,
                LoginCounter = ISNULL(LoginCounter, 0) + 1,
                ModifiedBy = @IP
            WHERE GUID = @GUID;
            """;

        await using var updateCommand = new SqlCommand(updateSql, connection);
        updateCommand.Parameters.AddWithValue("@Now", GetPacificNow());
        updateCommand.Parameters.AddWithValue("@IP", HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        updateCommand.Parameters.AddWithValue("@GUID", id);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, email ?? ""),
            new(ClaimTypes.Name, identityName),
            new("LoginGuid", id)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                AllowRefresh = false,
                IsPersistent = true,
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = GetNextSessionMidnightUtc()
            });

        return RedirectToAction("Index", "Admin");
    }

    [AcceptVerbs("GET", "POST")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Index");
    }

    private string GetLoginConnectionString()
    {
        var connectionString = _configuration.GetConnectionString("BPDConn");
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("YOUR_", StringComparison.OrdinalIgnoreCase))
        {
            connectionString = _configuration.GetConnectionString("WebOSConn");
        }

        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("YOUR_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A login database connection string is not configured.");
        }

        return connectionString;
    }

    private static async Task<string> GetMaskedDestinationAsync(SqlConnection connection, string guid, CancellationToken cancellationToken)
    {
        const string sql = "SELECT Email FROM WebLogin WHERE GUID = @GUID";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@GUID", guid);

        var email = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.IsNullOrWhiteSpace(email) ? "" : MaskEmail(email);
    }

    private static DateTimeOffset GetNextSessionMidnightUtc()
    {
        var sessionTimeZone = GetPacificTimeZone();
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, sessionTimeZone);
        var nextLocalMidnight = DateTime.SpecifyKind(nowLocal.Date.AddDays(1), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(nextLocalMidnight, sessionTimeZone));
    }

    private static DateTime GetPacificNow()
    {
        return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, GetPacificTimeZone()).DateTime;
    }

    private static TimeZoneInfo GetPacificTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        }
    }

    private static string MaskPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length < 4)
        {
            return "***-****";
        }

        return $"(***) ***-{digits[^4..]}";
    }

    private static string MaskEmail(string email)
    {
        var parts = email.Split('@', 2);
        if (parts.Length != 2 || parts[0].Length == 0)
        {
            return "***";
        }

        var name = parts[0].Length <= 2
            ? parts[0][0] + "***"
            : parts[0][0] + "***" + parts[0][^1];
        return $"{name}@{parts[1]}";
    }
}

using Microsoft.Extensions.Configuration;

namespace Bigpdf.Services;

public interface IAdminAuthSettings
{
    string Username { get; }
    string Password { get; }
}

public class AdminAuthSettings : IAdminAuthSettings
{
    public string Username { get; }
    public string Password { get; }

    public AdminAuthSettings(IConfiguration configuration)
    {
        var adminSection = configuration.GetSection("AdminAuth");
        Username = adminSection["Username"] ?? "admin";
        Password = adminSection["Password"] ?? "ChangeThisPassword123!";
    }
}
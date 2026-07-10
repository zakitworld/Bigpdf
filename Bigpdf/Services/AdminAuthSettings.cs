using System;
using Microsoft.Extensions.Configuration;

namespace Bigpdf.Services
{
    public interface IAdminAuthSettings
    {
        string Username { get; }
        string Password { get; }
        bool IsUsingDefaultPassword { get; }
    }

    public class AdminAuthSettings : IAdminAuthSettings
    {
        private const string DefaultUsername = "admin";
        private const string DefaultPassword = "BigpdfAdmin2026!";
        private readonly IConfiguration _configuration;

        public AdminAuthSettings(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public string Username => GetSetting("BIGPDF_ADMIN_USERNAME", "AdminUser:UserName", DefaultUsername);

        public string Password => GetSetting("BIGPDF_ADMIN_PASSWORD", "AdminUser:Password", DefaultPassword);

        public bool IsUsingDefaultPassword => string.Equals(Password, DefaultPassword, StringComparison.Ordinal);

        private string GetSetting(string environmentVariableName, string configurationKey, string fallback)
        {
            var envValue = Environment.GetEnvironmentVariable(environmentVariableName);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                return envValue;
            }

            var configValue = _configuration[configurationKey];
            return !string.IsNullOrWhiteSpace(configValue) ? configValue : fallback;
        }
    }
}

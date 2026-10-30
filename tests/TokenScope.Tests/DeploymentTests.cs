using Microsoft.AspNetCore.Http;
using Npgsql;
using TokenScope.Api.Infrastructure.Database;
using TokenScope.Api.Infrastructure.Http;
using Xunit;

namespace TokenScope.Tests;

public class DeploymentTests
{
    [Fact]
    public void Connection_configuration_is_required_and_errors_never_echo_credentials()
    {
        Assert.Contains("ConnectionStrings__tokenscope", Assert.Throws<InvalidOperationException>(
            () => DatabaseConfiguration.ConnectionString(null)).Message);
        foreach (var value in new[] { "Host=example;Password=PRIVATE-SECRET", "Host=example;Database=test;Password=PRIVATE-SECRET;Unsupported=1" })
        {
            var error = Assert.Throws<InvalidOperationException>(() => DatabaseConfiguration.ConnectionString(value));
            Assert.DoesNotContain("PRIVATE", error.ToString());
        }
        var valid = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(
            "Host=example;Database=test;Username=app;Timezone=Europe/Budapest;Include Error Detail=true"));
        Assert.Equal("UTC", valid.Timezone);
        Assert.False(valid.IncludeErrorDetail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void Database_alias_is_used_when_primary_is_missing_or_blank(string? primary)
    {
        var parsed = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(primary,
            "Host=alias.example;Database=alias;Timezone=Europe/Budapest;Include Error Detail=true"));
        Assert.Equal("alias.example", parsed.Host);
        Assert.Equal("alias", parsed.Database);
        Assert.Equal("UTC", parsed.Timezone);
        Assert.False(parsed.IncludeErrorDetail);
    }

    [Fact]
    public void Nonblank_primary_wins_and_unused_alias_is_not_validated()
    {
        const string primary = "Host=primary.example;Database=primary";
        foreach (var alias in new[] { "Host=alias.example;Database=alias", "invalid alias" })
        {
            var parsed = new NpgsqlConnectionStringBuilder(DatabaseConfiguration.ConnectionString(primary, alias));
            Assert.Equal("primary.example", parsed.Host);
            Assert.Equal("primary", parsed.Database);
        }
    }

    [Fact]
    public void Invalid_primary_does_not_fall_back_and_errors_identify_only_the_setting()
    {
        const string invalid = "Host=example;Password=PRIVATE-SECRET;Unsupported=1";
        var primaryError = Assert.Throws<InvalidOperationException>(() => DatabaseConfiguration.ConnectionString(
            invalid, "Host=alias.example;Database=alias"));
        Assert.Contains("ConnectionStrings__tokenscope is invalid", primaryError.Message);
        Assert.DoesNotContain("PRIVATE", primaryError.ToString());
        var aliasError = Assert.Throws<InvalidOperationException>(() => DatabaseConfiguration.ConnectionString(null, invalid));
        Assert.Contains("ConnectionStrings__Database is invalid", aliasError.Message);
        Assert.DoesNotContain("PRIVATE", aliasError.ToString());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", " ")]
    [InlineData(" \t ", "")]
    public void Missing_both_connection_settings_reports_both_names(string? primary, string? alias)
    {
        var error = Assert.Throws<InvalidOperationException>(() => DatabaseConfiguration.ConnectionString(primary, alias));
        Assert.Contains("ConnectionStrings__tokenscope", error.Message);
        Assert.Contains("ConnectionStrings__Database", error.Message);
    }

    [Theory]
    [InlineData("https://usage.example.com", "usage.example.com", false, true)]
    [InlineData("https://usage.example.com:8443", "usage.example.com:8443", false, true)]
    [InlineData("https://usage.example.com.attacker.test", "usage.example.com", false, false)]
    [InlineData("https://attacker.test", "usage.example.com", false, false)]
    [InlineData("http://localhost:5199", "localhost:5299", false, false)]
    [InlineData("http://localhost:5199", "localhost:5299", true, true)]
    [InlineData("null", "usage.example.com", false, false)]
    [InlineData("ftp://usage.example.com", "usage.example.com", false, false)]
    public void Origin_checks_allow_deployed_host_and_development_origin_only_when_enabled(
        string origin, string host, bool development, bool expected) =>
        Assert.Equal(expected, RequestOrigins.IsAllowed(origin, new HostString(host), development));

    [Fact]
    public void History_refresh_limits_minutes_but_preserves_full_hourly_and_daily_coverage()
    {
        var now = new DateTime(2026, 10, 21, 12, 34, 56, DateTimeKind.Utc);
        var old = now.AddDays(-120);
        var windows = ContinuousAggregateRefresher.RefreshWindows(old, now).ToDictionary(w => w.View);
        Assert.Equal(new DateTime(2026, 10, 21, 10, 34, 0, DateTimeKind.Utc), windows["spend_1m"].Start);
        Assert.Equal(new DateTime(2026, 10, 21, 12, 34, 0, DateTimeKind.Utc), windows["spend_1m"].End);
        Assert.Equal(old.Date.AddHours(12), windows["spend_1h"].Start);
        Assert.Equal(old.Date, windows["spend_1d"].Start);
        Assert.Empty(ContinuousAggregateRefresher.RefreshWindows(now.AddMinutes(5), now));
        var recent = ContinuousAggregateRefresher.RefreshWindows(now.AddMinutes(-10), now).Single(w => w.View == "spend_1m");
        Assert.Equal(new DateTime(2026, 10, 21, 12, 24, 0, DateTimeKind.Utc), recent.Start);
    }
}

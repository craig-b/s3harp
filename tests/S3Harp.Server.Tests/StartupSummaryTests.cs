using Microsoft.Extensions.Configuration;
using Xunit;

namespace S3Harp.Server.Tests;

public sealed class StartupSummaryTests
{
    [Fact]
    public void NamesWhereTheServerListensStoresAndWhoItServes_ButNeverTheSecret()
    {
        var options = S3HarpOptions.Load(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ACCESS_KEY_ID"] = "S3HARPEXAMPLEKEY",
                        ["SECRET_ACCESS_KEY"] = "very-secret",
                        ["DATA_DIR"] = "/srv/s3harp",
                        ["DOMAIN"] = "s3.test",
                    }
                )
                .Build()
        );

        var summary = StartupSummary.Describe(options, ["http://127.0.0.1:9000"]);

        Assert.Equal(
            "S3Harp listening on http://127.0.0.1:9000, storing data in /srv/s3harp, "
                + "serving buckets under s3.test, access key S3HARPEXAMPLEKEY",
            summary
        );
        Assert.DoesNotContain("very-secret", summary, StringComparison.Ordinal);
    }
}

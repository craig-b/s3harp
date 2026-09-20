namespace S3Harp.Server.Configuration;

/// <summary>A TOML file as a configuration source.</summary>
internal sealed class TomlConfigurationSource : FileConfigurationSource
{
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new TomlConfigurationProvider(this);
    }
}

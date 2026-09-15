namespace S3Harp.Server.Authentication;

/// <summary>The scope portion of a SigV4 credential: date, region, and service.</summary>
public sealed record CredentialScope(string Date, string Region, string Service)
{
    public override string ToString() => $"{Date}/{Region}/{Service}/aws4_request";
}

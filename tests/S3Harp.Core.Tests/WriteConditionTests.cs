using Xunit;

namespace S3Harp.Core.Tests;

public sealed class WriteConditionTests
{
    private static readonly ObjectRecord Existing = new(
        "key", "blob", Size: 3, ETag: "etag-hex", ContentType: null, ContentHeaders: ContentHeaders.None,
        Metadata: new Dictionary<string, string>(), LastModified: DateTimeOffset.UnixEpoch);

    [Fact]
    public void MustMatchAnyObject_IsSatisfiedByAnyExistingObject()
    {
        var condition = new WriteCondition(MustMatch: ETagCondition.AnyObject);

        Assert.Equal(WriteConditionResult.Satisfied, condition.Check(Existing));
        Assert.Equal(WriteConditionResult.ObjectMissing, condition.Check(null));
    }

    [Fact]
    public void MustMatchAnETag_RequiresThatExactObject()
    {
        Assert.Equal(
            WriteConditionResult.Satisfied,
            new WriteCondition(MustMatch: new ETagCondition("etag-hex")).Check(Existing));
        Assert.Equal(
            WriteConditionResult.PreconditionFailed,
            new WriteCondition(MustMatch: new ETagCondition("other")).Check(Existing));
        Assert.Equal(
            WriteConditionResult.ObjectMissing,
            new WriteCondition(MustMatch: new ETagCondition("etag-hex")).Check(null));
    }

    [Fact]
    public void MustNotMatchAnyObject_RequiresAnEmptyKey()
    {
        var condition = new WriteCondition(MustNotMatch: ETagCondition.AnyObject);

        Assert.Equal(WriteConditionResult.Satisfied, condition.Check(null));
        Assert.Equal(WriteConditionResult.PreconditionFailed, condition.Check(Existing));
    }

    [Fact]
    public void MustNotMatchAnETag_RefusesOnlyThatObject()
    {
        Assert.Equal(
            WriteConditionResult.PreconditionFailed,
            new WriteCondition(MustNotMatch: new ETagCondition("etag-hex")).Check(Existing));
        Assert.Equal(
            WriteConditionResult.Satisfied,
            new WriteCondition(MustNotMatch: new ETagCondition("other")).Check(Existing));
        Assert.Equal(
            WriteConditionResult.Satisfied,
            new WriteCondition(MustNotMatch: new ETagCondition("etag-hex")).Check(null));
    }
}

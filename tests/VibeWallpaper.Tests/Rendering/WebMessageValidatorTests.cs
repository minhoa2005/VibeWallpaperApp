using VibeWallpaper.Engine.Rendering.Web;

namespace VibeWallpaper.Tests.Rendering;

public sealed class WebMessageValidatorTests
{
    [Fact]
    public void ReadyMessage_IsAccepted()
    {
        var result = WebMessageValidator.Validate(@"{""schemaVersion"":1,""type"":""ready"",""payload"":{}}" );

        Assert.True(result.IsValid);
        Assert.Equal("ready", result.Message!.Type);
    }

    [Fact]
    public void UnknownMessageType_IsRejected()
    {
        var result = WebMessageValidator.Validate(@"{""schemaVersion"":1,""type"":""execute-native"",""payload"":{}}" );

        Assert.False(result.IsValid);
        Assert.Equal(WebMessageValidationError.UnknownType, result.Error);
    }

    [Fact]
    public void WrongSchemaVersion_IsRejected()
    {
        var result = WebMessageValidator.Validate(@"{""schemaVersion"":2,""type"":""ready"",""payload"":{}}" );

        Assert.False(result.IsValid);
        Assert.Equal(WebMessageValidationError.UnsupportedSchemaVersion, result.Error);
    }

    [Fact]
    public void MalformedJson_IsRejectedWithoutThrowing()
    {
        var result = WebMessageValidator.Validate("not-json");

        Assert.False(result.IsValid);
        Assert.Equal(WebMessageValidationError.MalformedJson, result.Error);
    }

    [Fact]
    public void OversizedMessage_IsRejectedBeforeParsing()
    {
        var result = WebMessageValidator.Validate(new string('x', WebMessageValidator.MaximumUtf8Bytes + 1));

        Assert.False(result.IsValid);
        Assert.Equal(WebMessageValidationError.TooLarge, result.Error);
    }

    [Fact]
    public void InteractionState_RequiresBooleanActivePayload()
    {
        var valid = WebMessageValidator.Validate(@"{""schemaVersion"":1,""type"":""interaction-state"",""payload"":{""active"":true}}" );
        var invalid = WebMessageValidator.Validate(@"{""schemaVersion"":1,""type"":""interaction-state"",""payload"":{""active"":""yes""}}" );

        Assert.True(valid.IsValid);
        Assert.False(invalid.IsValid);
        Assert.Equal(WebMessageValidationError.InvalidPayload, invalid.Error);
    }
}

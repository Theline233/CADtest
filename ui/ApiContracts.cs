namespace CadReviewUI;

/// <summary>Non-secret metadata for a user-configured vision API profile.</summary>
public sealed class ApiProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    // responses or chat_completions; custom providers must implement the selected wire protocol.
    public string Protocol { get; set; } = "responses";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "";
    public bool HasKey { get; set; }
}

/// <summary>Result is advisory and cannot modify or approve CAD records.</summary>
public sealed class ApiCallResult
{
    public string Text { get; set; } = "";
    public string? ReturnedModel { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public string? Note { get; set; }
}

namespace SignIt.Infrastructure.Llm;

public sealed class LlmOptions
{
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "gpt-4o-mini";
    public int TimeoutSeconds { get; set; } = 30;
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 1000;
}

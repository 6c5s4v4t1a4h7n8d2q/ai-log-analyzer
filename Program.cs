using System.Text;
using OpenAI.Chat;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>(optional: true);

var apiKey = builder.Configuration["OpenAI:ApiKey"] ?? builder.Configuration["OPENAI_API_KEY"];
var chatClient = string.IsNullOrWhiteSpace(apiKey)
    ? null
    : new ChatClient(model: "gpt-4.1-mini", apiKey: apiKey);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/analyze", async (HttpRequest request) =>
{
    if (chatClient is null)
    {
        return Results.Problem(
            title: "OpenAI API key is not configured",
            detail: "Set OpenAI:ApiKey with dotnet user-secrets, or set the OPENAI_API_KEY environment variable.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Upload a text log file using multipart/form-data." });
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("logFile");

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "Choose a non-empty log file." });
    }

    const long maxFileBytes = 2 * 1024 * 1024;
    if (file.Length > maxFileBytes)
    {
        return Results.BadRequest(new { error = "The log file is too large. The current limit is 2 MB." });
    }

    var extension = Path.GetExtension(file.FileName);
    if (!string.IsNullOrWhiteSpace(extension) &&
        !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) &&
        !extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Upload a .txt or .log file." });
    }

    string logs;
    using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
    {
        logs = await reader.ReadToEndAsync();
    }

    if (string.IsNullOrWhiteSpace(logs))
    {
        return Results.BadRequest(new { error = "The uploaded file does not contain readable log text." });
    }

    var systemMessage = """
        You are a senior production support engineer who analyzes application logs.
        Return a concise analysis in Markdown with exactly these headings:
        ## Summary
        ## Errors
        ## Likely root cause
        ## Severity
        ## Recommended next steps
        ## Useful signals

        Under Errors, list the concrete error messages, exception names, status codes, stack frames, or failure events you can identify.
        If the logs do not contain enough evidence for a section, say that clearly instead of guessing.
        Keep the response practical and focused on what an engineer should investigate next.
        """;

    var userMessage = $"""
        Analyze this uploaded log file.

        File name: {file.FileName}
        Size: {file.Length} bytes

        Logs:
        ```text
        {logs}
        ```
        """;

    try
    {
        var completion = await chatClient.CompleteChatAsync(
            messages:
            [
                new SystemChatMessage(systemMessage),
                new UserChatMessage(userMessage)
            ]);

        var analysis = completion.Value.Content.Count > 0
            ? completion.Value.Content[0].Text
            : "The model returned no analysis.";

        return Results.Ok(new
        {
            fileName = file.FileName,
            fileSize = file.Length,
            analysis
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "OpenAI log analysis failed.");
        return Results.Problem(
            title: "OpenAI analysis failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

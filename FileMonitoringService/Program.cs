using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using FileMonitoringService;

var builder = WebApplication.CreateBuilder(args);

// Add CORS services
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin() // Allow requests from any origin
              .AllowAnyMethod() // Allow any HTTP method (GET, POST, etc.)
              .AllowAnyHeader(); // Allow any headers
    });
});

// Register the FileMonitorService as a singleton
builder.Services.AddSingleton<FileMonitorService>();

var app = builder.Build();

// Enable CORS middleware
app.UseCors();

string fullText = "";

// GET endpoint to retrieve the latest text
app.MapGet("/get-text", (FileMonitorService fileMonitorService) =>
{
    return Results.Text(fileMonitorService.GetText());
});

// POST endpoint to update the text
app.MapPost("/update-text", async (FileMonitorService fileMonitorService, HttpContext context) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var newText = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(newText))
    {
        return Results.BadRequest("Text cannot be empty.");
    }

    Console.WriteLine($"Received text: {newText}");
    // If the end of the text is \n\n then append it to the existing text in fullText
    if (newText.EndsWith("\n\n"))
    {
        fullText += newText;
        newText = fullText;
        Console.WriteLine($"Updated fullText: {fullText}");
    }
    else if (newText.Contains("<clear>"))
    {
        fullText = "";
        newText = " ";
        Console.WriteLine($"Cleared fullText: {fullText}");
    }
    else
    {
        newText = fullText + newText;
        Console.WriteLine($"Updated newText: {newText}");
    }

    fileMonitorService.UpdateText(newText);
    return Results.Ok("Text updated successfully.");
});

app.Run();
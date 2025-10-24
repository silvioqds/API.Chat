using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// HttpClient for calling n8n
builder.Services.AddHttpClient("n8n", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Accept.Clear();
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();

app.MapControllers();

app.MapPost("/chat", async (ChatRequest body, IHttpClientFactory httpClientFactory, HttpContext httpContext, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body?.Message))
        return Results.BadRequest(new { error = "Message is required." });

    var n8nUrl = httpContext.RequestServices.GetRequiredService<IConfiguration>()["N8N:WebhookUrl"];

    if (string.IsNullOrWhiteSpace(n8nUrl))
        return Results.Problem("N8N webhook URL not configured. Set N8N:WebhookUrl or N8N_WEBHOOK_URL.", statusCode: 500);

    var client = httpClientFactory.CreateClient("n8n");

    HttpResponseMessage response;
    try
    {
        var payload = new { message = body.Message };
        response = await client.PostAsJsonAsync(n8nUrl, payload, ct);
    }
    catch (Exception ex) when (ex is TaskCanceledException || ex is HttpRequestException)
    {
        return Results.Problem(detail: "Failed to reach n8n webhook.", statusCode: 502, title: ex.Message);
    }

    if (!response.IsSuccessStatusCode)
    {
        var errorBody = await response.Content.ReadAsStringAsync(ct);
        return Results.Problem( title: $"n8n returned {(int)response.StatusCode} ({response.StatusCode})",statusCode: (int)response.StatusCode,detail: errorBody);
    }

    if (response.StatusCode == HttpStatusCode.NoContent ||response.Content.Headers.ContentLength == 0)    
        return Results.Ok(new ChatResponse(string.Empty));    

    var raw = await response.Content.ReadAsStringAsync(ct);
    if (string.IsNullOrWhiteSpace(raw))
        return Results.Ok(new ChatResponse(string.Empty));

    var contentType = response.Content.Headers.ContentType?.MediaType;
    
    if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
    {
        try
        {
            var typed = JsonSerializer.Deserialize<ChatResponse>(raw);
            if (typed is not null)
                return Results.Ok(typed);          
        }
        catch (JsonException)
        {
            throw;
        }
    }
    return Results.Ok(new ChatResponse(raw));
})
.WithName("Chat")
.Produces<ChatResponse>(StatusCodes.Status200OK)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status500InternalServerError)
.ProducesProblem(StatusCodes.Status502BadGateway);

app.Run();

record ChatRequest(string Message);
record ChatResponse(string Response);
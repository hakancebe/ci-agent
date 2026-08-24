using System.ClientModel;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace CiAgent.Core;

public class LlmService
{
    private readonly ChatClient _chat;

    private const int MaxLogChars = 8000;
    private const int HeadChars = 1500;

    private const string SystemPrompt = """
        Sen bir CI/CD hata analiz asistanısın. Sana bir GitHub Actions job'ının
        başarısız olan adımına ait log kesiti ve metadata verilecek.

        Kurallar:
        - Sadece verilen veriye dayan, tahmin uydurma.
        - Log yetersizse confidence alanını "low" yap ve bunu rootCause'da belirt.
        - suggestedFix somut ve uyanabilir olsun (hangi dosyada ne değişecek).
        - Türkçe cevap ver.
        - Yanıtı yalnızca istenen JSON şemasında döndür.
        """;

    private const string JsonSchema = """
        {
          "type": "object",
          "properties": {
            "summary":      { "type": "string" },
            "rootCause":    { "type": "string" },
            "suggestedFix": { "type": "string" },
            "confidence":   { "type": "string", "enum": ["high", "medium", "low"] },
            "affectedFile": { "type": ["string", "null"] },
            "affectedLine": { "type": ["integer", "null"] }
          },
          "required": ["summary", "rootCause", "suggestedFix", "confidence", "affectedFile", "affectedLine"],
          "additionalProperties": false
        }
        """;
    
    public LlmService(string endpoint, string apiKey, string deployment)
    {
        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

        _chat = client.GetChatClient(deployment);
    }

    public async Task<AnalysisResult?> AnalyzeAsync(ErrorContext context)
    {
        var options = new ChatCompletionOptions
        {
            //Kendince bir şey eklmemesi için 0.2f belirliyoruz
            Temperature = 0.2f,
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "analysis_result",
                jsonSchema: BinaryData.FromString(JsonSchema),
                jsonSchemaIsStrict: true)
        };

        List<ChatMessage> messages =
        [
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(BuildPrompt(context))
        ];

        ChatCompletion completion = await _chat.CompleteChatAsync(messages, options);
        var json = completion.Content[0].Text;

        //json to AnalysisResult type
        return JsonSerializer.Deserialize<AnalysisResult>(json);
    }

    private static string BuildPrompt(ErrorContext ctx)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Job adı: {ctx.JobName}");
        sb.AppendLine($"Başarısız adım: {ctx.FailedStepName}");

        if (ctx.ErrorMessage is not null)
            sb.AppendLine($"Ayrıştırılmış hata mesajı: {Masker.Mask(ctx.ErrorMessage)}");

        if (ctx.FilePath is not null)
            sb.AppendLine($"Dosya: {ctx.FilePath}:{ctx.LineNumber}");

        if (ctx.FilteredAnnotations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Checks API annotation'ları:");
            foreach (var a in ctx.FilteredAnnotations)
                sb.AppendLine($"- {Masker.Mask(a)}");
        }

        // Tüm hataların dosya:satır konumu zaten kesinse (AllFailuresLocated),
        // ham log ekstra bilgi katmıyor - sadece ErrorMessage'ın tekrarı oluyor.
        // Konum belirsizse (ör. build-cs1002'deki gibi parser'ın telafi
        // edemediği durumlar), LLM'in ham veriden çıkarım yapabilmesi için tam
        // hâliyle gönderiliyor.
        if (!string.IsNullOrWhiteSpace(ctx.RawStepLog) && !ctx.AllFailuresLocated)
        {
            sb.AppendLine();
            sb.AppendLine("Ham log kesiti:");
            sb.AppendLine("```");
            sb.AppendLine(TrimLog(Masker.Mask(ctx.RawStepLog)));
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    private static string TrimLog(string log)
    {
        if (log.Length <= MaxLogChars) return log;

        var head = log[..HeadChars];
        var tail = log[^(MaxLogChars - HeadChars)..];

        return $"{head}\n\n... [{log.Length - MaxLogChars} karakter kırpıldı] ...\n\n{tail}";
    }
}
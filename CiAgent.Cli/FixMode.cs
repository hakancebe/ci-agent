using CiAgent.Core;
using Microsoft.Extensions.Logging;

namespace CiAgent.Cli;

/// <summary>
/// /fix modunun giriş noktası. Girdiler workflow tarafından issue_comment
/// olayının payload'ından env var olarak besleniyor.
/// </summary>
public static class FixMode
{
    public static async Task<int> RunAsync(
        string githubToken, string azureEndpoint, string azureKey, string azureDeployment)
    {
        var missing = new List<string>();

        string Required(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) missing.Add(name);
            return value ?? "";
        }

        var owner = Required("CI_AGENT_OWNER");
        var repo = Required("CI_AGENT_REPO");
        var prRaw = Required("CI_AGENT_PR_NUMBER");
        var commentIdRaw = Required("CI_AGENT_COMMENT_ID");
        var association = Required("CI_AGENT_COMMENT_AUTHOR_ASSOCIATION");

        // Yorum gövdesi bilerek "zorunlu" listesinde değil: boş bir yorum
        // geçerli bir olay, sadece /fix komutu değil demektir.
        var body = Environment.GetEnvironmentVariable("CI_AGENT_COMMENT_BODY") ?? "";

        // Varsayılan olarak runner'ın checkout dizini.
        var workspace = Environment.GetEnvironmentVariable("CI_AGENT_WORKSPACE")
                        ?? Directory.GetCurrentDirectory();

        if (missing.Count > 0)
        {
            Console.Error.WriteLine(
                $"HATA: /fix modu için şu env var'lar zorunlu ama verilmedi: {string.Join(", ", missing)}");
            return 1;
        }

        if (!int.TryParse(prRaw, out var prNumber) || !long.TryParse(commentIdRaw, out var commentId))
        {
            Console.Error.WriteLine(
                $"HATA: PR numarası ('{prRaw}') ve yorum id ('{commentIdRaw}') sayısal olmalı.");
            return 1;
        }

        if (!Directory.Exists(workspace))
        {
            Console.Error.WriteLine($"HATA: Çalışma dizini bulunamadı: '{workspace}'");
            return 1;
        }

        var github = new GitHubService(githubToken);
        var llm = new LlmService(azureEndpoint, azureKey, azureDeployment);
        var report = new ReportService(github.Client);
        var commenter = new PrCommenter(github.Client);

        var analysisPipeline = new CiAnalysisPipeline(
            github, llm, report, ConsoleLogger.Create<CiAnalysisPipeline>());

        var fixPipeline = new FixPipeline(
            llm, new DotnetVerificationRunner(), ConsoleLogger.Create<FixPipeline>());

        var coordinator = new FixCoordinator(
            github, analysisPipeline, fixPipeline, commenter,
            ConsoleLogger.Create<FixCoordinator>());

        var result = await coordinator.RunAsync(new FixRequest
        {
            Owner = owner,
            Repo = repo,
            PullRequestNumber = prNumber,
            CommentId = commentId,
            CommentBody = body,
            AuthorAssociation = association,
            WorkspaceRoot = workspace
        });

        // Exit kodu bilerek her durumda 0: "yorum komut değildi", "yetki yok" ya da
        // "otomatik düzeltemedim" agent'ın hatası değil, normal sonuçlar. Kırmızı bir
        // job göstermek PR'da yanıltıcı olurdu - sonuç zaten yoruma yazılıyor.
        Console.WriteLine($"/fix sonucu: {result.Status}"
                        + (result.Fix is not null ? $" ({result.Fix.Status})" : "")
                        + (result.Pushed ? " — commit push edildi" : ""));
        return 0;
    }
}

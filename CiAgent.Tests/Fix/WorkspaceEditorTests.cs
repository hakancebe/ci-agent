using CiAgent.Core;

namespace CiAgent.Tests;

/// <summary>
/// Gerçekten diske yazan tek katman. Her test kendi geçici dizininde çalışıyor.
/// </summary>
public sealed class WorkspaceEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ciagent-test-" + Guid.NewGuid().ToString("N"));

    public WorkspaceEditorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temizlik hatası testi düşürmesin */ }
    }

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static CodeEdit Edit(string file, string oldText, string newText) =>
        new() { File = file, OldText = oldText, NewText = newText, Reason = "test" };

    [Fact]
    public async Task ApplyAsync_WritesChangeToDisk()
    {
        var path = Write("src/Calc.cs", "int Add(int a, int b) => a - b;");
        var editor = new WorkspaceEditor(_root);

        var outcome = await editor.ApplyAsync(Edit("src/Calc.cs", "a - b", "a + b"));

        Assert.True(outcome.Applied);
        Assert.Equal("int Add(int a, int b) => a + b;", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RevertAllAsync_RestoresEveryModifiedFile()
    {
        // Doğrulama başarısız olduğunda PR'ı yarım düzenlenmiş bırakmamak için şart.
        var a = Write("src/A.cs", "int X() => 1;");
        var b = Write("src/B.cs", "int Y() => 2;");
        var editor = new WorkspaceEditor(_root);

        await editor.ApplyAsync(Edit("src/A.cs", "=> 1", "=> 11"));
        await editor.ApplyAsync(Edit("src/B.cs", "=> 2", "=> 22"));
        Assert.Equal(2, editor.ModifiedFiles.Count);

        await editor.RevertAllAsync();

        Assert.Equal("int X() => 1;", await File.ReadAllTextAsync(a));
        Assert.Equal("int Y() => 2;", await File.ReadAllTextAsync(b));
        Assert.Empty(editor.ModifiedFiles);
    }

    [Fact]
    public async Task RevertAllAsync_RestoresOriginal_EvenWhenFileEditedTwice()
    {
        var path = Write("src/A.cs", "int X() => 1;");
        var editor = new WorkspaceEditor(_root);

        await editor.ApplyAsync(Edit("src/A.cs", "=> 1", "=> 2"));
        await editor.ApplyAsync(Edit("src/A.cs", "=> 2", "=> 3"));
        await editor.RevertAllAsync();

        // En başa dönmeli, ara adıma değil.
        Assert.Equal("int X() => 1;", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ApplyAsync_LeavesFileUntouched_WhenTextDoesNotMatch()
    {
        var path = Write("src/Calc.cs", "int Add(int a, int b) => a + b;");
        var editor = new WorkspaceEditor(_root);

        var outcome = await editor.ApplyAsync(Edit("src/Calc.cs", "olmayan metin", "yeni"));

        Assert.False(outcome.Applied);
        Assert.Contains("bulunamadı", outcome.RejectionReason);
        Assert.Equal("int Add(int a, int b) => a + b;", await File.ReadAllTextAsync(path));
        // Dosyaya dokunulmadığı için geri alınacak bir şey de yok.
        Assert.Empty(editor.ModifiedFiles);
    }

    [Fact]
    public async Task ApplyAsync_RejectsPolicyViolations_BeforeTouchingDisk()
    {
        var testFile = Write("CiAgent.Tests/CalcTests.cs", "Assert.Equal(5, sum);");
        var editor = new WorkspaceEditor(_root);

        var outcome = await editor.ApplyAsync(
            Edit("CiAgent.Tests/CalcTests.cs", "Assert.Equal(5, sum);", "Assert.True(true);"));

        // LLM'in testi zayıflatarak "düzeltme" girişimi diske hiç ulaşmamalı.
        Assert.False(outcome.Applied);
        Assert.Equal("Assert.Equal(5, sum);", await File.ReadAllTextAsync(testFile));
    }

    [Fact]
    public async Task ApplyAsync_RejectsPathEscapingWorkspace()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ciagent-outside-" + Guid.NewGuid().ToString("N") + ".cs");
        await File.WriteAllTextAsync(outside, "dokunulmamalı");
        try
        {
            var editor = new WorkspaceEditor(_root);

            var outcome = await editor.ApplyAsync(
                Edit($"../{Path.GetFileName(outside)}", "dokunulmamalı", "ele geçirildi"));

            Assert.False(outcome.Applied);
            Assert.Equal("dokunulmamalı", await File.ReadAllTextAsync(outside));
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_ForMissingOrOutOfBoundsPaths()
    {
        var editor = new WorkspaceEditor(_root);

        Assert.Null(await editor.ReadAsync("src/Yok.cs"));
        Assert.Null(await editor.ReadAsync("../../../etc/hosts"));
    }
}

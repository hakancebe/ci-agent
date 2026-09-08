using CiAgent.Core;

namespace CiAgent.Tests;

public class EditApplierTests
{
    private static CodeEdit Edit(string oldText, string newText) =>
        new() { File = "src/Calc.cs", OldText = oldText, NewText = newText, Reason = "test" };

    [Fact]
    public void Apply_ReplacesUniqueMatch()
    {
        const string file = "public int Add(int a, int b)\n{\n    return a - b;\n}";

        var (content, reason) = EditApplier.Apply(file, Edit("return a - b;", "return a + b;"));

        Assert.Null(reason);
        Assert.Equal("public int Add(int a, int b)\n{\n    return a + b;\n}", content);
    }

    [Fact]
    public void Apply_RejectsWhenTextNotFound()
    {
        var (content, reason) = EditApplier.Apply("bambaşka içerik", Edit("return a - b;", "return a + b;"));

        Assert.Null(content);
        Assert.Contains("bulunamadı", reason);
    }

    [Fact]
    public void Apply_RejectsAmbiguousMatch_RatherThanGuessing()
    {
        // Aynı satır iki metotta da geçiyor: hangisinin kastedildiği belirsiz.
        // Rastgele birini seçmek sessizce yanlış kodu değiştirmek olurdu.
        const string file = "int A() { return 0; }\nint B() { return 0; }";

        var (content, reason) = EditApplier.Apply(file, Edit("return 0;", "return 1;"));

        Assert.Null(content);
        Assert.Contains("birden fazla", reason);
    }

    [Fact]
    public void Apply_SucceedsOnAmbiguousLine_WhenGivenMoreContext()
    {
        // Yukarıdaki reddin çözümü: model daha uzun, benzersiz bir OldText versin.
        const string file = "int A() { return 0; }\nint B() { return 0; }";

        var (content, reason) = EditApplier.Apply(file, Edit("int B() { return 0; }", "int B() { return 1; }"));

        Assert.Null(reason);
        Assert.Equal("int A() { return 0; }\nint B() { return 1; }", content);
    }

    [Fact]
    public void Apply_MatchesAcrossLineEndingDifferences_AndPreservesFileStyle()
    {
        // Dosya CRLF, LLM'in verdiği metin LF. Eşleşme bozulmamalı, dosyanın
        // kendi satır sonu biçimi korunmalı (yoksa diff tüm dosyayı değişmiş gösterir).
        const string file = "class A\r\n{\r\n    int X() { return 1; }\r\n}";

        var (content, reason) = EditApplier.Apply(file, Edit("int X() { return 1; }", "int X() { return 2; }"));

        Assert.Null(reason);
        Assert.Contains("return 2;", content);
        Assert.Contains("\r\n", content);
        Assert.DoesNotContain("\n\n", content!.Replace("\r\n", ""));
    }

    [Fact]
    public void Apply_ReplacesMultiLineBlock()
    {
        const string file = "class A\n{\n    int X()\n    {\n        return 1;\n    }\n}";

        var (content, reason) = EditApplier.Apply(file,
            Edit("    int X()\n    {\n        return 1;\n    }", "    int X() => 2;"));

        Assert.Null(reason);
        Assert.Equal("class A\n{\n    int X() => 2;\n}", content);
    }
}

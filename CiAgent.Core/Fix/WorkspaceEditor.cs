namespace CiAgent.Core;

/// <summary>
/// Çalışma dizinindeki dosyaları okur/yazar ve yapılan değişiklikleri geri
/// alabilmek için orijinal içeriklerini saklar.
///
/// Geri alma şart: doğrulama başarısız olduğunda yarım bırakılmış bir düzenlemeyi
/// commit etmek, PR'ı bozuk bırakmak demek olurdu.
/// </summary>
public sealed class WorkspaceEditor
{
    private readonly string _root;
    private readonly Dictionary<string, string> _originals = new();

    public WorkspaceEditor(string workspaceRoot)
        => _root = Path.GetFullPath(workspaceRoot);

    public IReadOnlyCollection<string> ModifiedFiles => _originals.Keys;

    /// <summary>
    /// Yolu çalışma dizini içinde çözer. FixPolicy zaten ".." ve mutlak yolu
    /// reddediyor; bu ikinci kontrol savunma amaçlı - dosya sistemine yazan tek
    /// yer burası olduğu için sınır burada da doğrulanıyor.
    /// </summary>
    private string? ResolveInsideRoot(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relativePath));

        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;

        return full.StartsWith(rootWithSep, StringComparison.Ordinal) ? full : null;
    }

    public async Task<string?> ReadAsync(string relativePath)
    {
        var full = ResolveInsideRoot(relativePath);
        if (full is null || !File.Exists(full))
            return null;

        return await File.ReadAllTextAsync(full);
    }

    /// <summary>
    /// Tek bir değişikliği uygular. Sırasıyla: politika → dosya var mı → metin
    /// birebir ve benzersiz eşleşiyor mu. Herhangi biri tutmazsa dosyaya
    /// DOKUNULMAZ ve sebep döner.
    /// </summary>
    public async Task<EditOutcome> ApplyAsync(CodeEdit edit)
    {
        if (FixPolicy.RejectEdit(edit) is string policyProblem)
            return EditOutcome.Rejected(edit, policyProblem);

        var full = ResolveInsideRoot(edit.File);
        if (full is null)
            return EditOutcome.Rejected(edit, $"yol çalışma dizini dışına çıkıyor: '{edit.File}'");

        if (!File.Exists(full))
            return EditOutcome.Rejected(edit, $"dosya bulunamadı: '{edit.File}'");

        var current = await File.ReadAllTextAsync(full);
        var (updated, reason) = EditApplier.Apply(current, edit);

        if (updated is null)
            return EditOutcome.Rejected(edit, reason!);

        // İlk değişiklikten önceki hâli saklanıyor; aynı dosya birden çok kez
        // düzenlenirse ilk kayıt korunmalı ki geri alma en başa dönsün.
        if (!_originals.ContainsKey(edit.File))
            _originals[edit.File] = current;

        await File.WriteAllTextAsync(full, updated);
        return EditOutcome.Ok(edit);
    }

    /// <summary>Yapılan tüm değişiklikleri geri alır ve kaydı temizler.</summary>
    public async Task RevertAllAsync()
    {
        foreach (var (relativePath, original) in _originals)
        {
            var full = ResolveInsideRoot(relativePath);
            if (full is not null)
                await File.WriteAllTextAsync(full, original);
        }

        _originals.Clear();
    }
}

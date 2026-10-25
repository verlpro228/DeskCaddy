namespace DeskCaddy.Core.Library;

public sealed record OperationResult(bool Success, string Message, string? Path = null)
{
    public static OperationResult Ok(string message, string? path = null) => new(true, message, path);

    public static OperationResult Fail(string message) => new(false, message);
}

public sealed record ReconcileResult(IReadOnlyList<string> Added, IReadOnlyList<string> Removed)
{
    public bool HasChanges => Added.Count > 0 || Removed.Count > 0;
}

public sealed record BatchResult(IReadOnlyList<OperationResult> Results)
{
    public int Succeeded => Results.Count(r => r.Success);

    public int Failed => Results.Count(r => !r.Success);
}

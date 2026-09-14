namespace Game.Core.Style;

/// <summary>
/// The registered compilers, keyed by dialect. A pack names its dialect, so the compiler is
/// chosen from the pack rather than injected directly: injecting one compiler silently fixed
/// every pack to that compiler's dialect.
/// </summary>
public sealed class PromptCompilers
{
    private readonly IReadOnlyDictionary<PromptDialect, IPromptCompiler> _byDialect;

    public PromptCompilers(IEnumerable<IPromptCompiler> compilers)
    {
        ArgumentNullException.ThrowIfNull(compilers);
        _byDialect = compilers.ToDictionary(static c => c.Dialect);
    }

    public IPromptCompiler For(PromptDialect dialect) =>
        _byDialect.TryGetValue(dialect, out var compiler)
            ? compiler
            : throw new InvalidOperationException(
                $"No prompt compiler is registered for the {dialect} dialect. " +
                $"Registered: {string.Join(", ", _byDialect.Keys.Order())}.");
}

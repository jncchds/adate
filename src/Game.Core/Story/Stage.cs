namespace Game.Core.Story;

/// <summary>Someone standing on the stage, drawn at one expression.</summary>
/// <param name="Aesthetic">Their style, which picks the sprite's outfit; null for a scene saved before it was kept.</param>
/// <param name="Outfit">What they wear here, once the scene has said.</param>
/// <param name="SpritePath">Their picture at <paramref name="Expression"/>, once drawn.</param>
public sealed record SceneFigure(
    Guid CharacterId, string Name, string? Aesthetic, string Expression, Outfit? Outfit = null, string? SpritePath = null);

/// <summary>What the words say about someone at their end: that they are there, and how they look.</summary>
/// <param name="Id">Their id as the packet gives it.</param>
/// <param name="Expression">How they look, unchecked; null when the words did not say.</param>
public sealed record Presence(string Id, string? Expression);

/// <summary>Someone a scene is with, who could stand on its stage.</summary>
/// <param name="Resting">The expression they wear when nothing says otherwise.</param>
public sealed record StageCandidate(Guid Id, string Name, string? Aesthetic, string Resting);

/// <summary>
/// Who stands on the stage (user request: only those who are needed). Nobody is drawn before the words bring them
/// in, and someone the words have leave is drawn no longer. At most two by construction: an encounter is with the
/// person it is about and, at most, whoever the player brought along.
/// </summary>
public static class Stage
{
    /// <summary>The stage before any words: those there from the start, at their resting expressions.</summary>
    public static IReadOnlyList<SceneFigure> Opening(IReadOnlyList<StageCandidate> atFirst, IReadOnlyCollection<string> slots)
    {
        ArgumentNullException.ThrowIfNull(atFirst);

        return [.. atFirst.Select(c => new SceneFigure(c.Id, c.Name, c.Aesthetic, ScenePresentation.Expression(null, c.Resting, slots)))];
    }

    /// <summary>
    /// The stage once a block of words is shown. Who is there comes from <paramref name="present"/>, limited to
    /// <paramref name="with"/>; when the words said nothing usable (no answer, a failed writer, only unknown ids),
    /// the scene's own words bring in everyone it is with, and a later answer leaves the stage as it was.
    /// </summary>
    /// <param name="with">Everyone the scene is with, in the encounter's order.</param>
    /// <param name="shown">Who stood on the stage before these words, left to right.</param>
    /// <param name="present">Who the words say is there at their end; null when they said nothing.</param>
    /// <param name="owner">The person the scene is about, whose look the answer's single expression gives.</param>
    /// <param name="ownerExpression">That single expression, unchecked.</param>
    /// <param name="firstWords">Whether these are the scene's own words rather than an answer to the player.</param>
    public static IReadOnlyList<SceneFigure> After(
        IReadOnlyList<StageCandidate> with,
        IReadOnlyList<SceneFigure> shown,
        IReadOnlyList<Presence>? present,
        Guid? owner,
        string? ownerExpression,
        bool firstWords,
        IReadOnlyCollection<string> slots)
    {
        ArgumentNullException.ThrowIfNull(with);
        ArgumentNullException.ThrowIfNull(shown);

        var candidates = with.ToDictionary(c => c.Id.ToString(), StringComparer.OrdinalIgnoreCase);
        var said = (present ?? [])
            .Where(p => p is not null && candidates.ContainsKey(p.Id.Trim()))
            .DistinctBy(p => p.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => candidates[p.Id.Trim()].Id, p => p.Expression);

        // An answer that named nobody it could have is no answer; one that named nobody at all says everyone left.
        if (present is null || (said.Count == 0 && present.Count > 0))
        {
            said = (firstWords ? with.Select(c => c.Id) : shown.Select(f => f.CharacterId).Where(id => with.Any(c => c.Id == id)))
                .ToDictionary(id => id, _ => (string?)null);
        }

        // Those already standing keep their places; newcomers join in the encounter's order.
        var order = shown.Select(f => f.CharacterId).Where(said.ContainsKey)
            .Concat(with.Select(c => c.Id).Where(id => said.ContainsKey(id) && shown.All(f => f.CharacterId != id)));

        var figures = new List<SceneFigure>();
        foreach (var id in order)
        {
            var candidate = with.First(c => c.Id == id);
            var before = shown.FirstOrDefault(f => f.CharacterId == id);
            var written = said[id] ?? (id == owner ? ownerExpression : null);
            var expression = ScenePresentation.Expression(written, before?.Expression ?? candidate.Resting, slots);

            figures.Add(before is null
                ? new SceneFigure(id, candidate.Name, candidate.Aesthetic, expression)
                : before with { Aesthetic = before.Aesthetic ?? candidate.Aesthetic, Expression = expression, SpritePath = expression == before.Expression ? before.SpritePath : null });
        }

        return figures;
    }
}

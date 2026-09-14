using Game.Core.Characters;

namespace Game.Core.Style;

/// <summary>
/// Load-time rules for a subject's appearance choices. Appearance is picked from this
/// vocabulary rather than typed, so the vocabulary is the player's entire input surface and is
/// checked as such.
/// </summary>
internal static class FeatureVocabularyRules
{
    public static void Validate(string packId, string subject, SubjectProfile profile, IReadOnlyList<string> alwaysNegative)
    {
        foreach (var feature in AppearanceFeatures.All)
        {
            var options = profile.OptionsFor(feature);
            if (options.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Subject '{subject}' in style pack '{packId}' offers no '{feature}' choices. " +
                    "Appearance is chosen from pack vocabulary, so a feature with no options " +
                    "cannot be set at all.");
            }

            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in options)
            {
                if (string.IsNullOrWhiteSpace(option.Tag))
                {
                    throw new InvalidOperationException(
                        $"Subject '{subject}' in style pack '{packId}' has a blank '{feature}' choice.");
                }

                if (!tags.Add(option.Tag))
                {
                    throw new InvalidOperationException(
                        $"Subject '{subject}' in style pack '{packId}' lists '{option.Tag}' twice under '{feature}'.");
                }

                if (option.Prompt is not null && string.IsNullOrWhiteSpace(option.Prompt))
                {
                    throw new InvalidOperationException(
                        $"Subject '{subject}' in style pack '{packId}' gives the '{feature}' choice " +
                        $"'{option.Tag}' a blank prompt. Omit it to use the tag.");
                }

                foreach (var term in alwaysNegative)
                {
                    // Weighted entries repeat a plain term that is already checked.
                    if (term.StartsWith('('))
                    {
                        continue;
                    }

                    // The prompt wording is checked as well as the tag: it is what actually reaches
                    // the generator, and a harmless label must not smuggle in a negated term.
                    foreach (var words in new[] { option.Tag, option.Prompt })
                    {
                        if (words is not null && words.Contains(term, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                $"Subject '{subject}' in style pack '{packId}' offers '{option.Tag}' as a " +
                                $"'{feature}' choice whose wording '{words}' contains the always-negative term " +
                                $"'{term}'. A player must not be able to ask for what the pack negates at every ceiling.");
                        }
                    }
                }
            }

            var variable = AppearanceFeatures.Variable.Contains(feature, StringComparer.Ordinal);

            foreach (var option in options)
            {
                if (option.Near is null)
                {
                    continue;
                }

                foreach (var near in option.Near)
                {
                    if (!variable)
                    {
                        throw new InvalidOperationException(
                            $"Subject '{subject}' in style pack '{packId}' lists neighbours for the " +
                            $"'{feature}' choice '{option.Tag}', but '{feature}' is never varied. " +
                            "Remove them rather than leave vocabulary that reads as if it does something.");
                    }

                    if (!tags.Contains(near))
                    {
                        throw new InvalidOperationException(
                            $"Subject '{subject}' in style pack '{packId}' lists '{near}' as near " +
                            $"'{option.Tag}', but '{near}' is not one of its '{feature}' choices. An " +
                            "approved alternative has to be something the form could have chosen.");
                    }
                }
            }
        }

        // Cast vocabulary is authored rather than chosen by a player, but it reaches the prompt all
        // the same, so it meets the same bar.
        var castPhrases = (profile.CastFeatures ?? [])
            .Concat((profile.Aesthetics ?? new Dictionary<string, IReadOnlyList<string>>()).Values.SelectMany(static outfit => outfit))
            .Concat((profile.Wardrobe ?? new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>())
                .Values.SelectMany(static outfits => outfits.Values).SelectMany(static outfit => outfit));

        foreach (var phrase in castPhrases)
        {
            if (string.IsNullOrWhiteSpace(phrase))
            {
                throw new InvalidOperationException(
                    $"Subject '{subject}' in style pack '{packId}' has a blank cast feature or aesthetic outfit phrase.");
            }

            foreach (var term in alwaysNegative)
            {
                if (!term.StartsWith('(') && phrase.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Subject '{subject}' in style pack '{packId}' uses '{phrase}' for the cast, which contains the " +
                        $"always-negative term '{term}'.");
                }
            }
        }
    }
}

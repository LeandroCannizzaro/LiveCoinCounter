namespace CoinLabeler;

/// <summary>Combina i voti dei provider in un'etichetta per moneta e decide se l'immagine è "auto" o da rivedere.</summary>
public static class Consensus
{
    /// <param name="providerLabels">Nome di ogni provider, nello stesso ordine dei voti.</param>
    /// <param name="votes">votes[moneta][provider] = valore in centesimi, o null se la risposta non è utilizzabile.</param>
    /// <param name="wholeImageCounts">Per ogni provider i conteggi dell'immagine intera (null se la chiamata è fallita).</param>
    public static void Apply(LabelFile label, IReadOnlyList<string> providerLabels, int?[][] votes,
        IReadOnlyList<Dictionary<int, int>?> wholeImageCounts)
    {
        var allAgree = true;

        for (var i = 0; i < label.Coins.Count; i++)
        {
            var coin = label.Coins[i];
            var ballots = votes[i];
            coin.Votes = providerLabels.Select((name, p) => (name, vote: ballots[p])).ToDictionary(x => x.name, x => x.vote);

            var counted = ballots.Where(v => v.HasValue).GroupBy(v => v!.Value).OrderByDescending(g => g.Count()).ToList();
            coin.Label = counted.FirstOrDefault()?.Key;
            coin.Agreement = counted.Count == 1 && ballots.All(v => v.HasValue);
            coin.Confidence = coin.Label is null ? 0 : Math.Round((double)counted[0].Count() / ballots.Length, 2);
            if (coin.Agreement != true) allAgree = false;
        }

        // Per ogni provider i ritagli sommati devono dare gli stessi conteggi dell'immagine intera
        var countsMatch = true;
        for (var p = 0; p < providerLabels.Count; p++)
        {
            if (wholeImageCounts[p] is null) { countsMatch = false; continue; }
            var fromCrops = votes.Select(v => v[p]).Where(v => v.HasValue)
                .GroupBy(v => v!.Value).ToDictionary(g => g.Key, g => g.Count());
            if (!SameCounts(fromCrops, wholeImageCounts[p]!)) countsMatch = false;
        }

        label.TotalCents = label.Coins.Where(c => c.Label.HasValue).Sum(c => c.Label!.Value);
        label.Status = allAgree && countsMatch ? "auto" : "review";
    }

    private static bool SameCounts(Dictionary<int, int> a, Dictionary<int, int> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}

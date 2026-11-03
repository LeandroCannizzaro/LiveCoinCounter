using CoinLabeler;
using Xunit;

namespace CoinLabeler.Tests;

public class ConsensusTests
{
    private static LabelFile TwoCoins() => new()
    {
        Coins =
        {
            new CoinLabel { Bbox = new double[] { 0, 0, 10, 10 } },
            new CoinLabel { Bbox = new double[] { 20, 0, 10, 10 } }
        }
    };

    private static readonly string[] Two = { "a", "b" };

    [Fact]
    public void Providers_agreeing_and_matching_whole_image_counts_gives_auto()
    {
        var label = TwoCoins();
        var votes = new[] { new int?[] { 200, 200 }, new int?[] { 50, 50 } };
        var whole = new Dictionary<int, int>?[]
        {
            new() { [200] = 1, [50] = 1 },
            new() { [200] = 1, [50] = 1 }
        };

        Consensus.Apply(label, Two, votes, whole);

        Assert.Equal("auto", label.Status);
        Assert.Equal(250, label.TotalCents);
        Assert.All(label.Coins, c => Assert.True(c.Agreement));
        Assert.All(label.Coins, c => Assert.Equal(1.0, c.Confidence));
    }

    [Fact]
    public void Disagreement_on_one_coin_goes_to_review()
    {
        var label = TwoCoins();
        var votes = new[] { new int?[] { 200, 100 }, new int?[] { 50, 50 } };
        var whole = new Dictionary<int, int>?[] { new() { [200] = 1, [50] = 1 }, new() { [100] = 1, [50] = 1 } };

        Consensus.Apply(label, Two, votes, whole);

        Assert.Equal("review", label.Status);
        Assert.False(label.Coins[0].Agreement);
        Assert.True(label.Coins[1].Agreement);
        Assert.Equal(0.5, label.Coins[0].Confidence);
    }

    [Fact]
    public void Crop_votes_not_matching_whole_image_counts_goes_to_review()
    {
        var label = TwoCoins();
        var votes = new[] { new int?[] { 200, 200 }, new int?[] { 50, 50 } };
        var whole = new Dictionary<int, int>?[] { new() { [200] = 2 }, new() { [200] = 1, [50] = 1 } };

        Consensus.Apply(label, Two, votes, whole);

        Assert.Equal("review", label.Status);
    }

    [Fact]
    public void Unusable_crop_answer_counts_as_disagreement_and_is_not_summed()
    {
        var label = TwoCoins();
        var votes = new[] { new int?[] { 200, null }, new int?[] { 50, 50 } };
        var whole = new Dictionary<int, int>?[] { new() { [200] = 1, [50] = 1 }, new() { [200] = 1, [50] = 1 } };

        Consensus.Apply(label, Two, votes, whole);

        Assert.Equal("review", label.Status);
        Assert.False(label.Coins[0].Agreement);
        Assert.Equal(200, label.Coins[0].Label);
    }

    [Fact]
    public void Failed_whole_image_call_goes_to_review()
    {
        var label = TwoCoins();
        var votes = new[] { new int?[] { 200, 200 }, new int?[] { 50, 50 } };
        var whole = new Dictionary<int, int>?[] { null, new() { [200] = 1, [50] = 1 } };

        Consensus.Apply(label, Two, votes, whole);

        Assert.Equal("review", label.Status);
    }

    [Fact]
    public void Single_provider_is_auto_only_if_crops_match_whole_image()
    {
        var label = TwoCoins();
        var votes = new[] { new int?[] { 200 }, new int?[] { 50 } };

        Consensus.Apply(label, new[] { "a" }, votes, new Dictionary<int, int>?[] { new() { [200] = 1, [50] = 1 } });
        Assert.Equal("auto", label.Status);

        Consensus.Apply(label, new[] { "a" }, votes, new Dictionary<int, int>?[] { new() { [200] = 2 } });
        Assert.Equal("review", label.Status);
    }

    [Fact]
    public void Box_iou_is_one_for_identical_and_zero_for_disjoint()
    {
        Assert.Equal(1.0, new Box(0, 0, 10, 10).Iou(new Box(0, 0, 10, 10)), 6);
        Assert.Equal(0.0, new Box(0, 0, 10, 10).Iou(new Box(20, 20, 10, 10)), 6);
        Assert.Equal(25.0 / 175.0, new Box(0, 0, 10, 10).Iou(new Box(5, 5, 10, 10)), 6);
    }
}

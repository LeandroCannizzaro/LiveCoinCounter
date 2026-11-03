using LiveCoinCounter.Services.Recognition;
using Xunit;

namespace CoinLabeler.Tests;

public class YoloDecoderTests
{
    private const int Classes = 8;

    /// <summary>Costruisce un'uscita [12 x anchors] con le ancore indicate (cx, cy, w, h, classe, punteggio).</summary>
    private static float[] Output(int anchors, params (int anchor, float cx, float cy, float w, float h, int cls, float score)[] items)
    {
        var data = new float[(4 + Classes) * anchors];
        foreach (var (a, cx, cy, w, h, cls, score) in items)
        {
            data[a] = cx; data[anchors + a] = cy; data[2 * anchors + a] = w; data[3 * anchors + a] = h;
            data[(4 + cls) * anchors + a] = score;
        }
        return data;
    }

    [Fact]
    public void Decodes_center_box_into_top_left_and_best_class()
    {
        var output = Output(5, (2, 100, 50, 40, 20, 7, 0.9f));

        var result = Assert.Single(YoloDecoder.Decode(output, 5, Classes));

        Assert.Equal(7, result.ClassIndex);
        Assert.Equal(80, result.X);
        Assert.Equal(40, result.Y);
        Assert.Equal(40, result.Width);
        Assert.Equal(20, result.Height);
    }

    [Fact]
    public void Drops_low_confidence()
    {
        var output = Output(3, (0, 10, 10, 5, 5, 1, 0.2f));
        Assert.Empty(YoloDecoder.Decode(output, 3, Classes));
    }

    [Fact]
    public void Suppresses_overlapping_boxes_of_same_class_keeping_the_most_confident()
    {
        var output = Output(4, (0, 100, 100, 50, 50, 3, 0.6f), (1, 102, 101, 50, 50, 3, 0.9f));

        var result = Assert.Single(YoloDecoder.Decode(output, 4, Classes));

        Assert.Equal(0.9f, result.Confidence);
    }

    [Fact]
    public void Keeps_adjacent_coins_of_same_class()
    {
        var output = Output(4, (0, 100, 100, 50, 50, 3, 0.9f), (1, 160, 100, 50, 50, 3, 0.8f));
        Assert.Equal(2, YoloDecoder.Decode(output, 4, Classes).Count);
    }

    [Fact]
    public void Rejects_wrong_output_size()
    {
        Assert.Throws<ArgumentException>(() => YoloDecoder.Decode(new float[10], 5, Classes));
    }

    [Fact]
    public void Letterbox_maps_boxes_back_to_original_image()
    {
        // immagine 1280x640 in un quadrato di 640: scala 0,5, bande sopra e sotto di 160 px
        var letterbox = Letterbox.For(1280, 640, 640);
        Assert.Equal(0.5f, letterbox.Scale);
        Assert.Equal(0f, letterbox.PadX);
        Assert.Equal(160f, letterbox.PadY);

        var back = letterbox.ToImage(new Detection(0, 1, 100, 200, 50, 50));
        Assert.Equal(200f, back.X);
        Assert.Equal(80f, back.Y);
        Assert.Equal(100f, back.Width);
    }

    [Fact]
    public void ToTensor_is_channel_first_rgb()
    {
        var tensor = Letterbox.ToTensor(new[] { unchecked((int)0xFF2040FF), unchecked((int)0xFF000000) });

        Assert.Equal(6, tensor.Length);
        Assert.Equal(0x20 / 255f, tensor[0]);      // R pixel 0
        Assert.Equal(0x40 / 255f, tensor[2]);      // G pixel 0
        Assert.Equal(1f, tensor[4]);               // B pixel 0
        Assert.Equal(0f, tensor[1]);               // R pixel 1
    }
}

public class DetectionQualityTests
{
    [Fact]
    public void Confident_separate_coins_have_no_warning()
    {
        var detections = new[] { new Detection(7, 0.9f, 0, 0, 50, 50), new Detection(6, 0.8f, 100, 0, 50, 50) };
        Assert.Null(DetectionQuality.Warning(detections));
    }

    [Fact]
    public void Low_confidence_is_reported()
    {
        var detections = new[] { new Detection(7, 0.9f, 0, 0, 50, 50), new Detection(6, 0.4f, 100, 0, 50, 50) };
        Assert.Contains("poco riconoscibile", DetectionQuality.Warning(detections));
    }

    [Fact]
    public void Overlap_of_different_classes_is_reported()
    {
        var detections = new[] { new Detection(7, 0.9f, 0, 0, 50, 50), new Detection(6, 0.9f, 5, 5, 50, 50) };
        Assert.Contains("si sovrappongono", DetectionQuality.Warning(detections));
    }

    [Fact]
    public void Agnostic_nms_removes_same_coin_counted_with_two_classes()
    {
        var kept = YoloDecoder.NonMaxSuppression(
            new List<Detection> { new(7, 0.9f, 0, 0, 50, 50), new(6, 0.6f, 2, 2, 50, 50) }, 0.6f, classAgnostic: true);
        Assert.Single(kept);
        Assert.Equal(7, kept[0].ClassIndex);
    }
}

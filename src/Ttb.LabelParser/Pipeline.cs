using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Ttb.LabelParser;

public sealed record LabelCrop(string ImageId, int Page, double[] Bbox);
public sealed record PreparationResult(string JsonPath, int LabelCount, GeminiArtifacts Gemini);

public static class Pipeline
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    static readonly string[] LabelFields =
    [
        "brandName", "classTypeDesignation", "alcoholContent", "netContents",
        "bottlerProducerNameAndAddress", "countryOfOrigin", "governmentWarning",
        "importerNameAndAddress"
    ];

    // The generated fixtures place two 234 x 259.2 point labels at (33,52) and
    // (327,52) on a 612 x 1008 point page. Bboxes use top-left coordinates.
    static readonly List<LabelCrop> MockFormCrops =
    [
        new("label-01", 1, [33, 696.8, 267, 956]),
        new("label-02", 1, [327, 696.8, 561, 956])
    ];

    public static PreparationResult Prepare(string inputPdf, string outputDirectory, string? requestedName, string? cropFile, bool sendGemini)
    {
        if (!File.Exists(inputPdf)) throw new FileNotFoundException("Input PDF was not found.", inputPdf);
        if (!string.Equals(Path.GetExtension(inputPdf), ".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input must be a PDF file.");

        string outputName = string.IsNullOrWhiteSpace(requestedName)
            ? Path.GetFileNameWithoutExtension(inputPdf)
            : requestedName;
        if (outputName != Path.GetFileName(outputName) || outputName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("--name must be a file name without a directory.");

        Directory.CreateDirectory(outputDirectory);
        string copiedPdf = Path.Combine(outputDirectory, outputName + ".pdf");
        if (!string.Equals(Path.GetFullPath(inputPdf), Path.GetFullPath(copiedPdf), StringComparison.OrdinalIgnoreCase))
            File.Copy(inputPdf, copiedPdf, overwrite: true);

        JsonObject fields;
        JsonObject evidence;
        double pageWidth;
        double pageHeight;
        using (var document = PdfDocument.Open(inputPdf))
        {
            (fields, evidence) = new FormReader(document).Extract();
            var firstPage = document.GetPage(1);
            pageWidth = firstPage.Width;
            pageHeight = firstPage.Height;
        }

        List<LabelCrop> candidates = cropFile is null
            ? MockFormCrops
            : JsonSerializer.Deserialize<List<LabelCrop>>(File.ReadAllText(cropFile), JsonOptions)
                ?? throw new ArgumentException("Crop file must contain a JSON array.");
        ValidateCrops(candidates, pageWidth, pageHeight);
        var assets = ExportLabels(inputPdf, outputDirectory, outputName, candidates);

        var pendingFields = new JsonObject();
        foreach (string key in LabelFields)
            pendingFields[key] = new JsonObject { ["raw"] = null, ["status"] = "pending_api" };

        var source = new JsonObject
        {
            ["fileName"] = Path.GetFileName(copiedPdf),
            ["originalFileName"] = Path.GetFileName(inputPdf),
            ["sha256"] = Sha256(copiedPdf),
            ["formRevision"] = "04/2023"
        };
        var extraction = new JsonObject { ["status"] = "not_sent", ["fields"] = pendingFields };
        var record = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["parserVersion"] = typeof(Pipeline).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            ["sourcePdf"] = source,
            ["applicationFields"] = fields,
            ["formFieldEvidence"] = evidence,
            ["labelExtraction"] = extraction,
            ["labelImages"] = assets
        };

        string jsonPath = Path.Combine(outputDirectory, outputName + ".json");
        var gemini = GeminiRequest.Prepare(outputDirectory, outputName, source, assets, sendGemini);
        extraction["status"] = gemini.Status switch
        {
            "sent" => "sent_response_saved",
            "not_sent_no_images" => "not_sent_no_images",
            _ => "not_sent"
        };
        File.WriteAllText(jsonPath, record.ToJsonString(JsonOptions) + Environment.NewLine);
        return new PreparationResult(jsonPath, assets.Count, gemini);
    }

    static void ValidateCrops(IEnumerable<LabelCrop> crops, double pageWidth, double pageHeight)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var crop in crops)
        {
            if (string.IsNullOrWhiteSpace(crop.ImageId) || !ids.Add(crop.ImageId))
                throw new ArgumentException("Crop IDs must be unique and nonempty.");
            if (crop.Page != 1) throw new ArgumentException("This parser currently supports label crops on page 1.");
            if (crop.Bbox is not { Length: 4 }) throw new ArgumentException("Each crop bbox must contain four numbers.");
            double left = crop.Bbox[0], top = crop.Bbox[1], right = crop.Bbox[2], bottom = crop.Bbox[3];
            if (crop.Bbox.Any(v => !double.IsFinite(v)) || left < 0 || top < 0 || right > pageWidth || bottom > pageHeight || left >= right || top >= bottom)
                throw new ArgumentException("Crop is outside the first PDF page.");
        }
    }

    static JsonArray ExportLabels(string pdfPath, string outputDirectory, string outputName, IReadOnlyList<LabelCrop> crops)
    {
        const int dpi = 300;
        var assets = new JsonArray();
        int outputIndex = 0;
        foreach (var crop in crops)
        {
            float left = (float)crop.Bbox[0], top = (float)crop.Bbox[1];
            float width = (float)(crop.Bbox[2] - crop.Bbox[0]), height = (float)(crop.Bbox[3] - crop.Bbox[1]);
            var options = new RenderOptions
            {
                Dpi = dpi,
                Bounds = new RectangleF(left, top, width, height),
                WithAnnotations = true,
                WithFormFill = true,
                DpiRelativeToBounds = true
            };
            using var pdf = File.OpenRead(pdfPath);
            using SKBitmap bitmap = Conversion.ToImage(pdf, crop.Page - 1, false, null, options);
            if (IsBlank(bitmap)) continue;

            outputIndex++;
            string imageName = $"{outputName}-label-{outputIndex:00}.png";
            string imagePath = Path.Combine(outputDirectory, imageName);
            using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using (var destination = File.Create(imagePath)) encoded.SaveTo(destination);
            assets.Add(new JsonObject
            {
                ["imageId"] = $"label-{outputIndex:00}",
                ["file"] = imageName,
                ["page"] = crop.Page,
                ["bboxPointsTopLeft"] = Node(crop.Bbox),
                ["dpi"] = dpi,
                ["widthPixels"] = bitmap.Width,
                ["heightPixels"] = bitmap.Height,
                ["mimeType"] = "image/png",
                ["sha256"] = Sha256(imagePath),
                ["method"] = "rendered PDF region"
            });
        }
        return assets;
    }

    static bool IsBlank(SKBitmap bitmap)
    {
        long sampled = 0, nonWhite = 0;
        int stride = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 300);
        for (int y = 0; y < bitmap.Height; y += stride)
            for (int x = 0; x < bitmap.Width; x += stride)
            {
                SKColor c = bitmap.GetPixel(x, y);
                sampled++;
                if (c.Alpha > 16 && (c.Red < 245 || c.Green < 245 || c.Blue < 245)) nonWhite++;
            }
        return sampled == 0 || (double)nonWhite / sampled < 0.001;
    }

    static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static JsonArray Node(IEnumerable<double> values) =>
        new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}

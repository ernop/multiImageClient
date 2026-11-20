using System.Reflection;

using SkiaSharp;

namespace MultiImageClient;

public sealed class ContactSheetCellTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mic-sheet-cells-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(".jpg", 1600, 3200)]
    [InlineData(".png", 1000, 2000)]
    [InlineData(".png", 800, 1200)]
    public void TallResultsShrinkToTheCellAndKeepTheirOriginalSizeLabel(string extension, int width, int height)
    {
        var path = Path.Combine(_root, "result" + extension);
        using (var bitmap = Raster.Create(width, height, SKColors.SteelBlue))
        {
            File.WriteAllBytes(path, extension == ".jpg" ? Raster.EncodeJpeg(bitmap, 90) : Raster.EncodePng(bitmap));
        }
        var result = new TaskProcessResult { IsSuccess = true, ImageGeneratorDescription = "test" };
        result.SetSavedRawImagePath(0, path);

        var load = typeof(ImageCombiner).GetMethod("LoadResultImages", BindingFlags.NonPublic | BindingFlags.Static)!;
        var cells = ((IEnumerable<ImageCombiner.LoadedImage>)load.Invoke(null, [new[] { result }])!).ToList();
        try
        {
            var cell = Assert.Single(cells);
            Assert.True(cell.Success);
            Assert.Equal($"{width}x{height}", cell.SourceResolutionLabel);
            Assert.Equal(height > 1300 ? 1024 : height, cell.Height);
            Assert.Equal(cell.Height, cell.Image!.Height);
        }
        finally
        {
            foreach (var cell in cells) cell.Image?.Dispose();
        }
    }
}

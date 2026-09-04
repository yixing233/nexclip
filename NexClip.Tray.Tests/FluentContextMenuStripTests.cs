using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using NexClip.Tray;
using Svg;

namespace NexClip.Tray.Tests;

public sealed class FluentContextMenuStripTests
{
    [Fact]
    public void MenuKeepsSymmetricOuterPaddingAfterItemsAreAdded()
    {
        using var menu = new FluentContextMenuStrip();
        using var item = new FluentMenuItem("打开剪贴板", null, null);

        menu.Items.Add(item);
        menu.PerformLayout();

        Assert.Equal(new Padding(4, 6, 4, 6), menu.Padding);
    }

    [Fact]
    public void MenuItemsStretchToTheClientRightEdge()
    {
        using var menu = new FluentContextMenuStrip
        {
            AutoSize = false,
            Size = new Size(228, 100)
        };
        using var item = new FluentMenuItem("打开剪贴板", null, null)
        {
            AutoSize = false,
            Size = new Size(220, FluentMenuLayout.ItemHeight)
        };

        menu.Items.Add(item);
        menu.PerformLayout();

        Assert.Equal(menu.ClientSize.Width - item.Margin.Right, item.Bounds.Right);
    }

    [Fact]
    public void HoverAndTextColumnsKeepStableInsets()
    {
        var itemSize = new Size(228, FluentMenuLayout.ItemHeight);
        var background = FluentMenuLayout.GetItemBackgroundBounds(itemSize);
        var shortcut = FluentMenuLayout.GetShortcutBounds(itemSize, shortcutWidth: 34);
        var text = FluentMenuLayout.GetTextBounds(itemSize, shortcutWidth: 34);

        Assert.Equal(4, background.Left);
        Assert.Equal(4, itemSize.Width - background.Right);
        Assert.Equal(FluentMenuLayout.ContentRight, itemSize.Width - shortcut.Right);
        Assert.Equal(FluentMenuLayout.ShortcutGap, shortcut.Left - text.Right);
    }

    [Theory]
    [InlineData("Clipboard", "clipboard.svg")]
    [InlineData("Refresh", "refresh-cw.svg")]
    [InlineData("Settings", "settings.svg")]
    [InlineData("Restart", "rotate-cw.svg")]
    [InlineData("Exit", "x.svg")]
    public void TrayIconMatchesOfficialLucideSvg(string iconName, string assetName)
    {
        var iconType = typeof(TrayManager).GetNestedType("TrayIconType", BindingFlags.NonPublic);
        var drawIcon = typeof(TrayManager).GetMethod("DrawLucideIcon", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(iconType);
        Assert.NotNull(drawIcon);

        var icon = Enum.Parse(iconType!, iconName);
        const int iconSize = 16;
        using var actual = Assert.IsType<Bitmap>(drawIcon!.Invoke(null, [icon, Color.Black, iconSize]));

        var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "lucide", assetName);
        var document = SvgDocument.FromSvg<SvgDocument>(File.ReadAllText(assetPath));
        document.Color = new SvgColourServer(Color.Black);
        using var expected = document.Draw(iconSize, iconSize);

        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Assert.True(
                    expected.GetPixel(x, y).ToArgb() == actual.GetPixel(x, y).ToArgb(),
                    $"{iconName} differs from {assetName} at ({x}, {y}).");
            }
        }
    }
}

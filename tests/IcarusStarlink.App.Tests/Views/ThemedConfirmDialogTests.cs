using IcarusStarlink.App.Views;

namespace IcarusStarlink.App.Tests.Views;

public sealed class ThemedConfirmDialogTests
{
    [Fact]
    public void CopyText_LaysOutTitleMessageAndButtonsLikeTheWindowsMessageBox()
    {
        var text = ThemedConfirmDialog.CopyText("Delete mod", "Delete Better Bows?\nThis can't be undone.", ["No", "Yes"]);

        Assert.Equal(
            "---------------------------\r\n"
            + "Delete mod\r\n"
            + "---------------------------\r\n"
            + "Delete Better Bows?\r\nThis can't be undone.\r\n"
            + "---------------------------\r\n"
            + "No   Yes   \r\n"
            + "---------------------------\r\n",
            text);
    }
}

using System.IO;
using System.Threading;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Deed.Desktop;
using Deed.Core;

namespace Deed.Desktop.Tests;

public sealed class WindowSmokeTests
{
    [Fact]
    public void AuthoringControlsBuildAPlayableDrawing()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                TextBox Text(string name) => (TextBox)window.FindName(name)!;
                ComboBox Combo(string name) => (ComboBox)window.FindName(name)!;
                Button Button(string name) => (Button)window.FindName(name)!;
                void Click(string name) => Button(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Text("RecordedCall").Text = "north 100 feet";
                Text("Bearing").Text = "N";
                Text("RecordedDistance").Text = "100";
                Click("AddRootButton");
                var table = (DataGrid)window.FindName("CourseTable")!;
                Assert.Single(table.Items);
                for (int i = 0; i < 3; i++)
                {
                    Text("RecordedCall").Text = $"east {i + 1}";
                    Text("Bearing").Text = "E";
                    Text("RecordedDistance").Text = "25";
                    Click("AddChildButton");
                }
                Assert.Equal(4, table.Items.Count);
                for (int i = 0; i < 3; i++)
                {
                    Combo("AlongHost").SelectedIndex = 0;
                    Text("AlongDistance").Text = "10";
                    Click("AddAlongButton");
                }
                Combo("ParentCourse").SelectedIndex = 0;
                Combo("StartPoint").SelectedIndex = 3;
                Text("RecordedCall").Text = "branch from middle";
                Text("Bearing").Text = "W";
                Text("RecordedDistance").Text = "15";
                Click("AddChildButton");
                Assert.Equal(5, table.Items.Count);
                window.Left = -2000;
                window.Top = -2000;
                window.ShowInTaskbar = false;
                window.Show();
                window.Measure(new Size(1500, 900));
                window.Arrange(new Rect(0, 0, 1500, 900));
                window.UpdateLayout();
                var screenshot = Environment.GetEnvironmentVariable("DEED_SCREENSHOT_PATH");
                if (!string.IsNullOrWhiteSpace(screenshot))
                {
                    var bitmap = new RenderTargetBitmap(1500, 900, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(screenshot);
                    encoder.Save(stream);
                }

                var deletedRow = table.Items[4];
                table.SelectedItem = deletedRow;
                var deletedId = IdOf(deletedRow);
                var session = (WorkspaceSession)typeof(MainWindow)
                    .GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(window)!;
                var recordId = RecordId.TryParse("rec-00001").Value;
                Assert.True(session.Apply(p => ProjectEdits.DeleteCourse(p, recordId, deletedId)).IsSuccess);
                typeof(MainWindow).GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, new object?[] { null });
                var fallbackId = IdOf(table.SelectedItem!);
                Assert.Equal(IdOf(table.Items[3]), fallbackId);
                Assert.Equal(fallbackId, ((DrawingSurface)window.FindName("Drawing")!).SelectedCourseId);
                Assert.Equal(fallbackId, IdOf(Combo("ParentCourse").SelectedItem!));
                Assert.Equal(fallbackId, IdOf(Combo("AlongHost").SelectedItem!));
                Assert.Equal("east 3", Text("RecordedCall").Text);
                Assert.True(HasGoldLine((DrawingSurface)window.FindName("Drawing")!));
                window.Hide();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
            throw new Xunit.Sdk.XunitException("WPF smoke-test UI thread did not exit within 15 seconds.");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static CourseId IdOf(object row) => (CourseId)row.GetType()
        .GetProperty("Id")!.GetValue(row)!;

    private static bool HasGoldLine(DrawingSurface drawing)
    {
        int width = (int)Math.Ceiling(drawing.ActualWidth);
        int height = (int)Math.Ceiling(drawing.ActualHeight);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(drawing);
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] < 20 && pixels[i + 1] >= 205 && pixels[i + 2] >= 245)
                return true;
        return false;
    }

}

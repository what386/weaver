using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Weaver.ViewModels;

namespace Weaver.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Enable drag and drop
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        var files = e.DataTransfer.TryGetFiles();
        if (files != null)
        {
            var paths = files.Select(f => f.Path.LocalPath).ToList();
            if (paths.Count > 0)
            {
                await vm.LoadFilesFromPathsAsync(paths);
            }
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Only allow file drops
        if (e.DataTransfer.TryGetFiles() != null)
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }
}

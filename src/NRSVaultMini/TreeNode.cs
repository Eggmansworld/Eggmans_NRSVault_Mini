using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using NRSVaultMini.Core;

namespace NRSVaultMini;

/// <summary>A folder or ROM row in the collection tree. The DataGrid shows a flattened list of visible nodes.</summary>
public sealed class TreeNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public static readonly IBrush GreenBrush = new SolidColorBrush(Color.Parse("#C8EBC9"));
    public static readonly IBrush OrangeBrush = new SolidColorBrush(Color.Parse("#FFDDAA"));
    public static readonly IBrush RedBrush = new SolidColorBrush(Color.Parse("#FFB3B3"));
    public static readonly IBrush NoneBrush = Brushes.Transparent;
    public static readonly IBrush GreenFolderBrush = new SolidColorBrush(Color.Parse("#A9DCAB"));
    public static readonly IBrush OrangeFolderBrush = new SolidColorBrush(Color.Parse("#FFCB80"));
    public static readonly IBrush RedFolderBrush = new SolidColorBrush(Color.Parse("#FF9090"));

    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public TreeNode? Parent { get; init; }
    public int Depth { get; init; }
    public RowState? Row { get; init; }
    public bool IsFolder => Row == null;
    public List<TreeNode> Children { get; } = new();

    // Folder aggregates
    public int Total, Ok, Errors, Missing, Issues;

    private bool _expanded;
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph)));
        }
    }

    public FontWeight Weight => IsFolder ? FontWeight.SemiBold : FontWeight.Normal;

    /// <summary>Tells bound rows that every displayed value may have changed (after a scan).</summary>
    public void NotifyAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        foreach (var c in Children) c.NotifyAll();
    }

    public Thickness Indent => new(Depth * 14, 0, 0, 0);
    public string Glyph => IsFolder ? (IsExpanded ? "▾" : "▸") : "";
    public string Icon => IsFolder ? "📁" : "▫";

    public string StatusText => IsFolder
        ? (Total == 0 ? "" : $"{Ok:N0} / {Total:N0} OK")
        : RowState.Text(Row!.Status);

    public string SizeText => IsFolder ? "" : Row!.ShownSize.ToString("N0");
    public string Crc => IsFolder ? "" : Row!.ShownCrc;
    public string Sha1 => IsFolder ? "" : Row!.ShownSha1;
    public string Mapper => IsFolder ? "" : Row!.Row.Entry.MapperText;
    public string Note => IsFolder
        ? (Ok + Missing + Issues + Errors == 0 || Ok == Total ? "" : $"{Missing:N0} missing, {Issues:N0} other issues" + (Errors > 0 ? $", {Errors:N0} errors" : ""))
        : Row!.Note;

    public StatusColor StatusColor => IsFolder
        ? (Errors > 0 ? StatusColor.Red : Ok == Total && Total > 0 ? StatusColor.Green : Ok + Missing + Issues + Errors == 0 ? StatusColor.None : StatusColor.Orange)
        : Row!.Color;

    public IBrush RowBrush => (StatusColor, IsFolder) switch
    {
        (StatusColor.Green, false) => GreenBrush,
        (StatusColor.Orange, false) => OrangeBrush,
        (StatusColor.Red, false) => RedBrush,
        (StatusColor.Green, true) => GreenFolderBrush,
        (StatusColor.Orange, true) => OrangeFolderBrush,
        (StatusColor.Red, true) => RedFolderBrush,
        _ => NoneBrush,
    };

    public void Aggregate()
    {
        Total = Ok = Errors = Missing = Issues = 0;
        foreach (var c in Children)
        {
            if (c.IsFolder)
            {
                c.Aggregate();
                Total += c.Total; Ok += c.Ok; Errors += c.Errors; Missing += c.Missing; Issues += c.Issues;
            }
            else
            {
                Total++;
                switch (c.Row!.Status)
                {
                    case RowStatus.Ok: Ok++; break;
                    case RowStatus.Error: Errors++; break;
                    case RowStatus.Missing: Missing++; break;
                    case RowStatus.NotScanned: break;
                    default: Issues++; break;
                }
            }
        }
    }
}

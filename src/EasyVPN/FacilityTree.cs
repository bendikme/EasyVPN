using System.Windows;

namespace EasyVPN;

/// <summary>A line in a list of facilities: a section's title, a group, a facility, or a message.</summary>
public abstract class ListRow : ObservableObject
{
    /// <summary>False for titles and messages: the keyboard and the mouse pass them by.</summary>
    public virtual bool IsSelectable => true;

    /// <summary>A group's row is a button of its own, which opens and closes it.</summary>
    public virtual bool IsGroup => false;
}

/// <summary>A section's title, such as FAVOURITES.</summary>
public sealed class SectionRow(string title) : ListRow
{
    public string Title { get; } = title;
    public override bool IsSelectable => false;
}

/// <summary>Something said in place of rows: nothing found, more than shown.</summary>
public sealed class MessageRow : ListRow
{
    private string _text = "";

    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    public override bool IsSelectable => false;
}

/// <summary>A group of facilities, open or closed, with how many it holds.</summary>
public sealed class GroupRow(string path) : ListRow
{
    private int _count;
    private int _depth;
    private bool _open;
    private bool _hasActive;

    /// <summary>Its place in the tree, the names of the groups it is in and its own joined by '/'.</summary>
    public string Path { get; } = path;

    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    public override bool IsGroup => true;

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public int Depth
    {
        get => _depth;
        set
        {
            if (SetProperty(ref _depth, value)) OnPropertyChanged(nameof(Indent));
        }
    }

    public Thickness Indent => new(_depth * FacilityTree.IndentStep, 0, 0, 0);

    public bool IsOpen
    {
        get => _open;
        set => SetProperty(ref _open, value);
    }

    /// <summary>The facility connected is in it: shown on a closed group, so it is not lost.</summary>
    public bool HasActive
    {
        get => _hasActive;
        set => SetProperty(ref _hasActive, value);
    }
}

/// <summary>A facility in a list, at its depth there: the same facility can be in several sections.</summary>
public sealed class FacilityRow(FacilityViewModel facility) : ListRow
{
    private int _depth;

    public FacilityViewModel Facility { get; } = facility;

    public int Depth
    {
        get => _depth;
        set
        {
            if (SetProperty(ref _depth, value)) OnPropertyChanged(nameof(Indent));
        }
    }

    public Thickness Indent => new(_depth * FacilityTree.IndentStep, 0, 0, 0);
}

/// <summary>
/// Facilities under their groups, as the window's list and the tray menu show them. A group is
/// the facility's own (nested with '/') or else its customer's. Closed groups say how many they
/// hold; a search opens every group with a match in it and shows only the matches.
/// </summary>
public static class FacilityTree
{
    public const double IndentStep = 14;

    /// <summary>Up to this many facilities, groups start open; with more, closed - a list of a hundred Tosibox Locks is not read in full.</summary>
    public const int OpenUpTo = 15;

    private sealed class Node(string path)
    {
        public string Path { get; } = path;
        public SortedDictionary<string, Node> Groups { get; } = new(StringComparer.CurrentCultureIgnoreCase);
        public List<FacilityViewModel> Facilities { get; } = [];
        public int Count => Facilities.Count + Groups.Values.Sum(g => g.Count);
        public bool HasActive => Facilities.Any(f => f.IsActive) || Groups.Values.Any(g => g.HasActive);
    }

    /// <summary>Adds the rows for <paramref name="facilities"/> to <paramref name="rows"/>.</summary>
    /// <param name="isOpen">Whether a group (by its path) is open; while searching, every group is.</param>
    /// <param name="group">The row for a group's path, kept from one build to the next.</param>
    /// <param name="facility">The row for a facility, kept from one build to the next.</param>
    /// <param name="limit">At most this many facilities; the rest are counted in <paramref name="left"/>.</param>
    public static void Append(
        List<ListRow> rows,
        IEnumerable<FacilityViewModel> facilities,
        bool searching,
        Func<string, bool> isOpen,
        Func<string, GroupRow> group,
        Func<FacilityViewModel, FacilityRow> facility,
        int limit,
        out int left)
    {
        var root = new Node("");
        foreach (var item in facilities)
        {
            var node = root;
            foreach (var name in item.GroupNames)
            {
                var path = node.Path.Length == 0 ? name : $"{node.Path}/{name}";
                if (!node.Groups.TryGetValue(name, out var next)) node.Groups[name] = next = new Node(path);
                node = next;
            }
            node.Facilities.Add(item);
        }

        var shown = 0;
        var skipped = 0;
        Emit(root, 0);
        left = skipped;

        void Emit(Node node, int depth)
        {
            foreach (var child in node.Groups.Values)
            {
                var row = group(child.Path);
                row.Depth = depth;
                row.Count = child.Count;
                row.HasActive = child.HasActive;
                row.IsOpen = searching || isOpen(child.Path);
                rows.Add(row);
                if (row.IsOpen) Emit(child, depth + 1);
            }
            foreach (var item in node.Facilities.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (shown >= limit)
                {
                    skipped++;
                    continue;
                }
                var row = facility(item);
                row.Depth = depth;
                rows.Add(row);
                shown++;
            }
        }
    }
}
